using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Calendar;

public sealed class IcalCalendarReportProvider(
    IUserIntegrationStore integrationStore,
    ICloudStateStore cloudStateStore,
    IIcalFeedFetcher feedFetcher,
    ICalendarReportProvider fallbackProvider,
    ILogger<IcalCalendarReportProvider> logger) : ICalendarReportProvider
{
    private static readonly TimeSpan SuccessCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public async Task<CalendarReportSnapshot?> GetReportAsync(
        TurnContext turn,
        CancellationToken cancellationToken = default)
    {
        var feed = FindRobotFeed(turn);
        var loopId = feed?.LoopId ?? ResolveLoopId(turn);
        if (feed is null)
        {
            var memberId = ResolveMemberId(turn, loopId);
            if (string.IsNullOrWhiteSpace(memberId))
                return await fallbackProvider.GetReportAsync(turn, cancellationToken);
            feed = integrationStore.FindMemberCalendarFeed(loopId, memberId);
        }

        if (feed is null || !feed.IsEnabled || string.IsNullOrWhiteSpace(feed.IcalUrl))
            return await fallbackProvider.GetReportAsync(turn, cancellationToken);
        var memberIdForStatus = feed.MemberId;

        var cacheKey = BuildCacheKey(feed.FeedId, feed.UpdatedUtc);
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
            return cached.Snapshot;

        try
        {
            var fetch = await feedFetcher.FetchAsync(feed.IcalUrl, cancellationToken);
            if (!fetch.Ok || string.IsNullOrWhiteSpace(fetch.Body))
            {
                logger.LogWarning(
                    "Configured iCal feed failed for loop {LoopId}. Error={Error}",
                    loopId,
                    fetch.Error ?? "unknown");
                integrationStore.UpdateMemberCalendarFeedSyncStatus(
                    loopId,
                    memberIdForStatus,
                    null,
                    fetch.Error ?? "fetch_failed");
                var failureSnapshot = new CalendarReportSnapshot([], [], [], HasServiceError: true);
                _cache[cacheKey] = new CacheEntry(failureSnapshot, DateTimeOffset.UtcNow.Add(FailureCacheTtl));
                return failureSnapshot;
            }

            var localZone = ZoneFor(feed);
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, localZone).DateTime);
            var tomorrow = today.AddDays(1);
            var events = IcalCalendarParser.ParseEventsForWindow(fetch.Body, today, tomorrow, localZone);

            var todayEvents = events.Where(item => item.Date == today).ToArray();
            var tomorrowEvents = events.Where(item => item.Date == tomorrow).ToArray();
            var snapshot = new CalendarReportSnapshot(
                todayEvents.Select(static item => item.Summary).ToArray(),
                todayEvents.Select(static item => item.TimeLabel).ToArray(),
                tomorrowEvents.Select(static item => item.Summary).ToArray());

            integrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, memberIdForStatus, DateTimeOffset.UtcNow, null);
            _cache[cacheKey] = new CacheEntry(snapshot, DateTimeOffset.UtcNow.Add(SuccessCacheTtl));
            return snapshot;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "iCal calendar parse failed for loop {LoopId}", loopId);
            integrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, memberIdForStatus, null, "parse_failed");
            var failureSnapshot = new CalendarReportSnapshot([], [], [], HasServiceError: true);
            _cache[cacheKey] = new CacheEntry(failureSnapshot, DateTimeOffset.UtcNow.Add(FailureCacheTtl));
            return failureSnapshot;
        }
    }

    /// <summary>
    /// Managed hosting stores feeds per robot. Pick the feed of the person the report is for (by the
    /// name BEacon saved with it). Without a name, a robot with just one calendar uses it; a name that
    /// matches no saved calendar never reads someone else's.
    /// </summary>
    private MemberCalendarFeedRecord? FindRobotFeed(TurnContext turn)
    {
        var robot = (string.IsNullOrWhiteSpace(turn.DeviceId) ? null : turn.DeviceId.Trim()) ??
                    ReadAttribute(turn, "deviceId") ?? ReadAttribute(turn, "robotId");
        if (robot is null) return null;
        var feeds = integrationStore.GetMemberCalendarFeeds(MemberCalendarFeedRecord.RobotScope(robot))
            .Where(static feed => feed.IsEnabled && !string.IsNullOrWhiteSpace(feed.IcalUrl))
            .ToArray();
        if (feeds.Length == 0) return null;

        var name = ReadAttribute(turn, "personalReportUserName") ?? ReadAttribute(turn, "userName");
        if (name is null) return feeds.Length == 1 ? feeds[0] : null;

        var spoken = NormalizeName(name);
        return feeds.FirstOrDefault(feed => feed.MemberName is { } saved &&
                   (NormalizeName(saved) == spoken || NormalizeName(saved).Split(' ')[0] == spoken.Split(' ')[0])) ??
               (feeds.Length == 1 && feeds[0].MemberName is null ? feeds[0] : null);
    }

    private static TimeZoneInfo ZoneFor(MemberCalendarFeedRecord feed)
    {
        if (!string.IsNullOrWhiteSpace(feed.TimeZoneId))
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(feed.TimeZoneId); }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }

        return TimeZoneInfo.Local;
    }

    private string ResolveLoopId(TurnContext turn)
    {
        if (turn.Attributes.TryGetValue("loopId", out var loopValue) &&
            loopValue is not null &&
            !string.IsNullOrWhiteSpace(loopValue.ToString()))
            return loopValue.ToString()!.Trim();

        return cloudStateStore.GetLoops().FirstOrDefault()?.LoopId ?? "openjibo-default-loop";
    }

    private string? ResolveMemberId(TurnContext turn, string loopId)
    {
        if (turn.Attributes.TryGetValue("personId", out var personValue) &&
            personValue is not null &&
            !string.IsNullOrWhiteSpace(personValue.ToString()))
            return personValue.ToString()!.Trim();

        if (turn.Attributes.TryGetValue("speakerId", out var speakerValue) &&
            speakerValue is not null &&
            !string.IsNullOrWhiteSpace(speakerValue.ToString()))
            return speakerValue.ToString()!.Trim();

        if (turn.Attributes.TryGetValue("calendarMemberId", out var calendarMemberValue) &&
            calendarMemberValue is not null &&
            !string.IsNullOrWhiteSpace(calendarMemberValue.ToString()))
            return calendarMemberValue.ToString()!.Trim();

        var userName = ReadAttribute(turn, "personalReportUserName") ??
                       ReadAttribute(turn, "userName");
        if (string.IsNullOrWhiteSpace(userName)) return null;

        return ResolveMemberIdByName(loopId, userName);
    }

    private string? ResolveMemberIdByName(string loopId, string userName)
    {
        var normalized = NormalizeName(userName);
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        foreach (var person in cloudStateStore.GetPeople(loopId))
        {
            var candidates = new[]
            {
                person.Alias,
                person.DisplayName,
                person.DisplayName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            };

            if (candidates.Any(candidate =>
                    !string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(NormalizeName(candidate!), normalized, StringComparison.Ordinal)))
                return person.PersonId;
        }

        var members = cloudStateStore.GetLoopMembers(loopId)
            .Where(static member =>
                !string.Equals(member.Type, "robot", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(member.Status, "removed", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var member in members)
        {
            var candidates = new[]
            {
                member.Nickname,
                member.FirstName,
                string.Join(' ', new[] { member.FirstName, member.LastName }.Where(static part =>
                    !string.IsNullOrWhiteSpace(part)))
            };

            if (candidates.Any(candidate =>
                    !string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(NormalizeName(candidate!), normalized, StringComparison.Ordinal)))
                return member.Id;
        }

        return null;
    }

    private static string? ReadAttribute(TurnContext turn, string key)
    {
        if (!turn.Attributes.TryGetValue(key, out var value) || value is null)
            return null;
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string NormalizeName(string value)
    {
        return string.Join(
            ' ',
            value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string BuildCacheKey(string feedId, DateTimeOffset updatedUtc)
    {
        // Never include the private feed URL. Hash feed id + update stamp only.
        var material = $"{feedId}|{updatedUtc.ToUnixTimeSeconds()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash);
    }

    private sealed record CacheEntry(CalendarReportSnapshot Snapshot, DateTimeOffset ExpiresUtc);
}
