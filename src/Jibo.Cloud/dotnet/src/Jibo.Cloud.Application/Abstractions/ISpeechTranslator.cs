namespace Jibo.Cloud.Application.Abstractions;

/// <summary>What Jibo should say in another language, as markup for his own voice.</summary>
/// <param name="Translation">The translated sentence(s), for logs and tests.</param>
/// <param name="Parts">TTS markup to speak in order, without the outer speak tag.</param>
public sealed record TranslatedSpeech(string Translation, IReadOnlyList<string> Parts);

public interface ISpeechTranslator
{
    /// <summary>Null when the language is not offered, the worker is not installed, or it took too long.</summary>
    Task<TranslatedSpeech?> TryTranslateAsync(string language, string text, CancellationToken cancellationToken = default);
}
