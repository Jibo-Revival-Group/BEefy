# BEefyER

BEefyER runs realtime ASR on a separate Linux machine and connects **outward** to
BEefy. The home machine needs no incoming Internet port. BEefy streams captured
audio over an authenticated WSS connection; BEefyER returns partial transcripts,
speech endpoints, and the completed turn transcript. BEefy continues to handle
transcript correction, command selection, and robot replies.

The ASR engine is **Nemotron Speech Streaming English 0.6B**, using NVIDIA's
native CPU runtime. CPU inference threads default to hardware-aware auto:
up to eight threads, respecting CPU affinity and container CPU quotas. Explicit
thread settings remain available. The cache-aware RNNT right-context setting of `6` selects
the accuracy-focused 560 ms streaming preset; `1` selects 160 ms. Measure actual latency on your machine using
the benchmark below; no performance result for that CPU is assumed.

`BEEFYER_MODEL=auto` selects a prepared **F16** English model when available,
otherwise the indexed **Q8_0** model. F16 retains more weight precision and uses
more memory/compute; it is the same 600M-parameter architecture, not a larger
network, and is not guaranteed to improve every transcript. With your 6 GB
server, keep one ASR model resident and benchmark F16 before committing to it.

EOS uses a separate **Silero v6.2.1 ONNX** model. It examines 32 ms audio frames
and defaults to 320 ms of confirmed silence. When speech ends, the worker flushes
remaining recognition audio immediately instead of waiting for another large
ASR chunk. Short pauses and uncertain audio are handled with hysteresis; BEefy
retains its command-completeness checks.

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
endpointing and the existing local Sherpa provider as an outage backup. By
default, `LowMemoryMode=true`: Sherpa stays unloaded until fallback is needed
and is released after 30 idle seconds; local Whisper processes and the neural
BERT correction worker are disabled by default. An explicit
`OpenJibo__Stt__Correction__Enabled=true` keeps the neural corrector enabled
while retaining the other memory savings. Rule-based transcript correction and
normal command handling remain active. The first local fallback may take
longer while its model loads. Set `OpenJibo__Stt__BeefyEr__LowMemoryMode=false`
to restore warm local inference. `OpenJibo__Stt__BeefyEr__LocalModelIdleTimeout`
changes the idle release period. Existing Sherpa tuning still applies, and
synthetic transcript fixtures keep their original priority.

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
proxies. For a Docker proxy or a proxy on a different host, configure its exact
upstream source IP in BEefy's environment, then restart BEefy:

```dotenv
OpenJibo__Hosting__TrustedProxies__0=172.18.0.2
```

Use the actual proxy IP from your deployment; additional proxies use indices
`__1`, `__2`, etc. Give a container proxy a stable IP if it can change on restart.
The proxy must send `X-Forwarded-Proto: https` for incoming HTTPS connections.
Worker
connections over HTTP receive 426; missing or incorrect worker credentials
receive 401. Robot credentials do not grant worker access.

If BEefyER logs `stage=beefy_connection HTTP=426`, check this proxy trust setting
and the forwarded scheme. `HTTP=401` means check the shared secret;
`HTTP=404` means check feature enablement and the proxy route. Initial
`stage=local_runtime (ConnectionRefusedError)` messages are expected while the
native model downloads or loads.

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
BEEFYER_MODEL=auto
BEEFYER_THREADS=0
BEEFYER_RIGHT_CONTEXT=6
BEEFYER_HOTWORDS='["Jibo", "Hey Jibo"]'
BEEFYER_HOTWORD_BOOST=2.5
BEEFYER_VAD_ENABLED=true
BEEFYER_VAD_SILENCE_MS=320
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

`setup.sh` installs pinned NumPy/ONNX Runtime dependencies and downloads the
2.3 MB Silero model from a pinned revision, verifying its SHA-256. Run it again
when updating an existing installation. The Python 3.11+ worker, including
Python 3.14, does not need PyTorch for EOS detection.

## Optional Free Agent chat

Chat is off by default and does not replace ASR. To enable it, install the
optional HTTP client into the worker's own environment (not a system/Hermes
Python environment) and set the worker environment variable:

```sh
/opt/BEefyER/.venv/bin/python -m pip install -r /opt/BEefyER/requirements-chat.txt
```

```dotenv
BEEFYER_CHAT_ENABLED=true
```

