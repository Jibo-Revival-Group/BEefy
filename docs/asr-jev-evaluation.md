# ASR tuning and Jev NLU

BEefy keeps the current warmed streaming Zipformer model and greedy decoding by
default. The new beam-search controls are available for measured experiments;
no recorded-audio accuracy or latency improvement has been established in this
checkout. There is no installed Sherpa model, labeled microphone corpus, or Jev
API key here, so native-model replay and live Jev acceptance remain unverified.

## Configure Jev

Use the separate `OPENJIBO_JEV_*` settings documented in `.env.example`. Supply
these variables to the API process using the same launch environment mechanism
as the existing search settings; editing an example file does not configure a
running process. Jev is off by default. To enable it, set
`OPENJIBO_JEV_ENABLED=true` and supply `OPENJIBO_JEV_API_KEY`.

The default endpoint is `https://openrouter.ai/api/alpha/decisions`, and the
model is `typesafe/jev-1.13`. Override the full endpoint URL and model to use
another gateway implementing the same Decisions request/response format. This
is not a chat-completions API. See the [OpenRouter Decisions reference](https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-request).

Jev is a text-only typed decision model, not a speech recognizer or text generator.
Its Choice primitive selects from supplied options; Noul returns a yes/no
probability, and Score evaluates a rubric. TypeSafe's documented limits are
255 options per Choice, 64k tokens per request, and 32k tokens for state plus the
longest question. Questions in the same call are independent: a leaf question
cannot read the group selector's answer. See the official
[API reference](https://docs.typesafe.ai/api),
[model limits](https://docs.typesafe.ai/models), and
[speculative fan-out pattern](https://docs.typesafe.ai/patterns/fan-out).
The OpenRouter endpoint uses the
[Decisions API](https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-request),
which has the same typed question shape.

The catalog includes existing semantic commands, holiday greetings (including
Merry Christmas), and all 4,656 registered native chitchat responses. Native
responses retain their manifest MIM and fixed entities. Responses requiring
open-ended entity values still require local extraction; Jev cannot invent them.
Known local parsing remains the fast path. Standalone `mary christmas` (including
short greeting suffixes) preserves the Christmas claim locally; names or questions
about Mary are not rewritten. Greetings and holidays have an explicit selector
group so they are not described solely as requests to perform an action. Holiday greetings are also included
in the contextual ASR correction candidates.

The top-level selector has no unknown option and is advisory. All actual response
groups are evaluated independently, each with its own unknown option. The highest
valid non-unknown leaf probability wins if it meets `OPENJIBO_JEV_MIN_PROBABILITY`
(default 0.85). Group selector probabilities are neither gates nor multipliers.
For example, a Christmas leaf at 0.87 passes a 0.85 threshold even when the top
selector prefers another group. If every group selects unknown, or the best
non-unknown match is below the configured threshold, local fallback runs.

The scripted response families are partitioned by semantic topic into leaf groups
with at most 254 responses plus unknown. All leaf questions are packed into
parallel requests using conservative UTF-8 byte bounds for the 32k/64k context
budgets; the full catalog is never serialized into one request. Every response
remains reachable without trusting a top-level branch selection. Evaluating all
groups uses more provider requests and input tokens than selected-branch traversal.
All batches share one deadline and have no retries. Reported leaf scores are not
a calibrated global probability across groups and do not use the
distribution-confidence field. Live accuracy and latency must be evaluated with
labeled microphone transcripts; mocks verify coverage, dispatch, bounds, and
fallback behavior, not recognition quality.

Configured Jev runs only when the existing local parser, bounded ASR command
recovery and native conversation path have not recognized the turn. A known
local intent or recognized native response (including conversation) skips Jev.
The native parser returns no match only when local parsing is
also unresolved. Triggers, system input, skill-owned listens, yes/no
prompts, clock-value follow-ups and pending proactive offers keep local handling.
Accepted fallback decisions dispatch directly; scripted responses needing open-ended entities must pass local extraction.
Value-bearing commands must pass existing local extraction; otherwise the
original unknown-response path runs. No-match, low probability, malformed responses,
HTTP failures and timeouts also fall back. Caller cancellation propagates.

A request gets at most `OPENJIBO_JEV_TIMEOUT_MS` milliseconds (1–1000; default
1000), across all parallel batches, with no retries. Missing or invalid configuration disables
network calls. This deadline is separate from the 200 ms ASR allowance.
The `nlu` turn-phase metric reports provider duration and outcomes. Debug logs
record model, intent and selected probability without logging credentials or
raw remote error bodies. Rejected requests log the HTTP status and a bounded
structured validation message with the configured key and transcript redacted;
accepted turns carry `nlu:provider`, `nlu:model`,
`nlu:probability` and `nlu:outcome` attributes. A classification rejected by local
value extraction records `nlu:outcome=missing_values`.

## Tune Sherpa

These .NET configuration settings apply to both streaming and buffered ASR:

| Environment variable | Default | Meaning |
| --- | --- | --- |
| `OpenJibo__Stt__SherpaDecodingMethod` | `greedy_search` | `greedy_search` or `modified_beam_search` |
| `OpenJibo__Stt__SherpaMaxActivePaths` | `4` | Active paths for beam search, 1–16 |
| `OpenJibo__Stt__SherpaThreads` | `0` | 0 uses half the available processors; positive values set CPU threads |

Sherpa no longer reads Whisper's thread setting. Model weights stay resident,
warmup is retained, and hotword biasing stays disabled. Incomplete utterance
endpoint resets commit the preceding partial transcript so later hypotheses
retain the full utterance. Existing silence thresholds are unchanged.

## Replay recorded audio

Create a JSON corpus with unique names, paths to actual Ogg/Opus robot recordings,
reference transcripts and expected BEefy semantic intents. Paths are relative
to the corpus file. Do not substitute server hypotheses for reference labels.

```json
[
  {"name":"time-quiet", "audioFile":"audio/time-quiet.ogg", "reference":"what time is it", "expectedIntent":"time"},
  {"name":"pause-lights", "audioFile":"audio/pause-lights.ogg", "reference":"turn on the living room lights", "expectedIntent":"ha_lights_on"}
]
```

Include short commands, names, numbers, negation, noisy/distant speech,
non-command speech and mid-sentence pauses. Use an independent holdout corpus
for the final acceptance decision. With model files already installed, run on
the target server, using the same CPU allocation and representative load:

```sh
dotnet run --project tools/SpeechEvaluation -- \
  --corpus /path/to/recordings.json --model /path/to/zipformer \
  --repetitions 30 --concurrency 1 --threads 2 > /tmp/asr-report.json
```

Repeat at representative concurrency. The tool warms each recording, compares
greedy, beam2 and beam4, and reports per-sample hypotheses, corpus-weighted WER,
command errors and ASR p50/p95 processing times. File reads and local intent
routing are outside ASR timing. Routing uses isolated in-memory state and no
Jev calls. No model downloads occur, and no production settings are changed.
Unlabeled command cases keep the recommendation at greedy.

The recommended candidate must reduce WER, avoid increasing command errors and
stay within baseline p95 + 200 ms. Candidates tied on WER are ordered by p95;
otherwise greedy remains selected. This is **warm buffered processing latency**,
not live microphone-to-final-transcript latency: it excludes endpoint silence
and live receive/streaming timing. Before changing production defaults, replay
through the real WebSocket path and compare audio-end-to-final-transcript p95,
including pause cases. Keep ASR and Jev NLU measurements separate. The replay
report explicitly flags this required live endpoint validation.

## Evaluate Jev live

Prepare a labeled NLU corpus:

```json
[
  {"name":"time-paraphrase", "text":"Could I get the current time?", "expectedIntent":"time"},
  {"name":"ability", "text":"can you dance", "expectedIntent":"robot_can_dance"},
  {"name":"negated", "text":"do not turn on the lights", "expectedIntent":"unknown"}
]
```

With Jev enabled and credentials supplied in the process environment:

```sh
dotnet run --project tools/SpeechEvaluation -- \
  --nlu-corpus /path/to/intents.json > /tmp/jev-report.json
```

This mode makes paid live requests and reports classifications, probabilities,
accuracy and p50/p95 NLU latency. Test dispatch and entity extraction separately
using the automated routing tests. Reports contain corpus text/hypotheses but
no API keys.

## Rollback and checks

Set `OPENJIBO_JEV_ENABLED=false` to restore the deterministic native path.
Set `OpenJibo__Stt__SherpaDecodingMethod=greedy_search` to restore greedy ASR.
Restart the API after changing these startup settings. No persistence migrations
or robot wire-format changes are required.

```sh
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj \
  --filter 'FullyQualifiedName~JevNlu|FullyQualifiedName~SherpaAccuracy|FullyQualifiedName~SttReplayHarness|FullyQualifiedName~ModelEndpointingFinalization|FullyQualifiedName~AsrModelFallback|FullyQualifiedName~OggOpus'
python3 -B -m unittest discover -s tests/python -p 'test_*.py'
```

## Legacy speech markup

Some imported greeting and holiday lines retain legacy pause tags. When constructing
ESML from plain SpeakAction text, recognized numeric `break size` tags are emitted
as markup; residual tags are removed before spoken text is XML-escaped. Explicit
ESML payloads from native scripts and performances retain their existing handling.
This prevents Jibo from pronouncing escaped TTS tags such as `break size`.
