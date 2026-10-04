using System.Text.RegularExpressions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Reads preference and word-of-the-day frames from a transcript.
/// Attribute lookup uses the longest catalog surface, so "ice cream flavor"
/// stays an attribute and "flavor" only fills the favorite slot.
/// </summary>
internal static class UtteranceFrameParser
{
    private readonly record struct PreferenceRoute(string? FavoriteIntent, string? LeastIntent);

    private static readonly Regex PossessiveFavoritePattern = new(
        AsrTokenLexicon.QuestionPossessive + @"\s+(?<least>least\s+)?" + AsrTokenLexicon.FavoriteSlot +
        @"\s+(?<attr>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HaveFavoritePattern = new(
        @"\bdo\s+you\s+have\s+an?\s+(?<least>least\s+)?" + AsrTokenLexicon.FavoriteSlot + @"\s+(?<attr>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex InvertedFavoritePattern = new(
        @"\bwhat\s+(?<attr>.+?)\s+is\s+(?:your|jibos?)\s+(?<least>least\s+)?" + AsrTokenLexicon.FavoriteSlot + @"\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LikeFramePattern = new(
        @"\bwhat\s+(?:kind\s+of\s+)?(?<attr>.+?)\s+d(?:o|id)\s+you\s+(?<verb>like|dislike)(?:\s+(?:the\s+)?(?<degree>best|least|most))?\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PastFavoritePattern = new(
        @"\b(?:what|which|who)\s+was\s+(?:your|jibos?)\s+(?<least>least\s+)?" + AsrTokenLexicon.FavoriteSlot +
        @"(?:\s+(?<attr>.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WordOfDayPattern = new(
        @"\b(?:(?:play|start|open|launch|begin|do)\s+)?" + AsrTokenLexicon.WordSlot + @"\s+" +
        AsrTokenLexicon.WordOfDayLinker + @"\s+" + AsrTokenLexicon.DaySlot + @"\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Dictionary<string, PreferenceRoute> Routes = BuildRoutes();

    internal static IEnumerable<string> CorrectionCandidates() => Routes
        .Where(route => route.Value.FavoriteIntent is not null)
        .SelectMany(route => new[] { $"what is your favorite {route.Key}", $"do you have a favorite {route.Key}" });

    internal static string? TryParsePreference(string? transcript)
    {
        var text = Prepare(transcript);
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (TryResolveKnown(PossessiveFavoritePattern, text, out var intent) ||
            TryResolveKnown(HaveFavoritePattern, text, out intent) ||
            TryResolveKnown(InvertedFavoritePattern, text, out intent) ||
            TryResolveKnown(LikeFramePattern, text, out intent) ||
            TryResolveKnown(PastFavoritePattern, text, out intent))
            return intent;

        return null;
    }

    internal static string? TryParsePastPreference(string? transcript)
    {
        var text = Prepare(transcript);
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = PastFavoritePattern.Match(text);
        if (!match.Success) return null;

        if (TryIntent(match, out var intent)) return intent;

        // Unknown or missing subject only when the past question is the whole utterance.
        return match.Index == 0 ? "robot_did_you_have_a_favorite" : null;
    }

    internal static string? TryParseWordOfTheDay(string? transcript)
    {
        var text = Prepare(transcript);
        if (string.IsNullOrWhiteSpace(text)) return null;

        // A preference question ("what's your favorite word") is not a launch.
        if (AsrTokenLexicon.FavoriteSlotPattern.IsMatch(text)) return null;

        return WordOfDayPattern.IsMatch(text) ? "word_of_the_day" : null;
    }

    private static bool TryResolveKnown(Regex pattern, string text, out string? intent)
    {
        intent = null;
        var match = pattern.Match(text);
        if (!match.Success) return false;

        return TryIntent(match, out intent);
    }

    private static bool TryIntent(Match match, out string? intent)
    {
        intent = null;
        var attribute = CleanupAttribute(match.Groups["attr"].Value);
        var least = IsLeast(match);
        if (string.IsNullOrWhiteSpace(attribute)) return false;

        if (TryTimeOfDayAlias(attribute, match, least, out intent))
            return intent is not null;

        if (!TryRoute(attribute, out var route)) return false;

        intent = least ? route.LeastIntent : route.FavoriteIntent;
        return intent is not null;
    }

    private static bool TryRoute(string attribute, out PreferenceRoute route)
    {
        PreferenceRoute? bestExact = null;
        var bestExactLength = -1;
        PreferenceRoute? bestPrefix = null;
        var bestPrefixLength = -1;

        foreach (var (surface, candidate) in Routes)
        {
            if (IsSameTokenSequence(attribute, surface))
            {
                if (surface.Length <= bestExactLength) continue;
                bestExact = candidate;
                bestExactLength = surface.Length;
                continue;
            }

            if (surface.Length <= bestPrefixLength) continue;
            if (!StartsWithTokenSequence(attribute, surface)) continue;
            bestPrefix = candidate;
            bestPrefixLength = surface.Length;
        }

        if (bestExact is { } exact)
        {
            route = exact;
            return true;
        }

        if (bestPrefix is { } prefix)
        {
            route = prefix;
            return true;
        }

        var head = attribute.Split(' ', 2)[0];
        if ((AsrTokenLexicon.ColorAttributePattern.IsMatch(attribute) ||
             AsrTokenLexicon.ColorAttributePattern.IsMatch(head) ||
             EquivalentWordLexicon.AreEquivalent(head, "color") ||
             EquivalentWordLexicon.AreEquivalent(head, "colour")) &&
            Routes.TryGetValue("color", out route))
            return true;

        route = default;
        return false;
    }

    // Correction uses whole catalog subjects, never the prefix matching used by routing.
    internal static bool TryCanonicalizePreferenceAttribute(string attribute, out string canonical)
    {
        if (Routes.ContainsKey(attribute))
        {
            canonical = attribute;
            return true;
        }

        foreach (var surface in Routes.Keys)
        {
            if (!IsSameTokenSequence(attribute, surface)) continue;
            canonical = surface;
            return true;
        }

        if (AsrTokenLexicon.ColorAttributePattern.IsMatch(attribute))
        {
            canonical = "color";
            return true;
        }

        canonical = attribute;
        return false;
    }

    private static bool IsSameTokenSequence(string attribute, string surface)
    {
        var attributeTokens = attribute.Split(' ');
        var surfaceTokens = surface.Split(' ');
        if (attributeTokens.Length != surfaceTokens.Length) return false;

        for (var index = 0; index < attributeTokens.Length; index++)
        {
            if (!EquivalentWordLexicon.AreEquivalent(attributeTokens[index], surfaceTokens[index]))
                return false;
        }

        return true;
    }

    private static bool StartsWithTokenSequence(string attribute, string surface)
    {
        var attributeTokens = attribute.Split(' ');
        var surfaceTokens = surface.Split(' ');
        if (attributeTokens.Length <= surfaceTokens.Length) return false;

        for (var index = 0; index < surfaceTokens.Length; index++)
        {
            if (!EquivalentWordLexicon.AreEquivalent(attributeTokens[index], surfaceTokens[index]))
                return false;
        }

        return true;
    }

    private static bool TryTimeOfDayAlias(string attribute, Match match, bool least, out string? intent)
    {
        intent = null;
        if (!EquivalentWordLexicon.AreEquivalent(attribute, "time")) return false;

        var degree = match.Groups["degree"].Value;
        var verb = match.Groups["verb"].Value;
        if (degree is not ("best" or "most" or "least") && verb is not "dislike")
            return false;

        intent = least
            ? "robot_least_favorite_time_of_day"
            : "robot_favorite_time_of_day";
        return true;
    }

    private static bool IsLeast(Match match)
    {
        if (match.Groups["least"].Success) return true;

        var verb = match.Groups["verb"].Value;
        var degree = match.Groups["degree"].Value;
        return verb is "dislike" || degree is "least";
    }

    private static string CleanupAttribute(string value)
    {
        var attribute = value.Trim();
        if (attribute.StartsWith("the ", StringComparison.Ordinal))
            attribute = attribute[4..];

        return attribute.Trim();
    }

    private static string Prepare(string? transcript)
    {
        var normalized = TranscriptTextNormalizer.NormalizeLooseText(transcript);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        normalized = AsrGrammarCorrector.Correct(normalized);
        normalized = normalized.Replace("'", string.Empty, StringComparison.Ordinal);
        return TranscriptTextNormalizer.StripTrailingCourtesyWords(normalized);
    }

    private static Dictionary<string, PreferenceRoute> BuildRoutes()
    {
        var routes = new Dictionary<string, PreferenceRoute>(StringComparer.Ordinal);

        void Add(string surface, string? favorite, string? least = null) =>
            routes.Add(surface, new PreferenceRoute(favorite, least));

        Add("winter olympics event", "robot_favorite_winter_olympics_event");
        Add("winter x games event", "robot_favorite_winter_x_games_event");
        Add("super bowl commercial", "robot_favorite_super_bowl_commercial");
        Add("star wars character", "robot_favorite_star_wars_character");
        Add("movie from star wars", "robot_favorite_star_wars_movie");
        Add("star wars movie", "robot_favorite_star_wars_movie");
        Add("part of the today show", "robot_favorite_part_of_today_show");
        Add("part of today show", "robot_favorite_part_of_today_show");
        Add("thanksgiving food", "robot_favorite_thanksgiving_food");
        Add("country musician", "robot_favorite_country_musician");
        Add("country singer", "robot_favorite_country_musician");
        Add("halloween candy", "robot_favorite_halloween_candy");
        Add("christmas movie", "robot_favorite_christmas_movie");
        Add("holiday movie", "robot_favorite_christmas_movie");
        Add("christmas song", "robot_favorite_holiday_song");
        Add("holiday song", "robot_favorite_holiday_song");
        Add("music genre", "robot_favorite_music_genre");
        Add("kind of music", "robot_favorite_music_genre");
        Add("ice cream flavor", "robot_favorite_ice_cream_flavor");
        Add("ice cream flavour", "robot_favorite_ice_cream_flavor");
        Add("olympic event", "robot_favorite_olympic_event");
        Add("olympic ring", "robot_favorite_olympic_ring");
        Add("pizza topping", "robot_favorite_pizza_topping", "robot_least_favorite_pizza_topping");
        Add("basketball team", "robot_favorite_basketball_team");
        Add("baseball team", "robot_favorite_baseball_team");
        Add("football team", "robot_favorite_football_team");
        Add("hockey team", "robot_favorite_hockey_team");
        Add("time of day", "robot_favorite_time_of_day", "robot_least_favorite_time_of_day");
        Add("scary movie", "robot_favorite_scary_movie");
        Add("video game", "robot_favorite_video_game", "robot_least_favorite_video_game");
        Add("rock band", "robot_favorite_rock_band");
        Add("part of vegas", "robot_favorite_part_of_vegas");
        Add("part of ces", "robot_favorite_part_of_ces");
        Add("tv show", "robot_favorite_tv_show");
        Add("superhero", "robot_favorite_superhero");
        Add("adjective", "robot_favorite_adjective", "robot_least_favorite_adjective");
        Add("vegetable", "robot_favorite_vegetable", "robot_least_favorite_vegetable");
        Add("celebrity", "robot_favorite_celebrity", "robot_least_favorite_celebrity");
        Add("president", "robot_favorite_president", "robot_least_favorite_president");
        Add("reindeer", "robot_favorite_reindeer");
        Add("pastime", "robot_favorite_pastime");
        Add("dessert", "robot_favorite_dessert");
        Add("weather", "robot_favorite_weather", "robot_least_favorite_weather");
        Add("holiday", "robot_favorite_holiday");
        Add("mammal", "robot_favorite_mammal", "robot_least_favorite_mammal");
        Add("number", "robot_favorite_number", "robot_least_favorite_number");
        Add("planet", "robot_favorite_planet");
        Add("rapper", "robot_favorite_rapper");
        Add("season", "robot_favorite_season");
        Add("singer", "robot_favorite_singer");
        Add("artist", "robot_favorite_artist", "robot_least_favorite_artist");
        Add("author", "robot_favorite_author", "robot_least_favorite_author");
        Add("flower", "robot_favorite_flower");
        Add("animal", "robot_favorite_animal", "robot_least_favorite_animal");
        Add("actress", "robot_favorite_actress");
        Add("painter", "robot_favorite_painter");
        Add("hobby", "robot_favorite_hobby");
        Add("human", "robot_favorite_human");
        Add("person", "robot_favorite_human");
        Add("candy", "robot_favorite_candy");
        Add("drink", "robot_favorite_drink");
        Add("fruit", "robot_favorite_fruit");
        Add("movie", "robot_favorite_movie", "robot_least_favorite_movie");
        Add("music", "robot_favorite_music");
        Add("place", "robot_favorite_place", "robot_least_favorite_place");
        Add("shape", "robot_favorite_shape");
        Add("smell", "robot_favorite_smell", "robot_least_favorite_smell");
        Add("smells", "robot_favorite_smell", "robot_least_favorite_smell");
        Add("sport", "robot_favorite_sport");
        Add("thing", "robot_favorite_thing");
        Add("actor", "robot_favorite_actor");
        Add("dance", "robot_favorite_dance");
        Add("robot", "robot_favorite_robot");
        Add("color", "robot_favorite_color", "robot_least_favorite_color");
        Add("food", "robot_favorite_food", "robot_least_favorite_food");
        Add("joke", "robot_favorite_joke");
        Add("book", "robot_favorite_book");
        Add("bird", "robot_favorite_bird", "robot_least_favorite_bird");
        Add("fish", "robot_favorite_fish");
        Add("game", "robot_favorite_video_game", "robot_least_favorite_video_game");
        Add("band", "robot_favorite_various_styles_band", "robot_least_favorite_band");
        Add("name", "robot_favorite_name");
        Add("noun", "robot_favorite_noun", "robot_least_favorite_noun");
        Add("verb", "robot_favorite_verb", "robot_least_favorite_verb");
        Add("word", "robot_favorite_word", "robot_least_favorite_word");
        Add("song", "robot_favorite_song");
        Add("pet", "robot_favorite_pet");
        Add("car", "robot_favorite_car", "robot_least_favorite_car");

        return routes;
    }
}
