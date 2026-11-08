using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Jibo.Cloud.Infrastructure.Sports;

/// <summary>
/// Answers "are the Red Sox playing tonight", "did the Penguins win", "how did the Cowboys do" from ESPN's
/// public site API (no key). shortcut: MLB/NFL/NBA/NHL only; add a league to Leagues when people ask for it.
/// </summary>
public sealed class EspnSportsScoreProvider(HttpClient httpClient, ILogger<EspnSportsScoreProvider> logger)
    : ISportsScoreProvider
{
    private const string Api = "https://site.api.espn.com/apis/site/v2/sports/";

    private static readonly (string Path, string Sport, string Period)[] Leagues =
    [
        ("baseball/mlb", "baseball", "inning"),
        ("football/nfl", "football", "quarter"),
        ("basketball/nba", "basketball", "quarter"),
        ("hockey/nhl", "hockey", "period")
    ];

    private sealed record Team(string Id, string Name, string Location, int League, string Color = "555555", string? Logo = null);

    private (DateTimeOffset At, Team[] Teams)? _teams;

    public async Task<SportsAnswer?> AnswerAsync(string transcript, DateTimeOffset localNow, CancellationToken cancellationToken = default)
    {
        var text = " " + Regex.Replace(transcript.ToLowerInvariant().Replace("'s", string.Empty), "[^a-z0-9 ]", " ") + " ";
        var teams = await GetTeamsAsync(cancellationToken);
        var matches = teams
            // People say "the (Detroit) Lions"; without "the" it's "his jazz combo" or "play one stars collapse".
            .Where(t => Regex.IsMatch(text, " the (?:" + Regex.Escape(t.Location) + " )?" + Regex.Escape(t.Name) + "s? "))
            .ToList();
        if (matches.Count == 0) return null;
        // Same name in two leagues (Giants, Rangers, Kings...): the sport or city said wins, then in-season.
        if (matches.Count > 1)
        {
            var said = matches.Where(t => text.Contains(" " + Leagues[t.League].Sport + " ", StringComparison.Ordinal) ||
                                          text.Contains(" " + t.Location + " ", StringComparison.Ordinal)).ToList();
            if (said.Count > 0) matches = said;
        }

        try
        {
            // "did they win / how did they do / when did they play" ask for a result; "did they play tonight" asks about today.
            var past = !Regex.IsMatch(text, " (gonna|going to|will) ") &&
                       (Regex.IsMatch(text, " (won|win|lost|lose|beat|score|how did) ") ||
                        (Regex.IsMatch(text, " did .* (play|game) ") && !Regex.IsMatch(text, " (tonight|today) ")));
            TeamGame? today = null;
            foreach (var candidate in matches)
                if (await GetTodaysGameAsync(candidate, localNow, cancellationToken) is { } game)
                {
                    today = new TeamGame(candidate, game);
                    break;
                }

            if (today is not null && (today.Game.State != "pre" || !past))
            {
                var described = Describe(today.Team, today.Game, localNow);
                return today.Game.State == "pre" ? new SportsAnswer("Yes, t" + described[1..]) : Answer(described, today, teams);
            }

            var (last, next) = await GetScheduleAsync(today is null ? matches : [today.Team], localNow, cancellationToken);
            if (past && last is not null)
                return Answer(Describe(last.Team, last.Game, localNow) +
                       (today is null ? string.Empty
                           : $" They play the {Title(today.Game.Opponent)} {Day(today.Game.Start, localNow)} at {Clock(today.Game.Start.ToOffset(localNow.Offset))}."),
                    last, teams);
            if (today is not null) return new SportsAnswer(Describe(today.Team, today.Game, localNow));
            var team = last?.Team ?? next?.Team ?? matches[0];
            var answer = $"The {Title(team.Name)} don't play today.";
            if (next is not null)
                answer += $" Their next game is {next.Game.Start.ToOffset(localNow.Offset):dddd, MMMM d} " +
                          $"against the {Title(next.Game.Opponent)}.";
            else if (last is not null)
                return Answer(answer + " " + Describe(last.Team, last.Game, localNow), last, teams);
            return new SportsAnswer(answer);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "ESPN lookup failed for {Team}.", matches[0].Name);
            return new SportsAnswer($"I couldn't get the {Title(matches[0].Name)} score right now. Try again in a bit.");
        }
    }

    // The card shows a game with a score: away on the left, home on the right.
    private static SportsAnswer Answer(string spoken, TeamGame played, Team[] teams)
    {
        if (played.Game is not { Us: { } us, Them: { } them } game) return new SportsAnswer(spoken);
        var opponent = teams.FirstOrDefault(t => t.League == played.Team.League && t.Id == game.OpponentId);
        var mine = new SportsCardSide(Title(played.Team.Name), us, played.Team.Color, played.Team.Logo);
        var theirs = new SportsCardSide(Title(game.Opponent), them, opponent?.Color ?? "555555", opponent?.Logo);
        var (away, home) = game.Home ? (theirs, mine) : (mine, theirs);
        return new SportsAnswer(spoken, new SportsScoreCard(away, home, Background(away.Color, home.Color)));
    }

    // 1280x720 (Jibo's screen): each half fades strongly from a tint of the team color (50% toward white) at the top to
    // the plain color across the middle band (the score), then down to a deep shade (20%) at the bottom; across, it
    // darkens from full at the center line to 35% at its outer edge.
    private static string Background(string awayHex, string homeHex)
    {
        var away = Rgba32.ParseHex(awayHex);
        var home = Rgba32.ParseHex(homeHex);
        static byte Channel(byte c, float t, float h) => (byte)MathF.Round(h *
            (t < 0.4f ? c + (255 - c) * 0.5f * (1 - t / 0.4f) : t <= 0.6f ? c : c * (1 - 0.8f * (t - 0.6f) / 0.4f)));
        static Rgb24 Shade(Rgba32 c, float t, float h) => new(Channel(c.R, t, h), Channel(c.G, t, h), Channel(c.B, t, h));
        using var image = new Image<Rgb24>(1280, 720);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var t = (float)y / (rows.Height - 1);
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < 640; x++)
                {
                    var h = 1 - 0.65f * (639 - x) / 639f;   // x=639 / x=640 are the center line
                    row[x] = Shade(away, t, h);
                    row[1279 - x] = Shade(home, t, h);
                }
            }
        });
        return image.ToBase64String(SixLabors.ImageSharp.Formats.Png.PngFormat.Instance);
    }

    // White scores need a dark enough side: a near-white team color (e.g. some road colors) uses the alternate one.
    private static string CardColor(JsonElement team)
    {
        var color = team.TryGetProperty("color", out var c) ? c.GetString() : null;
        var alternate = team.TryGetProperty("alternateColor", out var a) ? a.GetString() : null;
        if (color is null || !Rgba32.TryParseHex(color, out var rgb)) return "555555";
        var light = 0.299 * rgb.R + 0.587 * rgb.G + 0.114 * rgb.B > 200;
        return light && alternate is not null && Rgba32.TryParseHex(alternate, out _) ? alternate : color;
    }

    // ESPN's resized logo over plain http (the robot's browser fails on https).
    private static string? CardLogo(JsonElement team) =>
        team.TryGetProperty("logos", out var logos) && logos.GetArrayLength() > 0 &&
        Uri.TryCreate(logos[0].GetProperty("href").GetString(), UriKind.Absolute, out var logo)
            ? logo.Host == "a.espncdn.com" ? $"http://a.espncdn.com/combiner/i?img={logo.AbsolutePath}&h=200&w=200"
            : "http://" + logo.Host + logo.PathAndQuery
            : null;

    private sealed record Game(DateTimeOffset Start, string State, string Opponent, int? Us, int? Them, bool? Won, int Period,
        string OpponentId = "", bool Home = false);

    private sealed record TeamGame(Team Team, Game Game);

    private static string Describe(Team team, Game game, DateTimeOffset localNow)
    {
        var name = "the " + Title(team.Name);
        var opponent = "the " + Title(game.Opponent);
        var start = game.Start.ToOffset(localNow.Offset);
        var day = Day(game.Start, localNow);
        switch (game.State)
        {
            case "pre":
                return $"{Capitalize(name)} play {opponent} {day} at {Clock(start)}.";
            case "in":
                var period = $"in the {Ordinal(game.Period)} {Leagues[team.League].Period}";
                if (game.Us == game.Them) return $"{Capitalize(name)} are tied with {opponent}, {game.Us} to {game.Them}, {period}.";
                return game.Us > game.Them
                    ? $"{Capitalize(name)} are beating {opponent} {game.Us} to {game.Them} {period}."
                    : $"{Capitalize(name)} are losing to {opponent} {game.Them} to {game.Us} {period}.";
            default:
                if (game.Us is null || game.Them is null) return $"{Capitalize(name)} played {opponent} {day}.";
                if (game.Us == game.Them) return $"{Capitalize(name)} tied {opponent} {game.Us} to {game.Them} {day}.";
                return game.Won == true || game.Us > game.Them
                    ? $"{Capitalize(name)} beat {opponent} {game.Us} to {game.Them} {day}."
                    : $"{Capitalize(name)} lost to {opponent} {game.Them} to {game.Us} {day}.";
        }
    }

    private static string Day(DateTimeOffset when, DateTimeOffset localNow)
    {
        var start = when.ToOffset(localNow.Offset);
        return start.Date == localNow.Date ? (start.Hour >= 17 ? "tonight" : "today")
            : start.Date == localNow.Date.AddDays(-1) ? (start.Hour >= 17 ? "last night" : "yesterday")
            : "on " + start.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
    }

    private async Task<Team[]> GetTeamsAsync(CancellationToken cancellationToken)
    {
        if (_teams is { } cached && DateTimeOffset.UtcNow - cached.At < TimeSpan.FromDays(1)) return cached.Teams;
        var teams = new List<Team>();
        for (var i = 0; i < Leagues.Length; i++)
        {
            using var doc = await GetAsync(Leagues[i].Path + "/teams", cancellationToken);
            foreach (var entry in doc.RootElement.GetProperty("sports")[0].GetProperty("leagues")[0].GetProperty("teams").EnumerateArray())
            {
                var t = entry.GetProperty("team");
                teams.Add(new Team(t.GetProperty("id").GetString()!, t.GetProperty("name").GetString()!.ToLowerInvariant(),
                    t.GetProperty("location").GetString()!.ToLowerInvariant(), i, CardColor(t), CardLogo(t)));
            }
        }

        _teams = (DateTimeOffset.UtcNow, teams.ToArray());
        return _teams.Value.Teams;
    }

    private async Task<Game?> GetTodaysGameAsync(Team team, DateTimeOffset localNow, CancellationToken cancellationToken)
    {
        using var doc = await GetAsync($"{Leagues[team.League].Path}/scoreboard?dates={localNow:yyyyMMdd}", cancellationToken);
        return doc.RootElement.GetProperty("events").EnumerateArray()
            .Select(e => ReadGame(e, team.Id))
            .FirstOrDefault(g => g is not null && g.Start.ToOffset(localNow.Offset).Date == localNow.Date);
    }

    private async Task<(TeamGame? Last, TeamGame? Next)> GetScheduleAsync(IEnumerable<Team> teams, DateTimeOffset localNow,
        CancellationToken cancellationToken)
    {
        TeamGame? last = null, next = null;
        foreach (var team in teams)
        {
            using var schedule = await GetAsync($"{Leagues[team.League].Path}/teams/{team.Id}/schedule", cancellationToken);
            var events = schedule.RootElement.TryGetProperty("events", out var list) ? list.EnumerateArray().ToList() : [];
            foreach (var game in events.Select(e => ReadGame(e, team.Id)))
            {
                if (game is null) continue;
                if (game.State == "post" && (last is null || game.Start > last.Game.Start)) last = new TeamGame(team, game);
                if (game.State == "pre" && game.Start > localNow && (next is null || game.Start < next.Game.Start)) next = new TeamGame(team, game);
            }

            // Off-season the schedule is last season's; the team page still has the next game.
            if (next is null)
            {
                using var page = await GetAsync($"{Leagues[team.League].Path}/teams/{team.Id}", cancellationToken);
                if (page.RootElement.GetProperty("team").TryGetProperty("nextEvent", out var nextEvents) &&
                    nextEvents.GetArrayLength() > 0 && ReadGame(nextEvents[0], team.Id) is { State: "pre" } upcoming &&
                    upcoming.Start > localNow)
                    next = new TeamGame(team, upcoming);
            }
        }

        return (last, next);
    }

    private static Game? ReadGame(JsonElement e, string teamId)
    {
        var competition = e.GetProperty("competitions")[0];
        var sides = competition.GetProperty("competitors").EnumerateArray().ToList();
        var us = sides.FirstOrDefault(s => s.GetProperty("id").GetString() == teamId);
        var them = sides.FirstOrDefault(s => s.GetProperty("id").GetString() != teamId);
        if (us.ValueKind != JsonValueKind.Object || them.ValueKind != JsonValueKind.Object) return null;
        var status = competition.TryGetProperty("status", out var s1) ? s1 : e.GetProperty("status");
        return new Game(
            DateTimeOffset.Parse(e.GetProperty("date").GetString()!, CultureInfo.InvariantCulture),
            status.GetProperty("type").GetProperty("state").GetString() ?? "pre",
            them.GetProperty("team").GetProperty("shortDisplayName").GetString()!,
            Score(us), Score(them),
            us.TryGetProperty("winner", out var w) && w.ValueKind is JsonValueKind.True or JsonValueKind.False ? w.GetBoolean() : null,
            status.TryGetProperty("period", out var p) && p.TryGetInt32(out var period) ? period : 0,
            them.GetProperty("id").GetString()!,
            us.TryGetProperty("homeAway", out var side) && side.GetString() == "home");
    }

    // The scoreboard has "score": "4"; a team schedule has "score": { "value": 4.0, "displayValue": "4" }.
    private static int? Score(JsonElement side)
    {
        if (!side.TryGetProperty("score", out var score)) return null;
        var raw = score.ValueKind == JsonValueKind.Object && score.TryGetProperty("displayValue", out var d) ? d.GetString()
            : score.ValueKind == JsonValueKind.String ? score.GetString() : null;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        using var response = await httpClient.GetAsync(Api + path, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
    }

    private static string Clock(DateTimeOffset time) =>
        time.Minute == 0 ? time.ToString("h tt", CultureInfo.InvariantCulture) : time.ToString("h:mm tt", CultureInfo.InvariantCulture);

    private static string Ordinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd", _ when n > 0 => n + "th", _ => "1st"
    };

    private static string Title(string name) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);

    private static string Capitalize(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}
