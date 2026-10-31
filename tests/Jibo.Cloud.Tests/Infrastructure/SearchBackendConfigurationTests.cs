using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.DependencyInjection;
using Jibo.Cloud.Infrastructure.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class SearchBackendConfigurationTests
{
    // Prod's .env has "OPENJIBO_SEARCH_FALLBACK=" (empty); that must not hide the appsettings fallback.
    [Fact]
    public void EmptyFallbackVariable_LetsAppsettingsChooseTheFallback()
    {
        var previous = Environment.GetEnvironmentVariable("OPENJIBO_SEARCH_FALLBACK");
        Environment.SetEnvironmentVariable("OPENJIBO_SEARCH_FALLBACK", "");
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:Search:Primary"] = "none",
                ["OpenJibo:Search:Fallback"] = "ollama"
            }).Build();
            var services = new ServiceCollection();
            services.AddOpenJiboCloud(configuration);
            using var provider = services.BuildServiceProvider();
            Assert.Equal(SearchBackendKind.Ollama, provider.GetRequiredService<SearchBackendOptions>().Fallback?.Kind);
        }
        finally { Environment.SetEnvironmentVariable("OPENJIBO_SEARCH_FALLBACK", previous); }
    }
}
