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
