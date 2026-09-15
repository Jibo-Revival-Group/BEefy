using Jibo.Cloud.Api.Hosting.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jibo.Cloud.Tests.Api;

public sealed class AdminConfigOverlayStoreTests
{
    [Fact]
    public void Upsert_WritesNestedJsonAndReadsFlatKeys()
    {
        var root = Path.Combine(Path.GetTempPath(), $"beefy-admin-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var overlayPath = Path.Combine(root, "admin-config-overlay.json");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["OpenJibo:AdminConfig:OverlayPath"] = overlayPath
                })
                .Build();
            var store = new AdminConfigOverlayStore(configuration, new FakeHostEnvironment(root));

            store.Upsert(new Dictionary<string, string?>
            {
                ["OpenJibo:Stt:EnableStreamingSherpa"] = "true",
                ["OpenJibo:Logging:MinimumLevel"] = "Warning"
            });

            var values = store.ReadFlatValues();
            Assert.Equal("true", values["OpenJibo:Stt:EnableStreamingSherpa"]);
            Assert.Equal("Warning", values["OpenJibo:Logging:MinimumLevel"]);
            Assert.True(File.Exists(overlayPath));

            store.Upsert(new Dictionary<string, string?>
            {
                ["OpenJibo:Logging:MinimumLevel"] = null
            });
            values = store.ReadFlatValues();
            Assert.False(values.ContainsKey("OpenJibo:Logging:MinimumLevel"));
            Assert.Equal("true", values["OpenJibo:Stt:EnableStreamingSherpa"]);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class FakeHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
