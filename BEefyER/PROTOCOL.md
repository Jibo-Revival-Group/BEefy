# BEefyER worker protocol v1

Transport: authenticated WSS at `/beefyer/connect`. BEefyER initiates the
connection with `Authorization: Bearer <shared secret>`. One worker is admitted
per BEefy process. Each JSON message includes `version: 1`, `type`, and
`generation`. BEefy creates a fresh generation for each connection.

| Direction | Type | Additional fields / meaning |
| --- | --- | --- |
| BEefy → worker | `hello` | Announces the connection generation. |
| Worker → BEefy | `ready` | `model`, `capacity`; sent once, after loaded-model readiness. |
| BEefy → worker | `ping` | Application heartbeat every second. |
| Worker → BEefy | `pong` | Answers heartbeat while inference runs independently. |
| BEefy → worker | `start` | `streamId`, `sessionId`, `turnId`, `sequence: 0`, `sampleRate: 16000`, `endpointMs`, `maxSpeechSeconds`. |
| Worker → BEefy | `partial` | Turn identity, `sequence`, `text`: full provisional turn text, including committed speech segments. |
| Worker → BEefy | `endpoint` | Same fields; an acoustic segment completed. BEefy decides whether the robot turn is complete. |
| Worker → BEefy | `progress` | Turn identity, `sequence`, processing counters; silence updates preserve the endpoint latch. |
| BEefy → worker | `finish` | Turn identity and the last audio sequence; drain inference after all earlier audio. |
| Worker → BEefy | `final` | Turn identity, matching finish `sequence`, completed full-turn `text`. |
| BEefy → worker | `cancel` | Turn identity and last sequence; release the stream without executing a transcript. |
| Worker → BEefy | `error` | Turn identity, `sequence`, sanitized `code`; abandon the remote attempt. |

Turn identity fields are repeated on all per-turn JSON messages. `streamId` is a
unique 32-character lowercase hexadecimal UUID. Native model connections are
independent for each stream. The audio sequence starts at 1 and increases by
exactly 1 per packet. Result sequences identify the most recent audio forwarded
to inference, not a promise that every sample has already been processed.
Inference events also carry `audioSeconds`, `processedSeconds`, and
`backlogSeconds` for measurement. `progress` events use the same
identity/sequence checks as transcription events.

Binary WebSocket messages contain:

| Offset | Bytes | Value |
| --- | --- | --- |
| 0 | 16 | Stream UUID bytes, in the same order as decoding its hex string. |
| 16 | 8 | Signed 64-bit little-endian audio sequence. |
| 24 | remaining | Signed 16-bit little-endian, mono 16 kHz PCM. |

Audio packets contain at most 100 ms of samples. All queues are bounded; the
worker does not block control-message handling while inference is busy. A
BEefy outgoing queue overflow aborts the connection so unsent cancellations
cannot leave orphan inference streams consuming capacity.

BEefy rejects protocol/generation mismatches. Results for removed streams are
ignored. Per-stream session/turn mismatches, sequence regression, or unexpected
finals abandon that remote attempt. Worker sequence gaps or capacity exhaustion
produce per-turn errors. A final result must match the finish sequence. A fresh
connection never takes over an already running local turn.

Native `.completed` events represent individual speech segments. The worker
accumulates them until BEefy sends `finish`, then sends exactly one `final` after
the native `input_audio_buffer.committed` acknowledgement. Resetting BEefy's
endpoint latch never discards recognized words. If BEefy defers a turn after
draining it and receives more audio, that turn switches to retained-audio local
ASR instead of attempting to reuse a finished native stream.
