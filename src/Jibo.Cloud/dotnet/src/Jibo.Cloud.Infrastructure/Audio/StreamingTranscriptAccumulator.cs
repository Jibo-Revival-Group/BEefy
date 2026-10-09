namespace Jibo.Cloud.Infrastructure.Audio;

// Called under the session lock. Partial hypotheses replace only the current
// segment; recognizer resets commit that segment without losing preceding words.
internal sealed class StreamingTranscriptAccumulator
{
    private string _committed = string.Empty;
    private string _current = string.Empty;
    public string Text => string.Join(" ", new[] { _committed, _current }.Where(s => s.Length > 0));
    public void Update(string text) => _current = text.Trim();
    public void Commit()
    {
        _committed = Text;
        _current = string.Empty;
    }
}