Only the literal value `true` (case-insensitive) enables chat. The extra HTTP
client is pinned in `requirements-chat.txt`, which also includes the base worker
requirements. It is optional and is not included in the ASR-only requirements file.
The enabled worker advertises `chatModel: "gemma3:4b"` and `chatCapacity: 1` in
its existing `ready` envelope. This announces configured capability, not an
Ollama/model health probe. Provision Ollama and the model separately; the worker
never downloads models or starts Ollama.

The endpoint is fixed to **`http://localhost:11434/api/chat` on the BEefyER host**,
with model **`gemma3:4b`**, non-streaming replies, a 120-second total timeout and
one independent chat slot. There is no endpoint/model configuration, redirect
following, environment-proxy use, cloud fallback, tool execution or public
Ollama fallback. Incoming chat envelopes cannot supply an upstream or model.
If the local service/model is unavailable, chat fails with a sanitized code;
ASR and application heartbeats remain independent. No prompts, replies or
upstream exception details are logged.

Cancellation and disconnect cancel the asynchronous HTTP request, close its
client resources, and retain the slot until cleanup completes. The worker
cannot guarantee when Ollama stops server-side computation after its HTTP
client disconnects. Generation-owned tasks cannot send into a later connection.
See `PROTOCOL.md` for validation, byte limits, cancellation and error semantics.

Offline verification (HTTP and ASR fixtures only, no models or live backend):

```sh
python -m unittest discover -s tests/python -p 'test_beefyer_chat*.py' -v
```

## Prepare higher-precision ASR

F16 is an optional single-model accuracy profile for the 6 GB Ryzen server.
Preparation needs the existing `native/` checkout and a separate Python 3.11 or
3.12 environment because the pinned converter requires older NumPy. If neither
interpreter is installed, the helper uses pinned uv to obtain managed Python
3.12 in the BEefyER directory. Your Python 3.14 worker environment remains
separate. Stop the worker during conversion to
give the converter the server's memory budget:

```sh
sudo systemctl stop beefyer
sudo -u beefyer env XDG_CACHE_HOME=/var/lib/beefyer/.cache \
    /opt/BEefyER/.venv/bin/python /opt/BEefyER/prepare-models.py \
    --asr-f16
sudo systemctl start beefyer
```

An optional `--converter-python /path/to/python3.12` selects an existing
interpreter. The helper checks
the native source revision, creates a separate conversion environment, installs
CPU PyTorch, downloads the pinned original checkpoint, and atomically publishes
`models/nemotron-en-f16.gguf`. `BEEFYER_MODEL=auto` picks it on restart. This
preparation needs additional disk space for the checkpoint and conversion
environment. If conversion exceeds available memory, prepare the F16 artifact
on a larger machine and copy it into `models/`.

Compare actual word error rate and paced final latency before selecting F16.
Set `BEEFYER_MODEL=nemotron-en` to select the original Q8 model explicitly.
Higher CPU usage alone is not evidence of better recognition.

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
- With independent VAD, native token-silence endpoints and arrival-gap heuristics
  do not decide EOS. Confirmed speech plus a complete transcript can finalize
  short utterances without the legacy 8500-byte threshold. Firmware EOS and hard
  listen limits still apply. A segment flush commits recognized words without
  closing an incomplete command; speech can resume.
- Audio is incrementally decoded to 16 kHz mono PCM16. WebSocket queues are
  bounded. If the worker is busy, disconnected, overloaded, or fails to return a
  final result within three seconds, BEefy uses retained audio for local ASR.
  Old results cannot complete a canceled or replaced turn.
- Reconnect delays grow from one to thirty seconds, with jitter. Reconnection
  makes the worker available for subsequent turns; it does not take over an
  existing local turn.
- `BEEFYER_MODEL` accepts the indexed model name or a local compatible streaming
  GGUF path. `BEEFYER_THREADS=0` automatically selects up to eight available CPU
  threads; a positive value controls inference threads explicitly. More threads
  are not always faster; compare four, six, and eight on your own machine.
  Existing installations with `BEEFYER_THREADS=4` keep that limit until changed.
- `BEEFYER_RIGHT_CONTEXT=6` is the accuracy-focused 560 ms streaming default.
  The English model's published WER is lower at 560 ms than at 160 ms; this is
  a model benchmark, not a measurement of your robot. `1` restores the faster
  160 ms setting. `0` selects 80 ms, `13` selects 1120 ms, and `-1` uses the
  model's maximum context. Longer chunks give the recognizer more acoustic
  context, at the cost of less frequent partial updates. This does not change
  the model's size. Compare latency and accuracy on representative recordings.
