namespace Jibo.Cloud.Infrastructure.News;

public sealed class GoogleNewsOptions
{
    public string BaseUrl { get; set; } = "https://news.google.com";

    public string UserAgent { get; set; } = "OpenJiboCloud/1.0";

    public string Country { get; set; } = "US";

    public string Language { get; set; } = "en-US";

    /// <summary>Topics used when the user didn't ask for one and set none in BEacon (5 of them at random).</summary>
    public string[] DefaultCategories { get; set; } = [.. Jibo.Cloud.Application.Abstractions.NewsTopics.All];

}
