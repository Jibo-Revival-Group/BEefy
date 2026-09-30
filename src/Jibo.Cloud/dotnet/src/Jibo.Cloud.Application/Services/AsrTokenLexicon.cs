using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Span matchers for ASR confusions. These fire inside a parsed frame, so a
/// token like "flavor" is only treated as "favorite" when the surrounding
/// question asks for a preference.
/// </summary>
internal static class AsrTokenLexicon
{
    // Apostrophes are stripped before matching, so "what's" is "whats".
    internal const string QuestionPossessive =
        @"\b(?:what(?:s|\s+s|\s+is)?|who(?:s|\s+s|\s+is)?|which(?:\s+is)?|where(?:s|\s+s|\s+is)?|buts|butts|wats)\s+(?:you|your|jibos?)\b";

    // Longer alternatives first so "favor to" wins over "favor".
    // "favorit" / "favourit" are the same slot with the final e dropped.
    internal const string FavoriteSlot =
        @"\b(?:favorites|favourites|favorite|favourite|favou?rits?|favou?r\s+to|flavou?rs?|favou?rs?|fave)\b";

    // "work" and "ward" stand in for "word" only inside the word-of-the-day frame.
    internal const string WordSlot = @"\b(?:words?|works?|wards?)\b";

    internal const string DaySlot = @"\b(?:days?|dates?)\b";

    // "the" and "a" are the same article here, including the mishear "da".
    internal const string WordOfDayLinker = @"(?:of\s+(?:the|a|da)|the|a)";

    internal static readonly Regex ColorAttributePattern = new(
        @"^(?:colou?rs?|collars?)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static readonly Regex FavoriteSlotPattern = new(
        FavoriteSlot,
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
