# BEefyER

BEefyER runs realtime ASR on a separate Linux machine and connects **outward** to
BEefy. The home machine needs no incoming Internet port. BEefy streams captured
audio over an authenticated WSS connection; BEefyER returns partial transcripts,
speech endpoints, and the completed turn transcript. BEefy continues to handle
transcript correction, command selection, and robot replies.

The default is **Nemotron Speech Streaming English 0.6B Q8_0**, using NVIDIA's
native CPU runtime. On a Ryzen 7 5700U, start with four CPU inference threads and
one active remote turn. The cache-aware RNNT right-context setting of `1` selects
the native 160 ms streaming preset. Measure actual latency on your machine using
the benchmark below; no performance result for that CPU is assumed.

## Configure BEefy

The feature is disabled until enabled. Add these variables to BEefy's existing
environment, using a newly generated secret shared only with BEefyER:

```dotenv
OpenJibo__Stt__BeefyEr__Enabled=true
OpenJibo__Stt__BeefyEr__SharedSecret=REPLACE_WITH_GENERATED_SECRET
OpenJibo__Stt__BeefyEr__MaxConcurrentTurns=1
OpenJibo__Stt__BeefyEr__FinalTimeout=00:00:03
```

Generate the secret after installing the worker's Python dependencies:

```sh
/opt/BEefyER/.venv/bin/python /opt/BEefyER/worker.py --generate-secret
```

Restart BEefy after updating configuration. Enabling BEefyER also enables model
endpointing and the existing local Sherpa provider as an outage backup. Existing
Sherpa model paths and tuning still apply. Synthetic transcript fixtures keep
their original priority.

BEefy's HTTPS reverse proxy must forward `/beefyer/connect` to the same .NET
process as robot requests, including the Authorization header and WebSocket
upgrade. For the existing loopback proxy on port 24667, an nginx location inside
the HTTPS virtual host can use:

```nginx
location = /beefyer/connect {
    proxy_pass http://127.0.0.1:24667;
    proxy_http_version 1.1;
    proxy_set_header Upgrade $http_upgrade;
    proxy_set_header Connection "upgrade";
    proxy_set_header Host $host;
    proxy_set_header X-Forwarded-Proto $scheme;
    proxy_read_timeout 60s;
}
```

BEefy accepts the forwarded HTTPS scheme from its existing trusted loopback
proxies. For a proxy on a different host, configure that specific trusted proxy
in BEefy hosting rather than accepting arbitrary forwarded headers. Worker
connections over HTTP receive 426; missing or incorrect worker credentials
receive 401. Robot credentials do not grant worker access.

## Install on the home Linux server

Copy this `BEefyER/` directory to `/opt/BEefyER`. These example commands use
Debian/Ubuntu and a dedicated service account:

```sh
sudo apt-get update
sudo apt-get install -y python3 python3-venv build-essential cmake ninja-build git pkg-config libsentencepiece-dev
sudo useradd --system --user-group --home-dir /var/lib/beefyer --create-home beefyer
sudo chown -R beefyer:beefyer /opt/BEefyER
sudo -u beefyer sh /opt/BEefyER/setup.sh
```

Confirm `cmake --version` is **3.26 or newer**. If your distribution ships an
older version, install a newer CMake before building. Python **3.11 or newer**
is required. Build the pinned runtime and download the indexed English Q8 model
into the same cache the service will use:

```sh
sudo -u beefyer mkdir -p /var/lib/beefyer/.cache
sudo -u beefyer env XDG_CACHE_HOME=/var/lib/beefyer/.cache sh /opt/BEefyER/build-runtime.sh
```

The runtime is pinned to NVIDIA/NeMo-Speech.cpp commit
`e59dfb0b5c22655620f41391cc9a000d6f56b802`, with its pinned submodules. That
revision's `nemotron-en` index pins the model revision and verifies its size and
SHA-256. The model download is approximately 700 MB. The CPU build needs no CUDA
or Radeon GPU support. Installation builds only the ASR and HTTP components.

Copy `beefyer.env.example` to `/etc/beefyer.env`. Set your actual public BEefy
hostname and the generated secret, matching BEefy's configuration exactly:

```dotenv
BEEFYER_BEEFY_URL=wss://api.example.com/beefyer/connect
BEEFYER_SHARED_SECRET=REPLACE_WITH_GENERATED_SECRET
BEEFYER_RUNTIME_BIN=/opt/BEefyER/runtime/bin/nemo-speech
BEEFYER_MODEL=nemotron-en
BEEFYER_THREADS=4
BEEFYER_CAPACITY=1
```

Then install and start the service:

```sh
sudo chown root:beefyer /etc/beefyer.env
sudo chmod 640 /etc/beefyer.env
sudo cp /opt/BEefyER/beefyer.service /etc/systemd/system/beefyer.service
sudo systemctl daemon-reload
sudo systemctl enable --now beefyer
sudo journalctl -u beefyer -f
```

The worker starts and supervises a resident `nemo-speech serve` process on
`127.0.0.1:8089`. It advertises readiness only after the native server opens a
recognition session with a loaded model. A runtime process exit causes systemd
to restart the worker. To manage that native process separately, start the
worker with `--no-start-runtime` and use the same loopback realtime endpoint.

BEefyER verifies BEefy's TLS certificate and hostname. There is no insecure TLS
switch. For a private certificate authority, install its CA certificate in the
home server's system trust store. The shared secret is sent in an authorization
header, never in a URL or command argument.

## Behaviour and tuning

- One authenticated worker connection serves every robot on a BEefy instance.
  Each turn has an independent recognition stream. A second worker connection
  is rejected while the first remains connected.
- Partial transcripts are provisional. Native speech endpoints preserve prior
  segments; BEefy's existing completeness rules decide whether to keep listening.
  A command executes only after turn finalization.
- Audio is incrementally decoded to 16 kHz mono PCM16. WebSocket queues are
  bounded. If the worker is busy, disconnected, overloaded, or fails to return a
  final result within three seconds, BEefy uses retained audio for local ASR.
  Old results cannot complete a canceled or replaced turn.
- Reconnect delays grow from one to thirty seconds, with jitter. Reconnection
  makes the worker available for subsequent turns; it does not take over an
  existing local turn.
- `BEEFYER_MODEL` accepts the indexed model name or a local compatible streaming
  GGUF path. `BEEFYER_THREADS` controls inference threads, not HTTP workers.
  `BEEFYER_CAPACITY` and BEefy's `MaxConcurrentTurns` both limit active remote
  turns; the smaller limit wins. Increase capacity only after measuring CPU load.
- Disable offloading by setting `OpenJibo__Stt__BeefyEr__Enabled=false` and
  restarting BEefy. Local ASR and existing robot protocols remain available.

`GET /beefyer/status` on BEefy's HTTPS host accepts the same bearer header and
reports connection readiness, model identity, capacity, active turns, completed
turns, fallback count, first partial latency, final drain latency, and the latest
reported processing backlog. It does not include transcripts or credentials.
BEefy's normal STT telemetry records the selected provider and model.

## Benchmark on your Ryzen server

While BEefyER's service is running, replay a representative robot recording as
PCM16 mono 16 kHz WAV:

```sh
/opt/BEefyER/.venv/bin/python /opt/BEefyER/benchmark.py command.wav \
    --reference "set a timer for five minutes"
```

The replay is paced at capture speed and appends one second of silence. JSON
output reports the actual transcript, word error rate when a reference is
provided, first partial latency, processing backlog, whether processing caught
up, endpoint-to-final latency, and commit-to-final drain latency. The local
benchmark isolates home-server inference; BEefy's status metrics additionally
include the network path. Aim for sustained processing faster than capture and
a final drain below one second. Test quiet audio, Jibo's name, short commands,
longer speech, and pauses within commands. Tune threads and concurrency using
these measurements rather than treating published benchmarks as your results.

Run automated tests without downloading model weights:

```sh
python3 -m pip install -r BEefyER/requirements.txt
python3 -B -m unittest discover -s tests/python -p 'test_beefyer.py'
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj --filter 'FullyQualifiedName~BeefyErTests|FullyQualifiedName~ModelEndpointingFinalizationTests'
```

The native streaming API and build instructions are documented upstream in
[NVIDIA's API reference](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/e59dfb0b5c22655620f41391cc9a000d6f56b802/docs/api.md)
and [build guide](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/e59dfb0b5c22655620f41391cc9a000d6f56b802/docs/build.md).
