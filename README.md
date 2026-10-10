# BEefy

BEefy is the conversation server a Jibo connects to. There is no account, no portal, and no Docker setup. A robot opens the hub socket, BEefy transcribes with Sherpa, and the Hey Jibo path answers.

The other BE repos are the rest of that system:

- BEach points a running robot at this server and at BEaker
- BEetle writes the same hub and OTA credentials when it flashes
- BEaker serves BEam and BEnch updates
- BEam is the on-robot skills those replies hand off to
- BEnch is the services image the client comes from

The hub robots use is `api.5x1.com:443`. This process listens on port 24667 behind that proxy. Updates are `http://joap.5x1.com:80`.

## Run

```bash
env \
  OpenJibo__State__Backend=Sqlite \
  OpenJibo__State__PersistencePath=/var/lib/5x1/cloud-state.json \
  OpenJibo__Stt__EnableStreamingSherpa=true \
  CONTAINER_APP_REVISION=5x1 \
  OpenJibo__Deployment__Mode=managed \
  OpenJibo__PersonalMemory__Backend=Sqlite \
  OpenJibo__PersonalMemory__PersistencePath=/var/lib/5x1/personal-memory.json \
  ASPNETCORE_URLS=http://0.0.0.0:24667 \
  dotnet run \
  --project src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Jibo.Cloud.Api.csproj \
  --no-launch-profile
```

Those environment names stay as they are so an existing launch keeps working. `OpenJibo__Deployment__Mode=managed` does not turn on a separate product tier. Sqlite files stay at the paths above.

`GET /health` returns `ok: true`. Hub sockets are `/v1/listen`, `/listen`, `/v1/proactive`, and `/proactive`, with or without a token. The HTTP `X-Amz-Target` calls the robot already makes stay on this same port.

## Hey Jibo

`conversation/` is the Phoenix parser and skills (nlu, skills, history, data). The API starts them on `127.0.0.1` when `node` and `conversation/node_modules` are present. They are not a second public port. If they are missing, the server still listens and the current turn handler answers.

Install the local packages once:

```bash
npm install --prefix conversation
```

Say “Hey Jibo, sing me a song” for a short robot song, or “Hey Jibo, sing a
Christmas song” for a Jingle Bells refrain. The server sends the introduction
and melody as a native sequence of short ESML prompts, with a pitch and duration
for each syllable. Each prompt stays under 400 characters and avoids nested pitch
or duration tags, as required by the robot's TTS service. Speech auto rules are
disabled for the performance so they cannot change the note markup. This uses
Jibo's own speech voice and needs no song download or API key.
The sound still needs listening verification on a physical robot.

## Speech and NLU tuning

Jev can classify unknown intents after the existing NLU, using independent
`OPENJIBO_JEV_*` endpoint, key and model settings. It is disabled until configured.
Sherpa supports configurable greedy or bounded beam decoding; greedy remains the
default until real recordings meet the accuracy and 200 ms latency gates. See
[configuration, evaluation and rollback](docs/asr-jev-evaluation.md).
