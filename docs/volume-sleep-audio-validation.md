# Volume, sleep, and Ogg audio validation

Implementation and validation recorded on October 4, 2026.

## Behavior

- Voice commands include `volume up`, `volume down`, `raise volume`, `lower volume`, and polite/hotphrase variants. Existing louder/quieter and numeric volume commands keep the native increments and 1–10 levels.
- Sleep uses one native handoff. Completed sleep transaction packets cannot reopen a listen, launch idle again, or overwrite a newer wake transaction. Fresh hotphrase listens, including inline command listens, clear the cloud sleep marker; passive listens do not.
- BEam idle ignores superseded asynchronous entry completions and repeated sleep events/refreshes while asleep. Its existing breathing-eye animation now loops after the sleep transition. Existing touch, hotphrase, and day-start wake transitions remain available.
- Recognized sleep, wake, and volume commands stay on their native route when the optional Phoenix conversation client is enabled.
- The shared Ogg normalizer counts actual decoded samples, fixes 60 ms SILK durations, preserves bounded end trimming, uses the no-completed-packet sentinel for continued audio, and recalculates checksums without changing packet payloads. Opus pre-skip is retained in the header and is not added again to the timeline.
- Local Whisper, Whisper Server, and Azure Speech FFmpeg paths all call the shared normalizer. Direct buffered and incremental Opus decoders read packets without relying on their container timestamps.

The original Jetstream encoder initialization defect was reported by Phoenix developers. These changes repair its server-side symptom; they do not patch the robot's Jetstream executable. Timing reference: [RFC 7845](https://www.rfc-editor.org/rfc/rfc7845.html#section-4).

## Automated validation

Focused cloud regressions cover short/polite volume phrases, numeric endpoints, one native action with silent completion, native routing with Phoenix enabled, completed sleep packets from idle and Nimbus, passive listens, fresh wake transactions, packet durations, continued packets, header pre-skip, final trimming, payload preservation, and checksums.

The FFmpeg test creates valid tone audio and corrupts only audio-page timestamps, with checksums updated to cover the malformed values. The reported negative origin `-4793230771707309754` produces zero PCM before repair; normalization restores PCM identical to the valid reference. Positive origins and signed overflow are covered too. Buffered and incremental direct Opus decoding remain identical for valid and corrupted origins.

Run from BEefy:

```sh
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj --filter 'FullyQualifiedName~OggOpusAudioNormalizerTests|FullyQualifiedName~NativeSleepAndVolume|FullyQualifiedName~Sleep_CompletedPackets|FullyQualifiedName~Volume_EmitsOne|FullyQualifiedName~ShortVolumeCommands'
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj
```

Run from BEam:

```sh
node tests/idle-sleep-lifecycle.test.js
node tests/sleep.test.js
node --check @be/be/skills/idle/index.js
```

The focused cloud set passed all 32 cases, both BEam scripts passed, and the shipped idle bundle passed its syntax check. FFmpeg was available and the PCM integration cases ran. When FFmpeg is absent, those cases print that the integration check did not run.

The full cloud suite has 29 failures also reproduced on an unchanged copy of BEefy revision `17abc107`. The failure sets match; there are no additional failures from these changes. The existing failures involve authentication/transport policy, API expectations, persistence, seasonal replies, and dance parsing. The weather regression cases pass.

## Physical validation pending

No physical Jibo sleep/wake result is claimed by the automated tests. Validate with the updated BEefy server and BEam pack together:

1. From idle, say `Hey Jibo, volume up`, `volume down`, `set volume to one`, and `set volume to ten`. Confirm each command changes the volume once and the listen closes.
2. Say `go to sleep` from idle and again during another skill. Confirm the sleep transition finishes and the breathing-eye animation persists for two minutes without interaction.
3. Check that each command creates one sleep transition, with no subsequent duplicate launch. A fresh hotphrase may intentionally wake Jibo before another sleep request.
4. Wake with head touch, screen touch, and a fresh `Hey Jibo`. Check that brightness and ordinary interaction return normally. Check the configured day-start wake separately.
5. Capture robot lifecycle logs and matching server transaction IDs for any interruption or unexpected wake.

Production deployment and BEam installation are separate from these source changes.
