namespace Jibo.Cloud.Application.Abstractions;

/// <summary>Address lookup and live travel times (TomTom in production).</summary>
public interface ICommuteRouting
{
    bool IsConfigured { get; }
    Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken cancellationToken = default);
    Task<CommuteRoute?> RouteAsync(double fromLat, double fromLng, double toLat, double toLng, string mode,
        CancellationToken cancellationToken = default);
}

public sealed record GeocodedAddress(
    string Address,
    double Latitude,
    double Longitude,
    string? City,
    string? State,
    string? StateCode,
    string? Country,
    string? CountryCode);

public sealed record CommuteRoute(int TravelTimeSeconds, int TrafficDelaySeconds);
