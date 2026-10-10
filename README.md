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

Conversations run entirely in .NET. The managed grammar matcher and native handlers use embedded, revision-pinned text resources; Node, npm, and localhost helper services are unnecessary. Existing provider configuration and personal data stores continue to work. See [native command coverage and hardware checks](docs/native-conversation.md).

Say “Hey Jibo, sing me a song” for the original Twinkle Twinkle performance.
“Sing a Christmas song” chooses from all seven original holiday song MIMs:
Jingle Bells, Frosty the Snowman, Rudolph, Winter Wonderland, Santa Claus Is
Coming to Town, Feliz Navidad, and Dreidel. You can request one by name, such as
“Hey Jibo, sing Rudolph”, or say “sing all the Christmas songs” for the repertoire.
All are available throughout the year; original seasonal comments are preserved.
These are short performances, not full songs. Imported MIM markup and prompt IDs
are preserved and play as short native SLIMs within the robot TTS limit. No song
download or API key is required; robot audio verification is still needed.

## Speech and NLU tuning

Jev can classify unknown intents after the existing NLU, using independent
`OPENJIBO_JEV_*` endpoint, key and model settings. It is disabled until configured.
Sherpa supports configurable greedy or bounded beam decoding; greedy remains the
default until real recordings meet the accuracy and 200 ms latency gates. See
[configuration, evaluation and rollback](docs/asr-jev-evaluation.md).
