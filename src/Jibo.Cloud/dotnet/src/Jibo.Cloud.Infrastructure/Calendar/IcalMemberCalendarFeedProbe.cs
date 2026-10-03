using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Infrastructure.Calendar;

public sealed class IcalMemberCalendarFeedProbe(IcalCalendarFeedInspector inspector) : IMemberCalendarFeedProbe
{
    public bool TryValidateHttpsPublicUrl(string? rawUrl, out string? error) =>
        IcalUrlValidator.TryValidateHttpsPublicUrl(rawUrl, out _, out error);

    public string? TryGetSafeHost(string? rawUrl) => IcalUrlValidator.TryGetSafeHost(rawUrl);

    public async Task<MemberCalendarFeedProbeResult> ProbeAsync(
        string icalUrl,
        CancellationToken cancellationToken = default)
    {
        var probe = await inspector.ProbeAsync(icalUrl, cancellationToken);
        return new MemberCalendarFeedProbeResult(
            probe.Ok,
            probe.Error,
            probe.TodayEventCount,
            probe.TomorrowEventCount,
            probe.SampleSummaries);
    }
}
