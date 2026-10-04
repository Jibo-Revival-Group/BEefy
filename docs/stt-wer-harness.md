# STT WER harness

Use `SttWerHarness` to compare whisper.cpp and streaming Sherpa transcripts against reference text.

## Quick check

```csharp
var summary = SttWerHarness.Evaluate(new[]
{
    ("turn-1", "hey jibo what time is it", sherpaHypothesis),
    ("turn-1-whisper", "hey jibo what time is it", whisperHypothesis),
});
Console.WriteLine(SttWerHarness.FormatMarkdown(summary));
```

Or load a JSON array of `{ "name", "reference", "hypothesis" }` objects:

```csharp
var summary = SttWerHarness.EvaluateFromJson(File.ReadAllText("captures/stt-wer-cases.json"));
```

There is no committed audio corpus yet. Capture a few live turns with both engines enabled one at a time, then score them here before switching `OpenJibo:Stt:EnableStreamingSherpa` on permanently.

## Bounded grammar correction

Buffered STT keeps the recognizer output in `RawTranscript` and applies a local
preference-question correction pass to `NormalizedTranscript`. Changed turns have
`stt:grammarCorrection=preference-frame` in their attributes. This uses the existing
preference subject catalog; no second recognizer, model download, or network call is
involved. The same correction is used when routing client-provided preference text.

For example, `what's your favorate holiday` becomes `whats your favorite holiday`.
There is no `paper` replacement rule; real-word recovery uses the neural fallback below.
Known favorite-slot confusions and single-edit `fav...` spellings are accepted only
inside a complete robot preference question with a whole known subject. Negation
via `least` is retained. Unknown subjects, extra trailing clauses, personal memory
statements, names, and numeric commands are not rewritten. This is a bounded
intent-domain correction, not a general grammar model or a measured acoustic WER gain.

Run the correction and routing regressions and print the local timing comparison:

```sh
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj \
  --filter 'FullyQualifiedName~AsrGrammarCorrectorTests|FullyQualifiedName~UtteranceFrameParserTests|FullyQualifiedName~ModelEndpointingFinalizationTests' \
  --logger 'console;verbosity=detailed'
```

For accuracy evaluation, score original and corrected hypotheses separately against
reference transcripts, including examples that must remain unchanged. Measure both
error reduction and accidental corrections before widening the accepted grammar.

## Optional neural correction on unrecognized commands

