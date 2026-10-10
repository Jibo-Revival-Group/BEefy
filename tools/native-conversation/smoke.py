#!/usr/bin/env python3
"""Exercise a published BEefy over real sockets, using only the Python standard library."""
import argparse, base64, hashlib, json, os, socket, struct, time, urllib.request
parser = argparse.ArgumentParser()
parser.add_argument('--port', type=int, default=29467)
args = parser.parse_args()
url = f'http://127.0.0.1:{args.port}'
assert json.load(urllib.request.urlopen(url + '/health'))['ok']

def turn(text, skill=None):
    connection = socket.create_connection(('127.0.0.1', args.port), timeout=10)
    key = base64.b64encode(os.urandom(16)).decode()
    connection.sendall((f'GET /listen HTTP/1.1\r\nHost: localhost:{args.port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n').encode())
    response = b''
    while not response.endswith(b'\r\n\r\n'): response += connection.recv(1)
    assert b'101 Switching Protocols' in response, response
    def send(message):
        data = json.dumps(message).encode(); mask = os.urandom(4)
        header = bytes([0x81, 0x80 | len(data)]) if len(data) < 126 else bytes([0x81, 0xfe]) + struct.pack('!H', len(data))
        connection.sendall(header + mask + bytes(c ^ mask[i % 4] for i, c in enumerate(data)))
    def read(n):
        data = b''
        while len(data) < n:
            part = connection.recv(n - len(data))
            if not part: raise EOFError()
            data += part
        return data
    send({'type': 'LISTEN', 'transID': 'native-smoke', 'data': {'hotphrase': True, 'rules': ['launch']}})
    send({'type': 'CLIENT_ASR', 'transID': 'native-smoke', 'data': {'text': text}})
    messages = []; deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        first, second = read(2); size = second & 127
        if size == 126: size = struct.unpack('!H', read(2))[0]
        elif size == 127: size = struct.unpack('!Q', read(8))[0]
        payload = read(size)
        if first & 15 == 8: raise AssertionError('Socket closed before command response')
        if first & 15 != 1: continue
        message = json.loads(payload); messages.append(message)
        if skill and message.get('type') == 'LISTEN':
            assert message['data']['match']['skillID'] == skill, message
            assert message['transID'] == 'native-smoke', message
        if message.get('type') == 'SKILL_ACTION' or skill and any(m.get('type') == 'LISTEN' for m in messages) and any(m.get('type') == 'EOS' for m in messages): break
    connection.close()
    assert any(m.get('type') == 'LISTEN' for m in messages), messages
    if skill: assert not any(m.get('type') in ('SKILL_ACTION', 'SKILL_REDIRECT') for m in messages), messages
    print(text, '->', [m.get('type') for m in messages])
    return messages
turn('open main menu', '@be/main-menu')
turn('do yoga', '@be/exercise')
hue = turn('make living room lights red', '@be/hue-control')
entities = next(m for m in hue if m.get('type') == 'LISTEN')['data']['nlu']['entities']
assert entities['group'] == 'living' and entities['color'] == 'red'
weather = turn('what is the weather')
assert any(m.get('type') == 'SKILL_ACTION' for m in weather)
print('Published native health, local launches, Hue parameters, and unavailable weather provider passed.')
