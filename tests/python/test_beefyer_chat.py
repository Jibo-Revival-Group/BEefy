"""Offline chat protocol tests; only loopback fixture HTTP/ASR traffic."""
import asyncio
import contextlib
import json
import pathlib
import struct
import sys
import unittest
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / "BEefyER"))
from worker import Config, WorkerConnection, config_from_env
from test_beefyer import FakeBeefy
from aiohttp import web
from websockets.asyncio.server import serve


class ChatTests(unittest.IsolatedAsyncioTestCase):
    async def fixture(self, handler):
        app = web.Application()
        app.router.add_post("/api/chat", handler)
        runner = web.AppRunner(app, access_log=None)
        await runner.setup()
        site = web.TCPSite(runner, "127.0.0.1", 0)
        await site.start()
        self.addAsyncCleanup(runner.cleanup)
        port = site._server.sockets[0].getsockname()[1]
        # Test-only transport seam, never a config or incoming-envelope field.
        patcher = patch("worker.CHAT_URL", f"http://127.0.0.1:{port}/api/chat", create=True)
        patcher.start()
        self.addCleanup(patcher.stop)

    async def test_chat_terminal_does_not_fail_full_asr_queue(self):
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, start_runtime=False)
        owner = WorkerConnection(config, FakeBeefy(), "generation")
        for _ in range(128):
            owner.send("partial", streamId="a" * 32, text="asr")
        owner.send("chat_result", requestId="b" * 32, text="ok")
        self.assertFalse(owner.failed.is_set())
        self.assertEqual(owner.outgoing.qsize(), 128)

    async def test_cancelled_chat_retains_lease_until_terminal_queued(self):
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, start_runtime=False)
        config.chat_enabled = True
        owner = WorkerConnection(config, FakeBeefy(), "generation")
        for _ in range(128):
            owner.send("chat_error", requestId="b" * 32, code="busy")
        started = asyncio.Event()
        async def chat(request_id, messages):
            started.set()
            await asyncio.Event().wait()
        owner.chat = chat
        event = {"version": 1, "generation": "generation", "type": "chat_start",
                 "requestId": "a" * 32, "messages": [{"role": "user", "content": "hi"}]}
        owner.start_chat(event, json.dumps(event))
        await started.wait()
        owner.chats["a" * 32].cancel()
        await asyncio.sleep(0)
        await asyncio.sleep(0)
        self.assertIn("a" * 32, owner.chats)
        self.assertFalse(owner.failed.is_set())
        owner.chat_outgoing.get_nowait()
        await asyncio.sleep(0)
        await asyncio.sleep(0)
        self.assertNotIn("a" * 32, owner.chats)
        frames = [json.loads(owner.chat_outgoing.get_nowait()) for _ in range(128)]
        self.assertEqual(frames[-1]["type"], "chat_cancelled")

    async def test_completed_identity_expires_and_capacity_recovers(self):
        now = [1000.0]
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, start_runtime=False)
        config.chat_enabled = True
        owner = WorkerConnection(config, FakeBeefy(), "generation")
        async def chat(request_id, messages):
            return "chat_result", {"text": "ok"}
        owner.chat = chat
        def start(rid):
            event = {"version": 1, "generation": "generation", "type": "chat_start",
                     "requestId": rid, "messages": [{"role": "user", "content": "hi"}]}
            owner.start_chat(event, json.dumps(event))
        with patch("worker.time") as clock, patch("worker.MAX_CHAT_IDENTITIES", 1):
            clock.monotonic.side_effect = lambda: now[0]
            start("a" * 32)
            await asyncio.sleep(0)
            await asyncio.sleep(0)
            now[0] += 299
            start("b" * 32)
            self.assertEqual(json.loads(owner.chat_outgoing.get_nowait())["type"], "chat_result")
            self.assertEqual(json.loads(owner.chat_outgoing.get_nowait())["code"], "busy")
            now[0] += 2
            start("b" * 32)
            self.assertIn("b" * 32, owner.chats)
            self.assertLessEqual(len(owner.chat_ids), 1)
            await asyncio.sleep(0)
            await asyncio.sleep(0)

    async def test_replay_window_starts_after_delayed_cleanup(self):
        now = [1000.0]
        release = asyncio.Event()
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, start_runtime=False)
        config.chat_enabled = True
        owner = WorkerConnection(config, FakeBeefy(), "generation")
        async def chat(request_id, messages):
            await release.wait()
            return "chat_result", {"text": "ok"}
        owner.chat = chat
        def start(rid):
            event = {"version": 1, "generation": "generation", "type": "chat_start",
                     "requestId": rid, "messages": [{"role": "user", "content": "hi"}]}
            owner.start_chat(event, json.dumps(event))
        with patch("worker.time") as clock, patch("worker.MAX_CHAT_IDENTITIES", 2):
            clock.monotonic.side_effect = lambda: now[0]
            start("a" * 32)
            now[0] += 1000
            with self.assertRaisesRegex(ValueError, "Reused"):
                start("a" * 32)
            start("b" * 32)
            self.assertEqual(json.loads(owner.chat_outgoing.get_nowait())["code"], "busy")
            self.assertIn("a" * 32, owner.chats)
            release.set()
            await asyncio.sleep(0)
            await asyncio.sleep(0)
            now[0] += 299
            with self.assertRaisesRegex(ValueError, "Reused"):
                start("a" * 32)
            now[0] += 1
            start("a" * 32)
            self.assertIn("a" * 32, owner.chats)
            await asyncio.sleep(0)
            await asyncio.sleep(0)

    async def test_replay_identity_memory_is_bounded_without_eviction(self):
        requests = []
        async def handler(request):
            requests.append(await request.json())
            return web.json_response({"message": {"content": "must not run"}})
        await self.fixture(handler)
        with patch("worker.MAX_CHAT_IDENTITIES", 4, create=True):
            socket, owner, task, ready = await self.launch(True)
            owner.chat_ids.update(f"{i:032x}" for i in range(4))
            for rid in ("f" * 32, "0" * 32, "f" * 32):
                socket.event("chat_start", requestId=rid, messages=[{"role": "user", "content": "hi"}])
                result = await socket.result("chat_error")
                self.assertEqual((result["requestId"], result["code"]), (rid, "busy"))
            self.assertEqual(owner.chat_ids, {f"{i:032x}" for i in range(4)})
            self.assertEqual(requests, [])
            self.assertFalse(task.done())

    async def test_loopback_chat_returns_correlated_fixed_model_result(self):
        requests = []
        async def handler(request):
            requests.append(await request.json())
            return web.json_response({"message": {"role": "assistant", "content": "hello"}, "done": True})
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(True)
        socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "hi"}])
        result = await socket.result("chat_result")
        self.assertEqual(result, {"version": 1, "generation": "generation", "type": "chat_result",
                                  "requestId": "a" * 32, "text": "hello"})
        self.assertEqual(requests, [{"model": "gemma3:4b", "messages": [{"role": "user", "content": "hi"}], "stream": False}])

    async def launch(self, enabled=None, generation="generation"):
        config = Config("wss://example.invalid/beefyer/connect", "s" * 43, start_runtime=False)
        if enabled is not None:
            config.chat_enabled = enabled
        socket = FakeBeefy()
        owner = WorkerConnection(config, socket, generation)
        task = asyncio.create_task(owner.run("ASR fixture"))
        self.addAsyncCleanup(self.stop, task)
        ready = await socket.result("ready")
        return socket, owner, task, ready

    async def test_rejects_invalid_requests_without_http_or_disconnect(self):
        requests = []
        async def handler(request):
            requests.append(await request.json())
            return web.json_response({"message": {"content": "unreachable"}})
        await self.fixture(handler)
        good = [{"role": "user", "content": "hi"}]
        cases = [[], None, "bad", [{"role": "tool", "content": "x"}],
                 [{"role": "user", "content": 3}], [{"role": "assistant", "content": "x"}],
                 good + good, good + [{"role": "system", "content": "x"}],
                 [{"role": "system", "content": "x"}],
                 [{"role": "user", "content": ""}],
                 [{"role": "user", "content": "x", "images": []}],
                 [{"role": "user", "content": "界" * 11000}],
                 ([{"role": "user", "content": "x"}, {"role": "assistant", "content": "y"}] * 11) + good]
        socket, owner, task, ready = await self.launch(True)
        for messages in cases:
            with self.subTest(messages=repr(messages)[:80]):
                socket.event("chat_start", requestId="a" * 32, messages=messages)
                error = await socket.result("chat_error")
                self.assertEqual(error["code"], "invalid_request")
        for key in ("url", "model", "upstream", "stream"):
            socket.event("chat_start", requestId="a" * 32, messages=good, **{key: "forbidden"})
            self.assertEqual((await socket.result("chat_error"))["code"], "invalid_request")
        self.assertEqual(requests, [])
        socket.event("ping")
        await socket.result("pong")

    async def test_disabled_chat_is_rejected_without_http(self):
        requests = []
        async def handler(request):
            requests.append(await request.read())
            return web.Response(status=500)
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(False)
        socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "hi"}])
        self.assertEqual((await socket.result("chat_error"))["code"], "chat_disabled")
        self.assertEqual(requests, [])

    async def test_malformed_request_ids_fail_connection_without_reflection(self):
        for rid in (None, 3, "A" * 32, "z" * 32, "a" * 31):
            socket, owner, task, ready = await self.launch(True)
            socket.event("chat_start", requestId=rid, messages=[{"role": "user", "content": "hi"}])
            with self.assertRaises(ExceptionGroup):
                await asyncio.wait_for(asyncio.shield(task), 1)
            self.assertTrue(socket.output.empty())

    async def stop(self, task):
        task.cancel()
        with contextlib.suppress(asyncio.CancelledError, ExceptionGroup):
            await task

    async def test_upstream_failures_are_bounded_sanitized_errors(self):
        mode = [None]
        async def handler(request):
            kind = mode[0]
            if kind == "status":
                return web.Response(status=500, text="PRIVATE_UPSTREAM")
            if kind == "redirect":
                return web.Response(status=302, headers={"Location": "http://public.invalid/api/chat"})
            if kind == "json":
                return web.Response(text="PRIVATE_UPSTREAM")
            if kind == "body":
                return web.Response(body=b"x" * 65537)
            content = {"long": "x" * 12001, "frame": "\x00" * 12000,
                       "unicode": "😀" * 12000, "type": 42, "blank": " "}.get(kind)
            if kind == "shape":
                return web.json_response([])
            return web.json_response({"message": {"content": content}})
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(True)
        for index, kind in enumerate(("status", "redirect", "json", "body", "long", "frame", "unicode", "type", "shape", "blank")):
            mode[0] = kind
            rid = f"{index:032x}"
            socket.event("chat_start", requestId=rid, messages=[{"role": "user", "content": "PRIVATE_PROMPT"}])
            event = await socket.result("chat_error")
            self.assertEqual(event["requestId"], rid)
            self.assertEqual(event["code"], "inference_failure")
            self.assertNotIn("PRIVATE", json.dumps(event))

    async def test_chat_http_deadline_is_120_seconds(self):
        from worker import CHAT_TIMEOUT
        self.assertEqual(CHAT_TIMEOUT, 120)

    async def test_http_total_timeout_releases_capacity(self):
        gate = asyncio.Event()
        async def handler(request):
            await gate.wait()
            return web.json_response({"message": {"content": "late"}})
        await self.fixture(handler)
        self.addCleanup(gate.set)
        with patch("worker.CHAT_TIMEOUT", .05, create=True):
            socket, owner, task, ready = await self.launch(True)
            socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "hi"}])
            self.assertEqual((await socket.result("chat_error"))["code"], "timeout")
            await asyncio.sleep(0)
            self.assertFalse(owner.chats)
        gate.set()

    async def blocked_fixture(self):
        entered, closed = asyncio.Event(), asyncio.Event()
        requests = []
        async def handler(request):
            requests.append(await request.json())
            entered.set()
            transport = request.transport
            while not transport.is_closing():
                await asyncio.sleep(.005)
            closed.set()
            return web.Response()
        await self.fixture(handler)
        return entered, closed, requests

    async def test_busy_ping_cancel_cleanup_and_late_cancel(self):
        entered, closed, requests = await self.blocked_fixture()
        socket, owner, task, ready = await self.launch(True)
        messages = [{"role": "user", "content": "hi"}]
        socket.event("chat_start", requestId="a" * 32, messages=messages)
        await asyncio.wait_for(entered.wait(), 1)
        socket.event("chat_start", requestId="b" * 32, messages=messages)
        self.assertEqual((await socket.result("chat_error"))["code"], "busy")
        socket.event("ping")
        await asyncio.wait_for(socket.result("pong"), .5)
        socket.event("chat_cancel", requestId="a" * 32)
        await asyncio.wait_for(closed.wait(), 1)
        self.assertEqual((await socket.result("chat_cancelled"))["requestId"], "a" * 32)
        async with asyncio.timeout(1):
            while owner.chats:
                await asyncio.sleep(.005)
        socket.event("chat_cancel", requestId="a" * 32)
        socket.event("chat_cancel", requestId="c" * 32)
        socket.event("ping")
        await socket.result("pong")
        self.assertEqual(len(requests), 1)
        self.assertTrue(socket.output.empty())

    async def test_disconnect_closes_http_and_cannot_reply_to_successor_generation(self):
        entered, closed, requests = await self.blocked_fixture()
        socket, owner, task, ready = await self.launch(True)
        socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "hi"}])
        await asyncio.wait_for(entered.wait(), 1)
        # Invalid generation drives the same TaskGroup finally path as a disconnect.
        socket.input.put_nowait(json.dumps({"version": 1, "generation": "stale", "type": "ping"}))
        with self.assertRaises(ExceptionGroup):
            await asyncio.wait_for(task, 1)
        await asyncio.wait_for(closed.wait(), 1)
        self.assertFalse(owner.chats)
        successor, new_owner, new_task, _ = await self.launch(True, "successor")
        await asyncio.sleep(.02)
        self.assertTrue(successor.output.empty())
        self.assertTrue(socket.output.empty())

    async def test_reused_request_id_is_protocol_violation(self):
        async def handler(request):
            return web.json_response({"message": {"content": "ok"}})
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(True)
        for _ in range(2):
            socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "hi"}])
            if _ == 0:
                await socket.result("chat_result")
            else:
                with self.assertRaises(ExceptionGroup):
                    await asyncio.wait_for(task, 1)

    async def test_ready_advertises_fixed_chat_only_when_explicitly_enabled(self):
        for enabled in (None, False, True):
            socket, owner, task, ready = await self.launch(enabled)
            if enabled:
                self.assertEqual(ready.get("chatModel"), "gemma3:4b")
                self.assertEqual(ready.get("chatCapacity"), 1)
            else:
                self.assertNotIn("chatModel", ready)
                self.assertNotIn("chatCapacity", ready)
            await self.stop(task)
        with patch.dict("os.environ", {"BEEFYER_CHAT_ENABLED": "true"}, clear=True):
            import types
            self.assertTrue(config_from_env(types.SimpleNamespace(no_start_runtime=True)).chat_enabled)

    async def test_asr_partials_and_final_continue_while_chat_is_generating(self):
        entered, closed, requests = await self.blocked_fixture()
        async def runtime(socket):
            await socket.send(json.dumps({"type": "session.created"}))
            await socket.recv()
            await socket.send(json.dumps({"type": "session.updated"}))
            async for raw in socket:
                if isinstance(raw, bytes):
                    await socket.send(json.dumps({"type": "conversation.item.input_audio_transcription.delta", "delta": "ASR live"}))
                else:
                    await socket.send(json.dumps({"type": "input_audio_buffer.committed"}))
        server = await serve(runtime, "127.0.0.1", 0)
        self.addCleanup(server.close)
        socket, owner, task, ready = await self.launch(True)
        owner.config.runtime_url = f"ws://127.0.0.1:{server.sockets[0].getsockname()[1]}/v1/audio/transcriptions/realtime"
        socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "hi"}])
        await asyncio.wait_for(entered.wait(), 1)
        sid = "b" * 32
        socket.event("start", streamId=sid, sessionId="test", turnId="test", sequence=0, sampleRate=16000)
        socket.input.put_nowait(bytes.fromhex(sid) + struct.pack("<q", 1) + b"\0\0" * 1600)
        self.assertEqual((await socket.result("partial"))["text"], "ASR live")
        socket.event("finish", streamId=sid, sessionId="test", turnId="test", sequence=1)
        self.assertEqual((await socket.result("final"))["text"], "ASR live")
        self.assertIn("a" * 32, owner.chats)
        socket.event("chat_cancel", requestId="a" * 32)
        await asyncio.wait_for(closed.wait(), 1)
        await self.stop(task)
        server.close()
        await server.wait_closed()

    async def test_exact_request_message_and_reply_limits_are_accepted(self):
        async def handler(request):
            return web.json_response({"message": {"content": "x" * 12000}})
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(True)
        messages = [{"role": "system", "content": "s"}]
        messages += [{"role": "user" if n % 2 == 0 else "assistant", "content": "x"} for n in range(21)]
        event = {"version": 1, "generation": "generation", "type": "chat_start", "requestId": "a" * 32, "messages": messages}
        messages[-1]["content"] += "x" * (32000 - len(json.dumps(event).encode()))
        raw = json.dumps(event)
        self.assertEqual(len(raw.encode()), 32000)
        socket.input.put_nowait(raw)
        result = await socket.result("chat_result")
        self.assertEqual(len(result["text"]), 12000)
        self.assertLessEqual(len(json.dumps(result).encode()), 65536)
        event["requestId"] = "b" * 32
        messages[-1]["content"] += "x"
        socket.input.put_nowait(json.dumps(event))
        self.assertEqual((await socket.result("chat_error"))["code"], "invalid_request")

    async def test_repeated_cancel_keeps_capacity_until_http_cleanup_finishes(self):
        import aiohttp
        entered, closed, requests = await self.blocked_fixture()
        cleanup_started, release = asyncio.Event(), asyncio.Event()
        original = aiohttp.ClientSession.close
        async def delayed_close(session):
            cleanup_started.set()
            try:
                await release.wait()
            finally:
                await original(session)
        self.addCleanup(release.set)
        with patch.object(aiohttp.ClientSession, "close", delayed_close):
            socket, owner, task, ready = await self.launch(True)
            messages = [{"role": "user", "content": "hi"}]
            socket.event("chat_start", requestId="a" * 32, messages=messages)
            await asyncio.wait_for(entered.wait(), 1)
            socket.event("chat_cancel", requestId="a" * 32)
            await asyncio.wait_for(cleanup_started.wait(), 1)
            socket.event("chat_cancel", requestId="a" * 32)
            socket.event("ping")
            self.assertEqual((await asyncio.wait_for(socket.output.get(), 1))["type"], "pong")
            self.assertTrue(socket.output.empty(), "ack arrived before HTTP cleanup completed")
            try:
                self.assertIn("a" * 32, owner.chats)
                socket.event("chat_start", requestId="b" * 32, messages=messages)
                self.assertEqual((await socket.result("chat_error"))["code"], "busy")
            finally:
                release.set()
            ack = await asyncio.wait_for(socket.result("chat_cancelled"), 1)
            self.assertEqual(ack["requestId"], "a" * 32)
            self.assertNotIn("a" * 32, owner.chats)
            await asyncio.wait_for(closed.wait(), 1)
            await self.stop(task)

    async def test_utf8_request_bound_also_holds_for_local_http_payload(self):
        sizes = []
        async def handler(request):
            sizes.append(len(await request.read()))
            return web.json_response({"message": {"content": "ok"}})
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(True)
        event = {"version": 1, "generation": "generation", "type": "chat_start", "requestId": "a" * 32,
                 "messages": [{"role": "user", "content": "界" * 10000}]}
        socket.input.put_nowait(json.dumps(event, ensure_ascii=False))
        await socket.result("chat_result")
        self.assertLessEqual(sizes[0], 32000)

    async def test_cancel_before_http_starts_releases_slot_without_request(self):
        requests = []
        async def handler(request):
            requests.append(await request.json())
            return web.json_response({"message": {"content": "unexpected"}})
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(True)
        socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "hi"}])
        socket.event("chat_cancel", requestId="a" * 32)
        socket.event("ping")
        await socket.result("pong")
        async with asyncio.timeout(1):
            while owner.chats:
                await asyncio.sleep(.005)
        self.assertEqual(requests, [])
        self.assertEqual((await socket.result("chat_cancelled"))["requestId"], "a" * 32)
        self.assertTrue(socket.output.empty())

    async def test_http_connection_loss_is_sanitized_without_logging(self):
        async def handler(request):
            await request.read()
            request.transport.abort()
            return web.Response(text="PRIVATE_UPSTREAM")
        await self.fixture(handler)
        socket, owner, task, ready = await self.launch(True)
        with self.assertNoLogs("BEefyER"):
            socket.event("chat_start", requestId="a" * 32, messages=[{"role": "user", "content": "PRIVATE_PROMPT"}])
            error = await socket.result("chat_error")
        self.assertEqual(error["code"], "inference_failure")
        self.assertNotIn("PRIVATE", json.dumps(error))

    async def test_actual_websocket_close_closes_active_http(self):
        from websockets.asyncio.client import connect
        entered, closed, requests = await self.blocked_fixture()
        async def backend(socket):
            ready = json.loads(await socket.recv())
            self.assertEqual(ready["chatCapacity"], 1)
            await socket.send(json.dumps({"version": 1, "generation": "generation", "type": "chat_start",
                                          "requestId": "a" * 32, "messages": [{"role": "user", "content": "hi"}]}))
            await asyncio.wait_for(entered.wait(), 1)
            await socket.close()
        async with serve(backend, "127.0.0.1", 0) as server:
            port = server.sockets[0].getsockname()[1]
            config = Config("wss://example.invalid/beefyer/connect", "s" * 43, chat_enabled=True)
            async with connect(f"ws://127.0.0.1:{port}") as socket:
                owner = WorkerConnection(config, socket, "generation")
                with self.assertRaises(ExceptionGroup):
                    await asyncio.wait_for(owner.run("ASR fixture"), 2)
            await asyncio.wait_for(closed.wait(), 1)
            self.assertFalse(owner.chats)
            self.assertTrue(owner.closed)

    async def test_serialized_frames_never_exceed_transport_bound(self):
        socket, owner, task, ready = await self.launch(True)
        owner.send("chat_error", requestId="a" * 32, code="x" * 65536)
        self.assertTrue(owner.failed.is_set())
        self.assertTrue(owner.outgoing.empty())
