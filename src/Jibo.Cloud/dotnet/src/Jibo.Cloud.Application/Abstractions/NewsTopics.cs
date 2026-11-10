namespace Jibo.Cloud.Application.Abstractions;

/// <summary>The news topics Jibo knows ("general" is Top stories); BEacon lists these, all on by default.</summary>
public static class NewsTopics
{
    public static readonly IReadOnlyList<string> All =
        ["general", "world", "nation", "business", "technology", "entertainment", "science", "sports", "health"];
}
