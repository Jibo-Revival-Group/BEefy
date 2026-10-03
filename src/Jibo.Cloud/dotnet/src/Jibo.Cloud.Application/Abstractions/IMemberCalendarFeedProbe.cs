namespace Jibo.Cloud.Application.Abstractions;

public interface IMemberCalendarFeedProbe
{
    bool TryValidateHttpsPublicUrl(string? rawUrl, out string? error);

    string? TryGetSafeHost(string? rawUrl);

    Task<MemberCalendarFeedProbeResult> ProbeAsync(
        string icalUrl,
        CancellationToken cancellationToken = default);
}

public sealed record MemberCalendarFeedProbeResult(
    bool Ok,
    string? Error,
    int TodayEventCount,
    int TomorrowEventCount,
    IReadOnlyList<string> SampleSummaries);