- `BEEFYER_VAD_SILENCE_MS=320` controls confirmed silence independently of ASR
  context. Raise it toward 400 ms for frequent pauses, or lower it carefully
  toward 200 ms after testing quiet speech and noise. `BEEFYER_VAD_ENABLED=false`
  restores native token-silence endpointing.
- Recognition uses native RNNT phrase boosting for Jibo's name and per-turn
  robot ASR hints. Add household names or device names through the JSON array
  `BEEFYER_HOTWORDS`. `BEEFYER_HOTWORD_BOOST=2.5` is the default; `0` disables
  boosting. Hints bias recognition without replacing the recognized transcript.
  BEefy still performs transcript correction and command handling.
- Startup logs show the model, resolved thread budget, available CPUs, and
  streaming context. Each completed remote recognition logs audio duration,
  first partial latency, final drain latency, and maximum processing backlog.
  `BEEFYER_CAPACITY` and BEefy's `MaxConcurrentTurns` both limit active remote
  turns; the smaller limit wins. Increase capacity only after measuring CPU load.
  The worker starts the runtime with `BEEFYER_CAPACITY + 1` HTTP workers, since each
  live turn holds one runtime websocket.
- Disable offloading by setting `OpenJibo__Stt__BeefyEr__Enabled=false` and
  restarting BEefy. Local ASR and existing robot protocols remain available.

`GET /beefyer/status` on BEefy's HTTPS host accepts the same bearer header and
reports connection readiness, model identity, capacity, active turns, completed
turns, fallback count, first partial latency, final drain latency, worker thread
budget and context (when the worker manages inference), and the latest
reported processing backlog. It does not include transcripts or credentials.
BEefy's normal STT telemetry records the selected provider and model.

Status also reports `independentEndpointing` and `lowMemoryMode`; both should be
true for the default deployment. Worker logs report `Acoustic EOS detected`
separately from final transcript draining.

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

Measure EOS against an annotated recording independently of ASR:

```sh
/opt/BEefyER/.venv/bin/python /opt/BEefyER/benchmark-eos.py command.wav \
    --speech-end 2.4
```

Here `2.4` is the actual speech-end time in that recording. The report lists
speech boundaries and the detector's delay after that timestamp. Network,
command completeness, and final model draining can add response latency.

To measure how much processing headroom the server actually has, remove the
capture-speed waits:

```sh
/opt/BEefyER/.venv/bin/python /opt/BEefyER/benchmark.py command.wav \
    --unpaced --reference "set a timer for five minutes"
```

`realtimeMultiplier` reports how many seconds of audio were processed per second
of wall time, including streaming session work. For example, `5` means five
times realtime. `realTimeFactor` is its inverse; below `1` is faster than capture.
Capture-paced mode deliberately waits for audio and therefore does not report
those throughput values. Use paced mode for conversational latency and unpaced
mode for CPU headroom. Building C++ code during setup can saturate the CPU;
one live microphone supplies much less work once inference keeps up.

Check that BEefy's STT logs show `provider=beefyer-nemotron-streaming` on real
turns. A loaded model alone does not establish that audio is being offloaded;
connection failures or an increasing fallback count mean BEefy is using local
ASR instead. Low CPU usage while idle is expected.

Run automated tests without downloading model weights:

```sh
python3 -m pip install -r BEefyER/requirements.txt
python3 -B -m unittest discover -s tests/python -p 'test_beefyer.py'
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj --filter 'FullyQualifiedName~BeefyErTests|FullyQualifiedName~ModelEndpointingFinalizationTests'
```

The native streaming API and build instructions are documented upstream in
[NVIDIA's API reference](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/e59dfb0b5c22655620f41391cc9a000d6f56b802/docs/api.md)
and [build guide](https://github.com/NVIDIA/NeMo-Speech.cpp/blob/e59dfb0b5c22655620f41391cc9a000d6f56b802/docs/build.md).

The default English model's [accuracy results by streaming chunk size](https://huggingface.co/nvidia/nemotron-speech-streaming-en-0.6b#performance)
explain the 560 ms accuracy preset. Model benchmark results do not guarantee the
same improvement on household audio or on the Q8 CPU runtime.
