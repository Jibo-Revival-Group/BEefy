namespace Jibo.Cloud.Infrastructure.News;

public sealed class GoogleNewsOptions
{
    public string BaseUrl { get; set; } = "https://news.google.com";

    public string UserAgent { get; set; } = "OpenJiboCloud/1.0";

    public string Country { get; set; } = "US";

    public string Language { get; set; } = "en-US";

    /// <summary>One top headline from each, in order, when the user didn't ask for a category.</summary>
    public string[] DefaultCategories { get; set; } = ["technology", "science", "sports"];

    public int FailureCacheTtlSeconds { get; set; } = 45;
}
