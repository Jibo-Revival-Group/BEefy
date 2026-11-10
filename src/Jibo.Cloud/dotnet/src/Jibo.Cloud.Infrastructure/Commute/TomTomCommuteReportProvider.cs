using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Infrastructure.Commute;

/// <summary>
/// Live commute: TomTom travel time from the speaker's home (or the household's) to the speaker's job,
/// all saved in BEacon. Robots without both addresses keep the profile-based fallback.
/// </summary>
public sealed class TomTomCommuteReportProvider(
    IUserIntegrationStore integrationStore,
    ICommuteRouting routing,
    ICloudStateStore cloudStateStore,
    ICommuteReportProvider fallbackProvider) : ICommuteReportProvider
{
    // Routes are cached briefly so a report and a follow-up question share one TomTom call.
    private static readonly TimeSpan RouteCacheTtl = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, (CommuteRoute Route, DateTimeOffset Expires)> _routes = new();

    public async Task<CommuteReportSnapshot?> GetReportAsync(TurnContext turn, CancellationToken cancellationToken = default)
    {
        var robot = (string.IsNullOrWhiteSpace(turn.DeviceId) ? null : turn.DeviceId.Trim()) ??
                    ReadAttribute(turn, "deviceId") ?? ReadAttribute(turn, "robotId");
        var addresses = robot is null || !routing.IsConfigured
            ? []
            : integrationStore.GetCommuteAddresses(MemberCalendarFeedRecord.RobotScope(robot));
        var work = PickWork(addresses.Where(static item => !CommuteAddressRecord.IsHome(item.MemberId)).ToArray(),
            ReadAttribute(turn, "personalReportUserName") ?? ReadAttribute(turn, "userName"));
        // The person's own home when BEacon has one, else the household's.
        var home = (work is null ? null : addresses.FirstOrDefault(item => item.MemberId == CommuteAddressRecord.HomeOf(work.MemberId))) ??
                   addresses.FirstOrDefault(static item => item.MemberId == CommuteAddressRecord.HomeMemberId);
        if (home is null || work is null) return await fallbackProvider.GetReportAsync(turn, cancellationToken);

        var profile = cloudStateStore.GetCommuteProfiles().FirstOrDefault(static item => item.IsEnabled);
        var mode = profile?.Mode ?? "driving";
        var route = await RouteAsync(home, work, mode, cancellationToken);
        if (route is null) return null;

        var zone = Zone(home.TimeZoneId);
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        var workAt = new DateTimeOffset(now.Year, now.Month, now.Day, profile?.WorkHour ?? 8, profile?.WorkMinute ?? 30, 0, now.Offset);
        var summary = mode.Trim().ToLowerInvariant() switch
        {
            "walking" => "your walk to work",
            "transit" => "your trip to work by public transportation",
            "bicycling" => "your bike ride to work",
            _ => "your drive to work"
        };
        return new CommuteReportSnapshot(
            "work",
            summary,
            Math.Max(1, (int)Math.Ceiling(route.TravelTimeSeconds / 60.0)),
            mode,
            false,
            (int)Math.Round((workAt - now).TotalMinutes),
            (int)Math.Round(route.TrafficDelaySeconds / 60.0));
    }

    /// <summary>The speaker's job by the name BEacon saved with it; one saved job is used for anyone.</summary>
    private static CommuteAddressRecord? PickWork(CommuteAddressRecord[] jobs, string? name)
    {
        if (jobs.Length == 0) return null;
        if (string.IsNullOrWhiteSpace(name)) return jobs.Length == 1 ? jobs[0] : null;
        var spoken = Normalize(name);
        return jobs.FirstOrDefault(job => job.MemberName is { } saved &&
                                          (Normalize(saved) == spoken || Normalize(saved).Split(' ')[0] == spoken.Split(' ')[0])) ??
               (jobs.Length == 1 ? jobs[0] : null);
    }

    private async Task<CommuteRoute?> RouteAsync(CommuteAddressRecord home, CommuteAddressRecord work, string mode,
        CancellationToken cancellationToken)
    {
        var key = $"{home.Latitude},{home.Longitude}:{work.Latitude},{work.Longitude}:{mode}";
        if (_routes.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Route;
        var route = await routing.RouteAsync(home.Latitude, home.Longitude, work.Latitude, work.Longitude, mode, cancellationToken);
        if (route is not null) _routes[key] = (route, DateTimeOffset.UtcNow.Add(RouteCacheTtl));
        return route;
    }

    private static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return TimeZoneInfo.Local;
    }

    private static string Normalize(string name) =>
        Regex.Replace(name.Trim().ToLowerInvariant(), @"\s+", " ");

    private static string? ReadAttribute(TurnContext turn, string key) =>
        turn.Attributes.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } text ? text.Trim() : null;
}
