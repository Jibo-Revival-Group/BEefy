namespace Jibo.Cloud.Application.Services;

/// <summary>Shared supported phrases for routing and contextual speech recovery.</summary>
public static class AsrCommandCatalog
{
    internal static readonly string[] Joke =
    [
        "joke",
        "knock knock",
        "nock nock",
        "funny",
        "make me laugh",
        "tell me a joke",
        "tell a joke",
    ];
    internal static readonly string[] Story =
    [
        "tell me a story",
        "can you tell me a story",
        "could you tell me a story",
        "can you tell me a bedtime story",
        "could you tell me a bedtime story",
        "read me a story",
        "read a story",
    ];
    internal static readonly string[] OrderPizza =
    [
        "can you order pizza",
        "can you order a pizza",
        "could you order a pizza",
        "order pizza",
        "order a pizza",
        "order us a pizza",
        "order me a pizza",
        "please order pizza",
    ];
    internal static readonly string[] Pizza =
    [
        "can you cook us a pizza",
        "flip a pizza",
        "make a pizza",
        "make pizza",
        "show pizza",
        "can you make pizza",
        "let's make pizza",
        "lets make pizza",
    ];
    internal static readonly string[] Time =
    [
        "time",
        "the time",
        "current time",
        "what time is it",
        "what s the time",
        "what's the time",
        "what is the time",
    ];
    internal static readonly string[] Date =
    [
        "what is the date",
        "what s the date",
        "what's the date",
        "what date is it",
        "today s date",
        "today date",
        "what's today's date",
        "what is today s date",
        "what s today s date",
        "what's today s date",
        "what's todays date",
        "what is todays date",
        "what s todays date",
    ];
    internal static readonly string[] Dance =
    [
        "dance",
        "boogie",
        "do a dance",
        "do your dance",
        "show me a dance",
        "show us a dance",
        "show me your dance",
        "show us your dance",
        "dance for me",
        "dance for us",
        "dance with me",
        "dance with us",
        "bust a move",
        "bust some moves",
        "do some dancing",
        "start dancing",
        "lets dance",
        "let s dance",
    ];
    internal static readonly string[] Weather =
    [
        "weather",
        "forecast",
        "how is the weather",
        "how s the weather",
        "how's the weather",
        "check the weather",
        "weather report",
        "what's today s weather",
        "what's today's weather",
        "what is the weather",
        "what will the weather",
        "what will tomorrow s weather",
        "what will tomorrow's weather",
        "look up the forecast",
        "launch the weather skill",
        "what is today s humidity",
        "what is today's humidity",
        "what's the humidity",
        "what is the humidity",
        "what's today's forecast",
        "what s today's forecast",
        "what s today s forecast",
        "what is today s forecast",
        "what is today's forecast",
        "what's today's weather look like",
        "what s today's weather look like",
        "what s today s weather look like",
        "what is today s weather look like",
        "what is today's weather look like",
        "what's the leather",
        "whats the leather",
        "what s the leather",
        "what is the leather",
        "how's the leather",
        "how s the leather",
        "how is the leather",
        "check the leather",
        "the leather",
    ];
    internal static readonly string[] Greeting =
    [
        "hello",
        "hi",
        "hey",
        "hello jibo",
        "hi jibo",
        "hey jibo",
    ];

    public static IReadOnlyList<string> Candidates { get; } = UtteranceFrameParser.CorrectionCandidates()
        .Concat(Joke)
        .Concat(Story)
        .Concat(OrderPizza)
        .Concat(Pizza)
        .Concat(Time)
        .Concat(Date)
        .Concat(Dance)
        .Concat(Weather)
        .Concat(Greeting)
        .Concat(new[] { "what day is it", "what is your name", "how old are you",
            "where are you from", "what can you do" })
        .Select(TranscriptTextNormalizer.NormalizeLooseText)
        .Where(phrase => !phrase.Contains("leather", StringComparison.Ordinal))
        .Where(phrase => phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2)
        .Distinct(StringComparer.Ordinal).ToArray();
}
