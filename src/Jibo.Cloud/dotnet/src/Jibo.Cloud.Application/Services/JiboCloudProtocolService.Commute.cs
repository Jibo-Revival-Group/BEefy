using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboCloudProtocolService
{
    private static bool IsCommuteOperation(string operation) =>
        operation is "GetCommuteAddresses" or "SetCommuteAddress" or "ClearCommuteAddress";

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
        switch (operation)
        {
            case "GetCommuteAddresses":
                return ProtocolDispatchResult.Ok(new
                {
                    available = commuteRouting?.IsConfigured == true,
                    addresses = userIntegrationStore.GetCommuteAddresses(scope)
                        .Select(static item => new { memberId = item.MemberId, address = item.Address }).ToArray()
                });
            case "SetCommuteAddress":
            {
                var address = ReadString(body, "address")?.Trim();
                if (string.IsNullOrWhiteSpace(address) || address.Length > 300)
                    return CalendarError(400, "An address is required.");
                if (commuteRouting is not { IsConfigured: true })
                    return CalendarError(503, "Address lookup isn't set up on this server.");
                var found = await commuteRouting.GeocodeAsync(address);
                if (found is null) return CalendarError(400, "That address couldn't be found. Check it and try again.");
                userIntegrationStore.UpsertCommuteAddress(new CommuteAddressRecord
                {
                    Scope = scope,
                    MemberId = memberId,
                    Address = found.Address,
                    Latitude = found.Latitude,
                    Longitude = found.Longitude,
                    MemberName = ReadString(body, "memberName"),
                    TimeZoneId = ReadString(body, "timeZone")
                });
                return ProtocolDispatchResult.Ok(new
                {
                    address = found.Address, lat = found.Latitude, lng = found.Longitude, city = found.City,
                    state = found.State, stateAbbr = found.StateCode, country = found.Country, countryCode = found.CountryCode
                });
            }
            case "ClearCommuteAddress":
                userIntegrationStore.ClearCommuteAddress(scope, memberId);
                return ProtocolDispatchResult.Ok(new { cleared = true, memberId });
            default:
                return CalendarError(400, "Unknown commute operation.");
        }
    }
}