The server uses a quantized masked language model from
[`prajjwal1/bert-mini`](https://huggingface.co/prajjwal1/bert-mini) (4 layers,
256 hidden units). This scores words in their sentence context rather than fixing
spelling or generating chat responses. A shared catalog supplies supported fixed
commands for pizza, dance, jokes,
stories, greetings, time/date, weather, and preference questions;
[CMUdict](https://github.com/cmusphinx/cmudict) supplies pronunciations.
Routing and recovery reuse these phrase lists. Existing weather/leather aliases
remain in routing but are excluded from recovery hypotheses.

The worker aligns whole phrases with at most one content-span correction and one
grammar edit. It concatenates pronunciations across a single word boundary, so
`make a pit sir` and `make a peter sir` can match `make a pizza`. Commands with two
or more words are eligible. Short fixed commands, including `twerk`, also allow
one unknown spelling of 4–16 letters, with the first two letters retained and at
most two spelling edits. Dictionary words are not reinterpreted in this single-word
path. The microphone transcript filter preserves these bounded candidates for
NLU rather than clearing them as empty audio. Protected ownership, numbers,
negation, unrelated trailing clauses, and personal names cannot be discarded to
force a match.

For acoustically nearby hypotheses, the masked LM scores each changed tokenizer
piece in its phrase context. Mean log probabilities allow comparisons across
word splits and multiple tokenizer pieces. Content replacements require positive
contextual improvement; grammar substitutions retain a stronger evidence check.
Candidates are ranked by contextual improvement and pronunciation distance. The
returned confidence is sigmoid(3 × minimum contextual gain), capped by the
sigmoid of the winning score margin when there is a competing hypothesis.
For a single-word command, both spellings use the same `can you …` command frame.
The LM compares complete-word log probabilities divided by the larger tokenizer
piece count of the two spellings. This prevents common subword fragments from
making a malformed spelling appear more likely than the intended command.
`twick` and `twelc` recover to `twerk` with the installed model; the ordinary
positive-evidence, pronunciation/spelling-distance and confidence checks still
apply. Twerk phrases are shared between routing and the recovery catalog.
An unknown single-word er/ir/ur spelling before a consonant (for example `twirk`)
can also match a unique supported command with the same bounded sound spelling.
This pronunciation match returns heuristic confidence 0.90 without requiring BERT
to prefer a rare command's tokenizer fragments. Known dictionary words, ambiguous
homophones, extra words, and protected tokens remain excluded.

A close competitor therefore lowers confidence instead of triggering a separate
fixed ambiguity cutoff. These are heuristic scores, not calibrated probabilities
of the user's intent. The host accepts scores at or above 75% by default.
Real microphone accuracy still requires a recorded corpus.

Missing-transcript fallback replies explicitly launch Nimbus for their cloud
speech, preventing the same LISTEN from also launching a robot-owned greeting.
Normal server logs include the original recognized words and finalized intent.

From the BEefy repository root, install the model and its isolated Python runtime:

```sh
python3 scripts/cloud/setup-asr-correction-model.py
```

The pinned dependencies require Python 3.10–3.13 with venv/ensurepip support;
Python 3.12 is recommended. Python 3.14 is incompatible with the NumPy pin.
Setup uses the current interpreter when compatible, otherwise searches for
`python3.12`, `python3.13`, `python3.11`, or `python3.10`. Pass
`--python /path/to/python3.12` to select one explicitly. Existing environments
using another Python version are preserved as `venv.previous` or
`export-venv.previous` (with numbered suffixes if needed) before recreation.
Existing environments without pip are repaired with `ensurepip`; if unavailable,
install the matching Ubuntu package, such as `python3.12-venv`, and retry.
Setup verifies pinned checkpoint,
tokenizer, and dictionary hashes, then exports and quantizes the full masked-LM
head. The pretrained ONNX feature extractor alone cannot score words. The inference
weights occupy about 19 MB under the ignored `App_Data/asr-correction/model` directory.
One-time export uses a separate, larger `export-venv` with PyTorch/Transformers;
`source` and `export-venv` can be removed after setup. Inference needs only `model`
and `venv`. The checkpoint model card declares MIT licensing; CMUdict's license is retained in
`model/CMUDICT-LICENSE`. Run setup on each server. No downloads occur during a turn.
The worker script is copied into .NET build and publish outputs.

At startup, the server loads and warms a single CPU-thread worker in the background.
Only an unresolved speech command invokes it. Confident recovery runs before the
Phoenix conversation handler and dispatches the recovered command directly, so a
conversational "I don't understand" response cannot preempt an accepted match.
When recovery is rejected or unavailable, Phoenix and the normal fallback still
run. Already recognized commands, direct text, non-English input, yes/no prompts,
skill-owned listens, and clock-value/cloud-owned follow-ups retain their existing
handling and do not invoke the correction model.
The server retries command matching once and accepts a proposal only if it maps to
a supported intent and passes bounded edit checks. It preserves numeric tokens,
negation, and preference ownership and rejects wholesale rewrites. A malformed
initial question word may be repaired, such as `my time is it`. If the model is
missing, busy, timed out, failed, or produces an unusable correction, the original
fallback runs. Requests do not queue. A timed-out pipe is discarded and the worker
is restarted to prevent late responses from being used for another turn.

Configuration under `OpenJibo:Stt:Correction` (environment variable example:
`OpenJibo__Stt__Correction__Enabled=false`):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Use the fallback when local assets are installed. |
| `Directory` | Auto-discovered `App_Data/asr-correction` | Contains `model` and `venv`. |
| `PythonPath` | Python in that venv | Optional custom interpreter. |
| `WorkerPath` | Published `Audio/AsrCorrection/worker.py` | Optional custom worker location. |
| `TimeoutMilliseconds` | `150` | Correction budget, clamped to 1–500 ms; normal command matching follows. |
| `MinimumConfidence` | `0.75` | Accept a valid match at or above this heuristic confidence, including the ambiguity cap; **not calibrated intent confidence**. |

Accepted corrections keep `RawTranscript` unchanged and record the corrected text,
model, contextual score, and elapsed time in `stt:modelCorrectedTranscript`,
`stt:correctionModel`, `stt:correctionContextScore`, and `stt:correctionDurationMs`.
The existing turn-phase metrics record `asr_correction` outcomes. Repeating an
accepted command reuses its corrected normalized text without another inference.

Initial buffered microphone turns and `CLIENT_ASR` messages are classified as
speech (`WakeWord`); explicit typed text remains `DirectText`. This distinction
keeps recovery eligible for the actual robot audio path, not just synthetic tests.

If startup says `ASR correction model is not installed`, recovery is inactive even
when `Enabled=true`. Assets are ignored by Git and must be installed on each server:

```sh
cd /home/ubuntu/BEefy
python3 scripts/cloud/setup-asr-correction-model.py
```

After installing and restarting, confirm startup reports
`Local quantized contextual BERT ASR correction model is ready.` The missing-model
warning reports the checked interpreter, worker script, and weights paths to
separate absent assets from a missing published worker.

Rebuild and restart the deployed API to load worker and dispatcher changes. An
explicit `OpenJibo__Stt__Correction__MinimumConfidence` override takes precedence
over the 0.75 default; update it to `0.75` if a previous deployment set `0.8`.

Run the dependency-free pronunciation and ambiguity tests, then the real
installed-model smoke/latency tests:

```sh
python3 -B -m unittest discover -s tests/python -p 'test_*.py'
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj \
  --filter 'FullyQualifiedName~AsrModelFallbackTests|FullyQualifiedName~LocalAsrCorrectionModelTests' \
  --logger 'console;verbosity=detailed'
```

The neural integration test explicitly skips when local model assets are absent;
it never downloads them itself. Recognized commands incur no model inference.
Unrecognized commands can add up to the configured inference budget, so evaluate
latency and accidental corrections alongside error recovery on deployment hardware.

The installed-model test checks pizza recovery (`peter`, `pit sir`, `peter sir`),
dance/story confusions, multi-piece words and word splits, existing real-word
recovery (`paper color`, `my time is it`, `choke`/`joke`), grammar insertion, and
unchanged ownership, negation, names,
numeric commands, unknown speech, and legitimate poem requests. Its timing loop
runs uncached model inference through the Python pipe. These examples verify
recovery behavior, not a population-level accuracy guarantee.

### Twerk response compatibility

Twerk closes recognized speech with EOS, then sends a non-final cloud LISTEN for
`chitchat-skill`, followed by its final SKILL_ACTION. The robot Skills Service Manager
remaps that cloud skill to `@be/nimbus`; the wire ID must identify the cloud skill
rather than the local renderer. The playback payload uses the original
`RA_JBO_Twerk_AN_01` prompt and `&(music, twerk), !(short)` animation selector,
including `endNeutral=true`, from the bundled legacy MIM. Correctly recognized
`twerk` and pronunciation-recovered commands use this same response path.
Already-recognized `CLIENT_NLU` twerk commands also emit the cloud playback action,
even when the robot supplies only the intent and no transcript text.

### Hub transaction completion on native robots

Final playback replies set `final = true` on the SKILL_ACTION envelope as well as
inside `data`. The native `LhubClient::run` reads the envelope flag and forwards it
to `ListenLoop::phw_jm_hub_skill_info`, which closes the Hub turn. Nimbus receives
the action data separately. A nested-only flag leaves the native Hub transaction open,
even when the server has finalized its turn and supplied valid playback.

Standalone no-input LISTEN replies are final. When a no-input reply is followed
by a skill redirect, LISTEN remains non-final and the redirect closes the turn.

### Empty timeout responses on native robots

Empty LISTEN closures carry `asr.annotation = SOS_TIMEOUT`. The native SDK's
`SharedGlobalEvents` checks this annotation before deciding that a result with
`launch` in its NLU rules and no match is an unknown command. Without it, a silent
turn can trigger Idle's no-match animation, empty quoted text, and “I don't know
what this means.” Local skill listens still receive empty text and intent.
This does not mark real unmatched speech as a timeout.

Production logs include transaction IDs on finalized plans and reply summaries,
plus the reason and annotation for empty hotphrase closures, to distinguish a
recognized command from a later no-speech transaction.
