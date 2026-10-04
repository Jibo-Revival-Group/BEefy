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
spelling or generating chat responses. The preference catalog supplies supported
command hypotheses; [CMUdict](https://github.com/cmusphinx/cmudict) supplies
pronunciations for finding acoustically nearby words. There is no error-specific
`paper -> favorite` or `my -> what` table.

For example, the model compares `paper` and `favorite` in `what is your ___ color?`,
and `my` and `what` in `___ time is it?`. It requires similar sounds (or a grammar
word edit), a plausible replacement, strong contextual improvement, and a clear
winning hypothesis. At most two word edits and one content-word substitution are
allowed. Whole sentences are aligned; unknown subjects and trailing clauses do
not get forced into a command. Multi-wordpiece replacements are currently excluded.
This is command-domain context, not conversation history or unrestricted intent
inference. Real microphone WER gains still require a recorded corpus.

From the BEefy repository root, install the model and its isolated Python runtime:

```sh
python3 scripts/cloud/setup-asr-correction-model.py
```

Python 3.10 or newer with venv/pip is required. Setup verifies pinned checkpoint,
tokenizer, and dictionary hashes, then exports and quantizes the full masked-LM
head. The pretrained ONNX feature extractor alone cannot score words. The inference
weights occupy about 19 MB under the ignored `App_Data/asr-correction/model` directory.
One-time export uses a separate, larger `export-venv` with PyTorch/Transformers;
`source` and `export-venv` can be removed after setup. Inference needs only `model`
and `venv`. The checkpoint model card declares MIT licensing; CMUdict's license is retained in
`model/CMUDICT-LICENSE`. Run setup on each server. No downloads occur during a turn.
The worker script is copied into .NET build and publish outputs.

At startup, the server loads and warms a single CPU-thread worker in the background.
Only an unresolved speech command invokes it. Already recognized commands,
Phoenix-handled turns, direct text, non-English input, yes/no prompts, skill-owned
listens, and clock-value/cloud-owned follow-ups preserve their existing paths.
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
| `MinimumConfidence` | `0.8` | Minimum sigmoid of contextual logit improvement; this is **not calibrated intent confidence**. |

Accepted corrections keep `RawTranscript` unchanged and record the corrected text,
model, contextual score, and elapsed time in `stt:modelCorrectedTranscript`,
`stt:correctionModel`, `stt:correctionContextScore`, and `stt:correctionDurationMs`.
The existing turn-phase metrics record `asr_correction` outcomes. Repeating an
accepted command reuses its corrected normalized text without another inference.

Run unit tests and the real installed-model smoke/latency test:

```sh
dotnet test tests/Jibo.Cloud.Tests/Jibo.Cloud.Tests.csproj \
  --filter 'FullyQualifiedName~AsrModelFallbackTests|FullyQualifiedName~LocalAsrCorrectionModelTests' \
  --logger 'console;verbosity=detailed'
```

The neural integration test explicitly skips when local model assets are absent;
it never downloads them itself. Recognized commands incur no model inference.
Unrecognized commands can add up to the configured inference budget, so evaluate
latency and accidental corrections alongside error recovery on deployment hardware.

The installed-model test checks real-word recovery (`paper color`, `my time is it`,
`choke`/`joke`), grammar insertion, and unchanged ownership, negation, names,
numeric commands, unknown speech, and legitimate poem requests. Its timing loop
runs uncached model inference through the Python pipe. These examples verify
recovery behavior, not a population-level accuracy guarantee.
