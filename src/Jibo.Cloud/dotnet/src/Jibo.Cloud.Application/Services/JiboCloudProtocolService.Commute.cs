using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboCloudProtocolService
{
    private static bool IsCommuteOperation(string operation) =>
        operation is "GetCommuteAddresses" or "SetCommuteAddress" or "ClearCommuteAddress" or "GetNewsTopics" or "SetNewsTopics";

    /// <summary>
    /// BEacon's home address (Etc tab) and each person's job (People tab), geocoded here so the
    /// TomTom key stays on the server. Same robot scope and credentials as calendar feeds.
    /// </summary>
    private async Task<ProtocolDispatchResult> HandleCommuteAsync(string operation, ProtocolEnvelope envelope)
    {
        if (AuthorizeMemberCalendar(envelope) is { } rejected) return rejected;
        if (userIntegrationStore is null) return CalendarError(503, "Commute store is unavailable.");

        var scope = ResolveCalendarScope(envelope);
        var body = envelope.TryParseBody();
        var memberId = ReadString(body, "memberId") ?? CommuteAddressRecord.HomeMemberId;
        // Local-first robots keep their addresses and topics: answer without reading or saving the store.
        var localFirst = ReadBool(body, "localFirst");
        switch (operation)
        {
            case "GetCommuteAddresses":
                return ProtocolDispatchResult.Ok(new
                {
                    available = commuteRouting?.IsConfigured == true,
                    addresses = (localFirst ? [] : userIntegrationStore.GetCommuteAddresses(scope))
                        .Select(static item => new { memberId = item.MemberId, address = item.Address, mode = item.Mode }).ToArray()
                });
            case "SetCommuteAddress":
            {
                var address = ReadString(body, "address")?.Trim();
                if (string.IsNullOrWhiteSpace(address) || address.Length > 300)
                    return CalendarError(400, "An address is required.");
                if (commuteRouting is not { IsConfigured: true })
                    return CalendarError(503, "Address lookup isn't set up on this server.");
                var mode = ReadString(body, "mode")?.Trim().ToLowerInvariant();
                if (mode is not null && !CommuteAddressRecord.Modes.Contains(mode))
                    return CalendarError(400, "Mode must be driving, bicycling or walking.");
                var existing = localFirst ? null : userIntegrationStore.GetCommuteAddresses(scope).FirstOrDefault(item => item.MemberId == memberId);
                // Changing only the mode re-sends the saved address: skip the lookup then.
                var found = mode is not null && existing is not null && string.Equals(existing.Address, address, StringComparison.OrdinalIgnoreCase)
                    ? new GeocodedAddress(existing.Address, existing.Latitude, existing.Longitude, existing.City, null, null, null, null)
                    : await commuteRouting.GeocodeAsync(address);
                if (found is null) return CalendarError(400, "That address couldn't be found. Check it and try again.");
                if (!localFirst)
                    userIntegrationStore.UpsertCommuteAddress(new CommuteAddressRecord
                    {
                        Scope = scope,
                        MemberId = memberId,
                        Address = found.Address,
                        Latitude = found.Latitude,
                        Longitude = found.Longitude,
                        City = found.City,
                        MemberName = ReadString(body, "memberName"),
                        TimeZoneId = ReadString(body, "timeZone"),
                        Mode = mode ?? existing?.Mode
                    });
                return ProtocolDispatchResult.Ok(new
                {
                    address = found.Address, lat = found.Latitude, lng = found.Longitude, city = found.City,
                    state = found.State, stateAbbr = found.StateCode, country = found.Country, countryCode = found.CountryCode
                });
            }
            // Personal Reports tab: the news topics each person turned off.
            case "GetNewsTopics":
                return ProtocolDispatchResult.Ok(new
                {
                    topics = NewsTopics.All,
                    people = (localFirst ? [] : userIntegrationStore.GetNewsTopics(scope))
                        .Select(static item => new { memberId = item.MemberId, disabledTopics = item.DisabledTopics }).ToArray()
                });
            case "SetNewsTopics":
            {
                if (memberId == CommuteAddressRecord.HomeMemberId) return CalendarError(400, "memberId is required.");
                var disabled = body?.TryGetProperty("disabledTopics", out var list) == true && list.ValueKind == System.Text.Json.JsonValueKind.Array
                    ? list.EnumerateArray().Select(static item => item.GetString()?.Trim().ToLowerInvariant())
                        .Where(static topic => topic is not null && NewsTopics.All.Contains(topic)).Distinct().ToArray()
                    : [];
                userIntegrationStore.UpsertNewsTopics(new NewsTopicsRecord
                {
                    Scope = scope, MemberId = memberId, MemberName = ReadString(body, "memberName"), DisabledTopics = disabled!
                });
                return ProtocolDispatchResult.Ok(new { memberId, disabledTopics = disabled });
            }
            case "ClearCommuteAddress":
                userIntegrationStore.ClearCommuteAddress(scope, memberId);
                return ProtocolDispatchResult.Ok(new { cleared = true, memberId });
            default:
                return CalendarError(400, "Unknown commute operation.");
        }
    }
}
