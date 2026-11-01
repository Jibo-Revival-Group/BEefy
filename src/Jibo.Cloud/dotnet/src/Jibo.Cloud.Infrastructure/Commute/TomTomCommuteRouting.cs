using System.Globalization;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Commute;

/// <summary>TomTom Search (geocode) and Routing (live traffic). Key: TOMTOM_API_KEY.</summary>
public sealed class TomTomCommuteRouting(HttpClient httpClient, ILogger<TomTomCommuteRouting> logger) : ICommuteRouting
{
    private const string BaseUrl = "https://api.tomtom.com";
    private static string? ApiKey => Environment.GetEnvironmentVariable("TOMTOM_API_KEY") is { Length: > 0 } key ? key.Trim() : null;

    public bool IsConfigured => ApiKey is not null;

    public async Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken cancellationToken = default)
    {
        if (ApiKey is not { } key || string.IsNullOrWhiteSpace(address)) return null;
        var root = await GetJsonAsync(
            $"{BaseUrl}/search/2/geocode/{Uri.EscapeDataString(address.Trim())}.json?limit=1&key={Uri.EscapeDataString(key)}",
            cancellationToken);
        if (root is not { } json || !json.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
        var first = results[0];
        if (!first.TryGetProperty("position", out var position)) return null;
        first.TryGetProperty("address", out var parts);
        string? Part(string name) => parts.ValueKind == JsonValueKind.Object && parts.TryGetProperty(name, out var value) &&
                                     value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return new GeocodedAddress(
            Part("freeformAddress") ?? address.Trim(),
            position.GetProperty("lat").GetDouble(),
            position.GetProperty("lon").GetDouble(),
            Part("municipality"),
            Part("countrySubdivisionName") ?? Part("countrySubdivision"),
            Part("countrySubdivisionCode") ?? Part("countrySubdivision"),
            Part("country"),
            Part("countryCode"));
    }

    public async Task<CommuteRoute?> RouteAsync(double fromLat, double fromLng, double toLat, double toLng, string mode,
        CancellationToken cancellationToken = default)
    {
        if (ApiKey is not { } key) return null;
        // TomTom has no public-transit routing; transit commutes use driving times.
        var travelMode = mode.Trim().ToLowerInvariant() switch
        {
            "walking" => "pedestrian",
            "bicycling" => "bicycle",
            _ => "car"
        };
        var points = string.Create(CultureInfo.InvariantCulture, $"{fromLat},{fromLng}:{toLat},{toLng}");
        var root = await GetJsonAsync(
            $"{BaseUrl}/routing/1/calculateRoute/{points}/json?traffic=true&departAt=now&travelMode={travelMode}&key={Uri.EscapeDataString(key)}",
            cancellationToken);
        if (root is not { } json || !json.TryGetProperty("routes", out var routes) ||
            routes.ValueKind != JsonValueKind.Array || routes.GetArrayLength() == 0) return null;
        var summary = routes[0].GetProperty("summary");
        return new CommuteRoute(
            summary.GetProperty("travelTimeInSeconds").GetInt32(),
            summary.TryGetProperty("trafficDelayInSeconds", out var delay) ? delay.GetInt32() : 0);
    }

    private async Task<JsonElement?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("TomTom request failed with HTTP {Status}", (int)response.StatusCode);
                return null;
            }
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or TaskCanceledException
                                              or KeyNotFoundException or InvalidOperationException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            logger.LogWarning("TomTom request failed: {Error}", exception.GetType().Name);
            return null;
        }
    }
}
