using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService(
    JiboExperienceContentCache contentCache,
    IJiboRandomizer randomizer,
    IPersonalMemoryStore personalMemoryStore,
    IWeatherReportProvider? weatherReportProvider = null,
    ICalendarReportProvider? calendarReportProvider = null,
    ICommuteReportProvider? commuteReportProvider = null,
    INewsBriefingProvider? newsBriefingProvider = null,
    IFunFactProvider? funFactProvider = null,
    IWordDefinitionProvider? wordDefinitionProvider = null,
    IWikipediaSummaryProvider? wikipediaSummaryProvider = null,
    IHolidayCountdownCatalog? holidayCountdownCatalog = null,
    IMeasurementConversionCatalog? measurementConversionCatalog = null,
    IKnowledgeSearchService? knowledgeSearchService = null,
    ITurnProgressPublisher? turnProgressPublisher = null,
    ICloudStateStore? cloudStateStore = null,
    JiboVerificationService? jiboVerificationService = null,
    HomeAssistantCommandService? homeAssistantCommandService = null,
    HomeAssistantPendingClimateStore? homeAssistantPendingClimateStore = null,
    RepeatLastCommandStore? repeatLastCommandStore = null,
    ILogger<JiboInteractionService>? logger = null,
    IAsrCorrectionModel? asrCorrectionModel = null,
    AsrCorrectionOptions? asrCorrectionOptions = null,
    ITransportMetrics? transportMetrics = null,
    INluClassifier? nluClassifier = null,
    IConversationHistory? conversationHistory = null)
{
    private readonly AsrCorrectionOptions _asrCorrectionOptions = asrCorrectionOptions ?? new();
    private readonly ITransportMetrics _asrCorrectionMetrics = transportMetrics ?? NullTransportMetrics.Instance;

    private const string GreetingRouteMetadataKey = "greetingsRoute";
    private const string GreetingSpeakerMetadataKey = "greetingsSpeaker";
    private const string LastProactiveGreetingUtcMetadataKey = "greetingsLastProactiveUtc";
    private const string LastReactiveGreetingUtcMetadataKey = "greetingsLastReactiveUtc";

    private const int MaxWeatherForecastDayOffset = 5;
    private const int MaxNewsHeadlines = 3;
    private const int MaxPreferredNewsCategories = 2;

    private static readonly Regex SplitAlarmPattern = new(
        @"\b(?<hour>\d{1,2}|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)(?:[:\s,-]+(?<minute>\d{2}|[a-z\-]+(?:\s+[a-z\-]+)?))?\s*(?<ampm>a[\s\.]*m\.?|p[\s\.]*m\.?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex CompactAlarmPattern = new(
        @"\b(?<compact>\d{3,4})\s*(?<ampm>a[\s\.]*m\.?|p[\s\.]*m\.?)?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VolumeLevelPattern = new(
        @"\b(?:volume|loudness)\s*(?:to|at|level|is)?\s*(?<value>10|\d|one|two|three|four|five|six|seven|eight|nine|ten)\b|\b(?:set|change|make|turn)\s+(?:the\s+|your\s+)?(?:volume|loudness)\s*(?:to|at)?\s*(?<value>10|\d|one|two|three|four|five|six|seven|eight|nine|ten)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex VolumeToValueHomophonePattern = new(
        @"\b(?:volume|loudness)\s+(?:2|two|to)\s+(?<value>10|\d|one|two|three|four|five|six|seven|eight|nine|ten)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] CommandLeadPhrases =
    [
        "hey jibo",
        "hello jibo",
        "hi jibo",
        "jibo",
        "o",
        "oh",
        "so",
        "well",
        "um",
        "uh",
        "hmm",
        "erm",
        "ah",
        "please",
        "ok jibo",
        "okay jibo"
    ];

    private static readonly Regex AlarmDeletePattern = new(
        @"\b(?:cancel|delete|remove|stop|turn\s+off)\s+(?:the\s+)?(?:alarm|along|elo)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherLocationPattern = new(
        @"\b(?:in|for|at)\s+(?<location>[a-z][a-z\s'\-]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherLocationSuffixPattern = new(
        @"\b(?:today|tonight|tomorrow|day after tomorrow|outside|right now|please|thanks|this weekend|next weekend|the weekend|weekend|this week|next week|on monday|on tuesday|on wednesday|on thursday|on friday|on saturday|on sunday|this monday|this tuesday|this wednesday|this thursday|this friday|this saturday|this sunday|next monday|next tuesday|next wednesday|next thursday|next friday|next saturday|next sunday|monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherConditionForecastPattern = new(
        @"\bwill it be\s+(sunny|cloudy|windy|foggy|stormy|rainy|snowy|hail|hailing)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherTopicLocationPattern = new(
        @"\b(?:weather|forecast|temperature|humidity)\b.*\b(?:in|for|at)\s+[a-z]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WeatherDayOfWeekPattern = new(
        @"\b(?<next>next\s+)?(?<this>this\s+)?(?:on\s+)?(?<day>monday|tuesday|wednesday|thursday|friday|saturday|sunday)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly PizzaMimPrompt[] PizzaMimPrompts =
    [
        new("RA_JBO_ShowPizzaMaking_AN_01", "<speak><anim cat='jiboji' filter='pizza-making'/></speak>"),
        new("RA_JBO_ShowPizzaMaking_AN_02",
            "<speak><anim cat='jiboji' filter='pizza-making' nonBlocking='true'/><pitch mult='1.2'>One </pitch> pizza, coming right up.</speak>"),
        new("RA_JBO_ShowPizzaMaking_AN_03",
            "<speak><anim cat='jiboji' filter='pizza-making' nonBlocking='true'/>My <pitch mult='1.2'>specialty </pitch>.</speak>")
    ];

    private static readonly string[] WeatherDateEntityKeys =
    [
        "date",
        "sys.date",
        "datetime",
        "dateTime",
        "date_time",
        "day"
    ];

    private static readonly string[] YesNoAcknowledgementPrefixes =
    [
        "uh",
        "um",
        "hmm",
        "well",
        "so",
        "actually",
        "honestly",
        "thanks",
        "thank you"
    ];

    private static readonly HashSet<string> YesNoAffirmativeLeadTokens = new(StringComparer.Ordinal)
    {
        "yes",
        "yeah",
        "yep",
        "yup",
        "sure",
        "ok",
        "okay",
        "absolutely",
        "affirmative",
        "definitely",
        "certainly",
        "indeed"
    };

    private static readonly HashSet<string> YesNoNegativeLeadTokens = new(StringComparer.Ordinal)
    {
        "no",
        "nope",
        "nah",
        "negative",
        "never"
    };

    private static readonly HashSet<string> YesNoAffirmativeLeadPhrases = new(StringComparer.Ordinal)
    {
        "uh huh",
        "sounds good",
        "sure thing",
        "why not",
        "please do",
        "go ahead",
        "of course",
        "i guess so",
        "i think so"
    };

    private static readonly HashSet<string> YesNoNegativeLeadPhrases = new(StringComparer.Ordinal)
    {
        "not now",
        "not today",
        "not really",
        "no thanks",
        "no thank you",
        "maybe later",
        "i guess not",
        "i do not",
        "i dont",
        "i don t"
    };

    private static readonly HashSet<string> GenericWeatherLocationTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "here",
        "hear",
        "her",
        "inside",
        "indoors",
        "this room",
        "the room",
        "my room",
        "our room",
        "my area",
        "our area",
        "this area",
        "the area",
        "the city",
        "this city",
        "our city",
        "my city",
        "the town",
        "this town",
        "our town",
        "my town",
        "our street",
        "this street",
        "my street",
        "the neighborhood",
        "the neighbourhood",
        "this neighborhood",
        "this neighbourhood",
        "our neighborhood",
        "our neighbourhood"
    };

    private static readonly HashSet<string> SpokenAbbreviationTokens = new(StringComparer.Ordinal)
    {
        "US",
        "USA",
        "UK",
        "UAE",
        "EU",
        "DC",
        "AL",
        "AK",
        "AZ",
        "AR",
        "CA",
        "CO",
        "CT",
        "DE",
        "FL",
        "GA",
        "HI",
        "ID",
        "IL",
        "IN",
        "IA",
        "KS",
        "KY",
        "LA",
        "ME",
        "MD",
        "MA",
        "MI",
        "MN",
        "MS",
        "MO",
        "MT",
        "NE",
        "NV",
        "NH",
        "NJ",
        "NM",
        "NY",
        "NC",
        "ND",
        "OH",
        "OK",
        "OR",
        "PA",
        "RI",
        "SC",
        "SD",
        "TN",
        "TX",
        "UT",
        "VT",
        "VA",
        "WA",
        "WV",
        "WI",
        "WY"
    };

    private static readonly TimeSpan ProactiveGreetingCooldown = TimeSpan.FromMinutes(20);

    private static readonly (string Keyword, string Category)[] NewsCategoryKeywordMap =
    [
        ("sports", "sports"),
        ("sport", "sports"),
        ("football", "sports"),
        ("baseball", "sports"),
        ("basketball", "sports"),
        ("hockey", "sports"),
        ("technology", "technology"),
        ("tech", "technology"),
        ("ai", "technology"),
        ("a i", "technology"),
        ("a eye", "technology"),
        ("aye eye", "technology"),
        ("artificial intelligence", "technology"),
        ("science", "science"),
        ("business", "business"),
        ("finance", "business"),
        ("market", "business"),
        ("stock", "business"),
        ("politics", "general"),
        ("political", "general"),
        ("world", "general"),
        ("entertainment", "entertainment"),
        ("movie", "entertainment"),
        ("music", "entertainment")
    ];

    private static readonly (string Phrase, string Station)[] RadioGenreAliases =
    [
        ("country music", "Country"),
        ("country radio", "Country"),
        ("country", "Country"),
        ("football", "Sports"),
        ("sports", "Sports"),
        ("classic rock", "ClassicRock"),
        ("soft rock", "SoftRock"),
        ("hip hop", "HipHop"),
        ("hip-hop", "HipHop"),
        ("news and talk", "NewsAndTalk"),
        ("news talk", "NewsAndTalk"),
        ("news radio", "NewsAndTalk"),
        ("sports radio", "Sports"),
        ("christian music", "ChristianAndGospel"),
        ("gospel music", "ChristianAndGospel"),
        ("oldies", "Oldies"),
        ("pop music", "Pop"),
        ("jazz", "Jazz"),
        ("latin music", "Latin"),
        ("dance music", "Dance"),
        ("reggae", "ReggaeAndIsland"),
        ("island music", "ReggaeAndIsland"),
        ("alternative", "Alternative"),
        ("blues", "Blues"),
        ("classical music", "Classical"),
        ("classical", "Classical"),
        ("college radio", "CollegeRadio"),
        ("comedy radio", "Comedy"),
        ("npr", "NPR")
    ];

    public async Task<JiboInteractionDecision> BuildDecisionAsync(TurnContext turn,
        CancellationToken cancellationToken = default)
    {
        var decision = await BuildDecisionCoreAsync(turn, cancellationToken);
        if (SkillListenOwnership.ReadListenHotphrase(turn) &&
            NativeConversationValue.Read(decision.ContextUpdates, "chitchatNativeState") is not "color")
        {
            var updates = new Dictionary<string, object?>(decision.ContextUpdates ?? new Dictionary<string, object?>())
            { ["chitchatNativeState"] = null };
            decision = decision with { ContextUpdates = updates };
        }
        RecordRepeatableCommand(turn, decision);
        if (conversationHistory is not null && decision.IntentName is not ("skill_listen" or "prompt_echo" or "trigger_ignored"))
        {
            try
            {
                conversationHistory.Record(new ConversationHistoryRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow,
                    turn.DeviceId ?? "", turn.SessionId ?? "", decision.SkillName ?? "chitchat-skill", decision.IntentName,
                    turn.NormalizedTranscript ?? turn.RawTranscript, decision.ReplyText,
                    System.Text.Json.JsonSerializer.Serialize(new
                    {
                        intent = (NativeConversationValue.Read(turn.Attributes, NativeParseAttribute) as NativeParseResult)?.Intent
                            ?? NativeConversationValue.Read(decision.SkillPayload, "localIntent")?.ToString() ?? decision.IntentName,
                        entities = (NativeConversationValue.Read(turn.Attributes, NativeParseAttribute) as NativeParseResult)?.Entities
                            ?? ReadEntities(turn).ToDictionary(p => p.Key, p => (object?)p.Value)
                    })));
            }
            catch (Exception exception)
            {
                logger?.LogWarning(exception, "Conversation history could not be recorded for {RobotId}", turn.DeviceId);
            }
        }
        return decision;
    }
}
