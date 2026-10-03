using System.Text.Json;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboCloudProtocolService
{
    private static bool IsMemberCalendarOperation(string operation) =>
        operation is "GetCalendarFeeds" or "SetCalendarFeed" or "ClearCalendarFeed" or "TestCalendarFeed";

    private async Task<ProtocolDispatchResult> HandleMemberCalendarAsync(string operation, ProtocolEnvelope envelope)
    {
        if (AuthorizeMemberCalendar(envelope) is { } rejected)
            return rejected;

        if (userIntegrationStore is null)
            return CalendarError(503, "Calendar store is unavailable.");

        var loopId = ResolvePersonalReportLoopId();
        var body = envelope.TryParseBody();

        switch (operation)
        {
            case "GetCalendarFeeds":
                return ProtocolDispatchResult.Ok(new
                {
                    loopId,
                    members = userIntegrationStore.GetMemberCalendarFeeds(loopId)
                        .Select(feed => CalendarFeedStatus(feed.MemberId, feed))
                        .ToArray()
                });
            case "SetCalendarFeed":
            {
                var memberId = ReadString(body, "memberId") ?? ReadString(body, "id");
                if (string.IsNullOrWhiteSpace(memberId))
                    return CalendarError(400, "memberId is required.");

                if (calendarFeedProbe is null)
                    return CalendarError(503, "Calendar probe is unavailable.");

                var icalUrl = ReadString(body, "icalUrl");
                if (!calendarFeedProbe.TryValidateHttpsPublicUrl(icalUrl, out var validationError))
                    return CalendarError(400, validationError ?? "iCal URL is required.");

                var isEnabled = body?.TryGetProperty("isEnabled", out _) == true
                    ? ReadBool(body, "isEnabled")
                    : true;
                var feed = userIntegrationStore.UpsertMemberCalendarFeed(
                    loopId,
                    memberId,
                    icalUrl!.Trim(),
                    isEnabled);
                return ProtocolDispatchResult.Ok(CalendarFeedStatus(memberId, feed));
            }
            case "ClearCalendarFeed":
            {
                var memberId = ReadString(body, "memberId") ?? ReadString(body, "id");
                if (string.IsNullOrWhiteSpace(memberId))
                    return CalendarError(400, "memberId is required.");

                var removed = userIntegrationStore.ClearMemberCalendarFeed(loopId, memberId);
                if (removed is null)
                    return CalendarError(404, "No calendar feed is configured for that member.");

                return ProtocolDispatchResult.Ok(new { cleared = true, memberId });
            }
            case "TestCalendarFeed":
            {
                if (calendarFeedProbe is null)
                    return CalendarError(503, "Calendar probe is unavailable.");

                var memberId = ReadString(body, "memberId") ?? ReadString(body, "id");
                if (string.IsNullOrWhiteSpace(memberId))
                    return CalendarError(400, "memberId is required.");

                var icalUrl = ReadString(body, "icalUrl");
                if (string.IsNullOrWhiteSpace(icalUrl))
                    icalUrl = userIntegrationStore.FindMemberCalendarFeed(loopId, memberId)?.IcalUrl;

                if (string.IsNullOrWhiteSpace(icalUrl))
                    return CalendarError(400, "iCal URL is required.");

                if (!calendarFeedProbe.TryValidateHttpsPublicUrl(icalUrl, out var validationError))
                    return CalendarError(400, validationError ?? "iCal URL is required.");

                var probe = await calendarFeedProbe.ProbeAsync(icalUrl);
                if (!probe.Ok)
                {
                    userIntegrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, memberId, null, probe.Error);
                    return ProtocolDispatchResult.Ok(new
                    {
                        ok = false,
                        error = probe.Error,
                        host = calendarFeedProbe.TryGetSafeHost(icalUrl)
                    });
                }

                userIntegrationStore.UpdateMemberCalendarFeedSyncStatus(loopId, memberId, DateTimeOffset.UtcNow, null);
                return ProtocolDispatchResult.Ok(new
                {
                    ok = true,
                    host = calendarFeedProbe.TryGetSafeHost(icalUrl),
                    todayEventCount = probe.TodayEventCount,
                    tomorrowEventCount = probe.TomorrowEventCount,
                    sampleSummaries = probe.SampleSummaries
                });
            }
            default:
                return CalendarError(400, "Unknown calendar operation.");
        }
    }

    /// <summary>
    /// Personal report reads <c>session.Metadata["loopId"]</c>, which is the account's default loop.
    /// Feeds are stored on that same id so a saved calendar is the one the report speaks.
    /// </summary>
    private string ResolvePersonalReportLoopId()
    {
        var accountId = stateStore.GetAccount().AccountId;
        var loops = stateStore.GetLoops();
        var ownerLoop = loops.FirstOrDefault(loop =>
            string.Equals(loop.OwnerAccountId, accountId, StringComparison.OrdinalIgnoreCase));
        return ownerLoop?.LoopId ?? loops.FirstOrDefault()?.LoopId ?? "openjibo-default-loop";
    }

    private ProtocolDispatchResult? AuthorizeMemberCalendar(ProtocolEnvelope envelope)
    {
        var identity = _identityResolver.Resolve(envelope);
        if (string.Equals(identity.Source, "conflict", StringComparison.OrdinalIgnoreCase))
            return CalendarError(401, "Robot identity conflict.");

        var accessKeyId = AwsRequestAccessKey.Read(envelope);
        var secret = LookupCalendarCredentialSecret(accessKeyId);
        if (secret is not null)
        {
            if (!Aws3Signature.Verify(envelope, secret))
                return CalendarError(401, "Cloud credential signature was not accepted.");
            return null;
        }

        if (!Aws3Signature.HasPresentedCredential(envelope) || string.IsNullOrWhiteSpace(identity.HeaderIdentity))
            return CalendarError(401, "Cloud credentials are required.");

        if (!identity.IsResolved)
            return CalendarError(401, "Robot identity could not be resolved.");

        return null;
    }

    private string? LookupCalendarCredentialSecret(string? accessKeyId)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId)) return null;

        var account = stateStore.GetAccount();
        if (accessKeyId.Equals(account.AccessKeyId, StringComparison.Ordinal))
            return account.SecretAccessKey;

        return stateStore.FindUserByAccessKeyId(accessKeyId)?.SecretAccessKey;
    }

    private object CalendarFeedStatus(string memberId, MemberCalendarFeedRecord? feed)
    {
        var configured = feed is not null && !string.IsNullOrWhiteSpace(feed.IcalUrl);
        return new
        {
            memberId,
            configured,
            isEnabled = feed?.IsEnabled ?? false,
            host = configured ? calendarFeedProbe?.TryGetSafeHost(feed!.IcalUrl) : null,
            lastSuccessUtc = feed?.LastSuccessUtc,
            lastError = feed?.LastError,
            updatedUtc = feed?.UpdatedUtc
        };
    }

    private static ProtocolDispatchResult CalendarError(int statusCode, string error) =>
        new()
        {
            StatusCode = statusCode,
            BodyText = JsonSerializer.Serialize(new { error })
        };
}
