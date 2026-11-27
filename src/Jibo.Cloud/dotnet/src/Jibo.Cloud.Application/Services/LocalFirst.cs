using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Local-first robots keep their people and report settings on the robot: CONTEXT carries them as
/// <c>runtime.localFirst</c> (BEam sends it only on 192.168.7.163) and BEefy uses them for the turn without saving
/// them. Unlike <see cref="BetaFeatures"/> there is no version clause: only the tag counts.
/// </summary>
public static class LocalFirst
{
    // shortcut: caps what one CONTEXT can put in memory (a loop has at most 16 people); raise if BEacon ever allows more.
    private const int MaxEntries = 64;

    public static bool IsEnabled(TurnContext turn) => ReadSettingsElement(ContextText(turn)) is not null;

    /// <summary>True when a CONTEXT message (or its data payload) carries <c>runtime.localFirst</c>.</summary>
    public static bool IsInContext(string? context) => ReadSettingsElement(context) is not null;

    /// <summary>This turn's report settings as the integration-store records for <paramref name="scope"/>, or null.</summary>
    public static LocalFirstSettings? ReadSettings(TurnContext turn, string scope = "")
    {
        if (ReadSettingsElement(ContextText(turn)) is not { } settings) return null;

        return new LocalFirstSettings(
            Entries(settings, "calendarFeeds").Select(item => new MemberCalendarFeedRecord
            {
                // Stable per feed so the report's fetch cache survives the per-turn refresh.
                FeedId = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"{scope}|{Text(item, "memberId")}|{Text(item, "icalUrl")}")))[..32],
                LoopId = scope,
                MemberId = Text(item, "memberId") ?? string.Empty,
                IcalUrl = Text(item, "icalUrl") ?? string.Empty,
                IsEnabled = !item.TryGetProperty("isEnabled", out var enabled) || enabled.ValueKind != JsonValueKind.False,
                CreatedUtc = DateTimeOffset.UnixEpoch,
                UpdatedUtc = DateTimeOffset.UnixEpoch,
                MemberName = Text(item, "memberName"),
                TimeZoneId = Text(item, "timeZone")
            }).Where(static feed => feed.MemberId.Length > 0).ToArray(),
            Entries(settings, "commuteAddresses").Select(item => new CommuteAddressRecord
            {
                Scope = scope,
                MemberId = Text(item, "memberId") ?? string.Empty,
                Address = Text(item, "address") ?? string.Empty,
                Latitude = Number(item, "lat"),
                Longitude = Number(item, "lng"),
                City = Text(item, "city"),
                MemberName = Text(item, "memberName"),
                TimeZoneId = Text(item, "timeZone"),
                Mode = Text(item, "mode")?.ToLowerInvariant() is { } mode && CommuteAddressRecord.Modes.Contains(mode) ? mode : null
            }).Where(static address => address.MemberId.Length > 0).ToArray(),
            Entries(settings, "newsTopics").Select(item => new NewsTopicsRecord
            {
                Scope = scope,
                MemberId = Text(item, "memberId") ?? string.Empty,
                MemberName = Text(item, "memberName"),
                DisabledTopics = item.TryGetProperty("disabledTopics", out var topics) && topics.ValueKind == JsonValueKind.Array
                    ? topics.EnumerateArray().Where(static topic => topic.ValueKind == JsonValueKind.String)
                        .Select(static topic => topic.GetString()!.Trim().ToLowerInvariant())
                        .Where(static topic => NewsTopics.All.Contains(topic)).Distinct().ToArray()
                    : []
            }).Where(static topics => topics.MemberId.Length > 0).ToArray());
    }

    private static string? ContextText(TurnContext turn) =>
        turn.Attributes.TryGetValue("context", out var context)
            ? context is JsonElement element ? element.GetRawText() : context?.ToString()
            : null;

    private static JsonElement? ReadSettingsElement(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return null;
        try
        {
            using var document = JsonDocument.Parse(context);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var data = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            return data.TryGetProperty("runtime", out var runtime) && runtime.ValueKind == JsonValueKind.Object &&
                   runtime.TryGetProperty("localFirst", out var localFirst) && localFirst.ValueKind == JsonValueKind.Object
                ? localFirst.Clone()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<JsonElement> Entries(JsonElement settings, string name) =>
        settings.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(static item => item.ValueKind == JsonValueKind.Object).Take(MaxEntries)
            : [];

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;

    private static double Number(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
}

/// <summary>A local-first robot's report settings for one turn: never persisted.</summary>
public sealed record LocalFirstSettings(
    IReadOnlyList<MemberCalendarFeedRecord> CalendarFeeds,
    IReadOnlyList<CommuteAddressRecord> CommuteAddresses,
    IReadOnlyList<NewsTopicsRecord> NewsTopics);
