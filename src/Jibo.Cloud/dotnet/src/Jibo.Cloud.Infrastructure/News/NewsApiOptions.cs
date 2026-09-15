namespace Jibo.Cloud.Infrastructure.News;

public sealed class NewsApiOptions
{
    public string BaseUrl { get; set; } = "https://newsapi.org";

    public string? ApiKey { get; set; }

    public string UserAgent { get; set; } = "OpenJiboCloud/1.0";

    public string Country { get; set; } = "us";

    public string Language { get; set; } = "en";

    public string FallbackQuery { get; set; } = "robotics";

    public string[] DefaultCategories { get; set; } =
    [
        "general",
        "technology",
        "sports",
        "business"
    ];

    /// <summary>
    /// Legacy / unused for successful briefings. Success entries now expire at the
    /// next UTC half-hour (:00 or :30) via <c>HalfHourAlignedCacheExpiry</c>.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 300;

    public int FailureCacheTtlSeconds { get; set; } = 45;
}
