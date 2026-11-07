using System.Net;
using System.Text;
using Jibo.Cloud.Infrastructure.Sports;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

// ESPN responses trimmed to the fields the provider reads; the phrasings are real prod questions.
public sealed class EspnSportsScoreProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 13, 0, 0, TimeSpan.FromHours(-5));

    private const string MlbTeams = """{"sports":[{"leagues":[{"teams":[{"team":{"id":"2","name":"Red Sox","location":"Boston"}},{"team":{"id":"26","name":"Giants","location":"San Francisco"}}]}]}]}""";
    private const string NflTeams = """{"sports":[{"leagues":[{"teams":[{"team":{"id":"8","name":"Lions","location":"Detroit"}},{"team":{"id":"19","name":"Giants","location":"New York"}}]}]}]}""";
    private const string NbaTeams = """{"sports":[{"leagues":[{"teams":[{"team":{"id":"15","name":"Bucks","location":"Milwaukee"}}]}]}]}""";
    private const string NhlTeams = """{"sports":[{"leagues":[{"teams":[{"team":{"id":"16","name":"Penguins","location":"Pittsburgh"}},{"team":{"id":"23","name":"Capitals","location":"Washington"}}]}]}]}""";

    private static string Event(string date, string state, int period, (string Id, string Name, string? Score, bool? Winner) a,
        (string Id, string Name, string? Score, bool? Winner) b, bool scheduleShape = false)
    {
        string Side((string Id, string Name, string? Score, bool? Winner) s) =>
            "{\"id\":\"" + s.Id + "\",\"team\":{\"shortDisplayName\":\"" + s.Name + "\"}" +
            (s.Score is null ? "" : scheduleShape ? ",\"score\":{\"value\":" + s.Score + ".0,\"displayValue\":\"" + s.Score + "\"}" : ",\"score\":\"" + s.Score + "\"") +
            (s.Winner is null ? "" : ",\"winner\":" + (s.Winner.Value ? "true" : "false")) + "}";
        return "{\"date\":\"" + date + "\",\"competitions\":[{\"status\":{\"period\":" + period + ",\"type\":{\"state\":\"" + state +
               "\"}},\"competitors\":[" + Side(a) + "," + Side(b) + "]}]}";
    }

    private static readonly string NhlScoreboard = "{\"events\":[" +
        Event("2026-11-08T02:00Z", "pre", 0, ("16", "Penguins", null, null), ("8", "Kings", null, null)) + "," +
        Event("2026-11-07T17:30Z", "in", 2, ("23", "Capitals", "1", null), ("26", "Panthers", "2", null)) + "]}";

    private static readonly string LionsSchedule = "{\"events\":[" +
        Event("2026-10-26T17:00Z", "post", 4, ("8", "Lions", "10", false), ("3", "Bears", "17", true), true) + "," +
        Event("2026-11-01T18:00Z", "post", 4, ("8", "Lions", "19", true), ("16", "Vikings", "15", false), true) + "," +
        Event("2026-11-15T18:00Z", "pre", 0, ("8", "Lions", null, null), ("21", "Eagles", null, null), true) + "]}";

    private static EspnSportsScoreProvider Create(Func<string, string?> body) =>
        new(new HttpClient(new Handler(body)), NullLogger<EspnSportsScoreProvider>.Instance);

    private static string? Espn(string url) => url switch
    {
        _ when url.Contains("baseball/mlb/teams?") || url.EndsWith("baseball/mlb/teams") => MlbTeams,
        _ when url.EndsWith("football/nfl/teams") => NflTeams,
        _ when url.EndsWith("basketball/nba/teams") => NbaTeams,
        _ when url.EndsWith("hockey/nhl/teams") => NhlTeams,
        _ when url.Contains("hockey/nhl/scoreboard?dates=20261107") => NhlScoreboard,
        _ when url.Contains("/scoreboard?dates=20261107") => """{"events":[]}""",
        _ when url.EndsWith("football/nfl/teams/8/schedule") => LionsSchedule,
        _ when url.EndsWith("baseball/mlb/teams/2/schedule") => "{\"events\":[" +
            Event("2026-09-30T23:00Z", "post", 9, ("2", "Red Sox", "2", false), ("10", "Yankees", "9", true), true) + "]}",
        _ when url.EndsWith("baseball/mlb/teams/2") => "{\"team\":{\"nextEvent\":[" +
            Event("2027-02-20T18:05Z", "pre", 0, ("2", "Red Sox", null, null), ("30", "Rays", null, null)) + "]}}",
        _ when url.EndsWith("basketball/nba/teams/15/schedule") => "{\"events\":[" +
            Event("2026-11-08T23:00Z", "pre", 0, ("15", "Bucks", null, null), ("2", "Celtics", null, null), true) + "]}",
        _ when url.EndsWith("football/nfl/teams/19/schedule") => "{\"events\":[" +
            Event("2026-11-02T18:00Z", "post", 4, ("19", "Giants", "27", true), ("34", "Texans", "21", false), true) + "]}",
        _ when url.EndsWith("baseball/mlb/teams/26/schedule") => "{\"events\":[" +
            Event("2026-09-28T20:00Z", "post", 9, ("26", "Giants", "1", false), ("19", "Dodgers", "4", true), true) + "]}",
        _ when url.Contains("/teams/") => """{"team":{}}""",
        _ => null
    };

    [Theory]
    [InlineData("are the red sox's playing tonight", "The Red Sox don't play today. Their next game is Saturday, February 20 against the Rays.")]
    [InlineData("did the red sox win", "The Red Sox lost to the Yankees 9 to 2 on Wednesday, September 30.")]
    [InlineData("did the pittsburgh penguins have a game tonight", "Yes, the Penguins play the Kings tonight at 9 PM.")]
    [InlineData("did the penguins play tonight", "Yes, the Penguins play the Kings tonight at 9 PM.")]
    [InlineData("what's the capitals score", "The Capitals are losing to the Panthers 2 to 1 in the 2nd period.")]
    [InlineData("when did the lions play this weekend", "The Lions beat the Vikings 19 to 15 on Sunday, November 1.")]
    [InlineData("how did the detroit lions do", "The Lions beat the Vikings 19 to 15 on Sunday, November 1.")]
    [InlineData("who's gonna win the bucks game today", "The Bucks don't play today. Their next game is Sunday, November 8 against the Celtics.")]
    [InlineData("did the penguins win", "The Penguins play the Kings tonight at 9 PM.")]
    [InlineData("did the giants win", "The Giants beat the Texans 27 to 21 on Monday, November 2.")]
    [InlineData("did the giants win in baseball", "The Giants lost to the Dodgers 4 to 1 on Monday, September 28.")]
    public async Task AnswerAsync_ProdQuestions(string question, string expected)
    {
        Assert.Equal(expected, await Create(Espn).AnswerAsync(question, Now));
    }

    [Theory]
    [InlineData("is it gonna rain today")]
    [InlineData("what game should we play")]
    [InlineData("i'm gonna watch thiago play uh guitar in the his capitals combo")]
    [InlineData("uh play one penguins collapse")]
    public async Task AnswerAsync_NoTeam_LeavesItToOtherRoutes(string question)
    {
        Assert.Null(await Create(Espn).AnswerAsync(question, Now));
    }

    [Fact]
    public async Task AnswerAsync_EspnDown_SaysSoInsteadOfGuessing()
    {
        var answer = await Create(url => url.EndsWith("/teams") ? Espn(url) : null).AnswerAsync("did the lions win", Now);
        Assert.Equal("I couldn't get the Lions score right now. Try again in a bit.", answer);
    }

    private sealed class Handler(Func<string, string?> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = body(request.RequestUri!.ToString());
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
