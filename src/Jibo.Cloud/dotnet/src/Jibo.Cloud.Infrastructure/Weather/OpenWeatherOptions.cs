namespace Jibo.Cloud.Infrastructure.Weather;

public sealed class OpenWeatherOptions
{
    public string BaseUrl { get; set; } = "https://api.openweathermap.org";

    public string? ApiKey { get; set; }

    public string DefaultLocation { get; set; } = "Boston,US";

    public bool UseCelsius { get; set; }

    /// <summary>
    /// Legacy / unused for successful weather payloads. Success entries now expire at the
    /// next UTC half-hour (:00 or :30) via <c>HalfHourAlignedCacheExpiry</c>.
    /// </summary>
    public int CurrentCacheTtlSeconds { get; set; } = 120;

    /// <summary>
    /// Legacy / unused for successful forecast payloads. Success entries now expire at the
    /// next UTC half-hour (:00 or :30) via <c>HalfHourAlignedCacheExpiry</c>.
    /// </summary>
    public int ForecastCacheTtlSeconds { get; set; } = 600;

    public int GeocodeCacheTtlSeconds { get; set; } = 21600;

    public int FailureCacheTtlSeconds { get; set; } = 45;
}
