# Native conversation and command coverage

BEefy runs conversations in .NET. There is no Phoenix process host, HTTP conversation adapter, npm installation, or dependency on the former localhost services at ports 24701–24704. The embedded resource is shipped in `Jibo.Cloud.Application.dll`, so a published server needs neither `~/phoenix` nor a `conversation/` directory. Existing audio transcription tools remain separate dependencies when enabled.

The reference is Phoenix revision `ff8f592fa543bd875b79756275a655b637b97817`. [Resource provenance](native-conversation-reference.json) records hashes of all 4,801 bundled grammar, vocabulary, manifest, dialog, catalog, and fixture files. [The command inventory](native-command-inventory.json) includes all 622 catalog names and 4,764 manifest registrations, with source rules, entity schemas, and handler targets.

| Inventory category | Catalog names | Native outcome |
| --- | ---: | --- |
| Robot launches | 68 | Route the original intent and parameters to the registered on-robot skill |
| Cloud responses | 442 | Existing native personality, knowledge, reports, and singing; pinned scripted content for additional responses |
| Contextual skill replies | 71 | Return parsed NLU to the listening robot skill without a second launch or cloud speech |
| Global commands and replies | 21 | Existing global handlers, idle gestures, menu routing, or an active robot listener |
| Catalog-only names | 20 | Phoenix has no registered handler for these names; existing BEefy handling or normal no-match fallback applies |

These counts describe the reference surface, rather than treating every catalog name as an executable command. Registered local commands have 76 entries across 17 skills. The coverage tests execute every entry, check the target skill and intent in actual socket messages, and reject duplicate redirects or competing speech. Every registered chitchat entry is rendered to native ESML without unresolved parameters. The 89 pinned launch oracle phrases retain their reference intents. Contextual catalog entries must reference loaded rules and an owning domain.

## Routing and dialog

`NativeGrammar` reads the reference DSL, including alternatives, optional/repeated phrases, equivalent vocabulary, factories, costs, semantic assignments, and enrolled names. Actions use a bounded managed interpreter; no JavaScript engine executes grammar text. A `NativeParseResult` carries intent, target skill, domain, requested rules, priority, and typed entities. Grammar selection uses specificity minus match cost; priority remains output metadata, matching the reference launch union.

Existing BEefy handlers retain precedence. Replay, Bad Apple, dice, countdown, and existing native personality behavior continue to run. Personal fact memory and shopping/to-do list commands have been removed; robot profile recognition and provider settings remain available. Additional local commands use a generic socket launch carrying the original intent and parameters. Settings, main menu, Circuit Saver, exercise/yoga, Friendly Tips, tutorial, IFTTT, release notes, Who Am I, and additional clock/greeting/photography/radio/Word of the Day routes use the manifests' skill IDs.

Hue setup, reset, default group, on/off, brightness, warm/cool, colors, and named groups route to `@be/hue-control`. Room names use Phoenix's canonical identifiers, such as `living` and `kidsBedroom`. IFTTT passes both the command and `slotAction`. These routes instruct existing robot skills; they do not provision bridges or alter robot firmware.

Robot-owned replies stay with their existing listener. The color dialog asks for a favorite color, retains its state for the answer, and closes on completion or cancellation. Its question emits a non-final cloud action with a listen context. Template behavior returns its reference announcement; explicitly addressed example behavior runs its three scripted node outcomes. Additional chitchat uses the pinned MIM text, category CSVs, runtime speaker/referent/owner/location/date data, and original animation and speech markup. Missing runtime facts use the source fallback rather than inventing personal data.

Weather, calendar, commute, news, knowledge, and their existing provider configuration remain native. Weather accepts location entities and dates; news accepts topic/category entities in addition to the existing transcript and preference handling. Existing unavailable-provider responses remain available without credentials. Jev remains optional: entity-free executable local commands are added to its catalog; commands needing parameters are excluded from model-only launches. Deterministic routing works without Jev.

Home Assistant implementation, pairing, providers, and capabilities are unchanged. Accounts, portal, OTA, deployment, and robot publication are outside this change.

## History and existing data

Native launch history uses an additive SQLite sidecar. Cloud state and personal-memory formats are unchanged. Launch history has a 14-day window; optional speech records persist separately. History write errors are logged without discarding the command response.

| Configuration | Default |
| --- | --- |
| `OpenJibo__History__PersistencePath` | The state persistence path with extension `.history.sqlite`, or `App_Data/cloud-state.history.sqlite` |
| `OpenJibo__History__RecordSpeech` | `false`; the existing `ETCO_hub_recordSpeechHistory=true` is also honored when the new setting is absent |
| `OpenJibo__History__LegacySnapshotPath` | `ETCO_history_dataFile`, otherwise `conversation/packages/history/data/store.json` |

An existing Phoenix snapshot imports once per path. Launch and speech arrays are preserved in the sidecar, and the original file is left untouched. Invalid snapshots fail explicitly without recording a successful import. The old snapshot directory can be retained solely for migration; it is unnecessary for command processing. Ignored, untracked runtime directories and user data have not been deleted.

## Build and validation

The normal .NET build/publish includes the resource automatically:

```sh
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj
dotnet publish src/Jibo.Cloud/dotnet/src/Jibo.Cloud.Api/Jibo.Cloud.Api.csproj -c Release -o /tmp/beefy-native-publish
```

To regenerate the inventory and resource after deliberately choosing a different reference revision:

```sh
python3 tools/native-conversation/import_reference.py ~/phoenix
```

This optional maintenance tool requires Python and Git, never Node. Re-run the oracle and coverage tests after updating the reference.

A published server was started from `/tmp` with isolated configuration and data, Node absent from `PATH`, and all former helper ports unavailable. `tools/native-conversation/smoke.py --port 29467` exercised real HTTP health and WebSocket conversations for main menu, yoga, Hue group/color parameters, and an unavailable weather provider. Transcription was disabled for this text-only smoke; audio and hardware checks below remain necessary. The published server was stopped after validation.

The full regression suite includes interaction, socket, provider, persistence, and Home Assistant checks: 2,765 passed, 29 failed, and 18 skipped. The unchanged baseline has the same 29 failures involving API/security expectations, seasonal response text, and a protocol persistence fixture. The native implementation adds no regression failures relative to that baseline. PostgreSQL integration checks require a configured database and remain skipped in this environment.

## Hardware checks still required

No physical robot or Hue bridge was exercised. Run these checks before deploying this implementation:

- Open each added local skill; verify the expected screen/performance and a single launch. Confirm clock menus, timer/alarm values, photos, radio station selection, greetings, and Word of the Day still work.
- Complete yoga/routine selection, tutorial yes/no replies, settings navigation, Who Am I confirmation, and other robot-owned contextual replies. Confirm cloud speech does not interrupt them.
- Set up Hue; verify the default group, each named room, all-lights commands, brightness extremes, warm/cool, named colors, and reset. Check group identifiers against the installed robot skill.
- Trigger an existing IFTTT action; verify its full action text reaches the robot.
- Ask and answer the color question, cancel it, and interrupt it with a fresh wake phrase. Verify microphone behavior and completion.
- Play representative newly covered scripted responses with animations and speech markup. Check real audio, TTS limits, and animation timing.
- Exercise configured weather/calendar/commute/news/knowledge providers and unavailable-provider behavior on hardware. Recheck existing Home Assistant commands without adding capabilities.
