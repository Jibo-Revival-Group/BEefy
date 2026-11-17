using Jibo.Cloud.Api.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Tests.Api;

public sealed class WebSocketTransportPolicyTests
{
    // Stock robots speak plain ws:// and prod sits behind a TLS proxy, so every mode accepts both.
    [Theory]
    [InlineData("self-hosted-isolated")]
    [InlineData("managed")]
    [InlineData(null)]
    public void IsAllowed_AcceptsPlainHttpInEveryMode(string? deploymentMode)
    {
        var policy = CreatePolicy(deploymentMode);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";

        Assert.True(policy.IsAllowed(context.Request));
    }

    [Fact]
    public void IsAllowed_AllowsDirectHttpsOutsideIsolatedSelfHosting()
    {
        var policy = CreatePolicy("self-hosted-hybrid");
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";

        Assert.True(policy.IsAllowed(context.Request));
    }

    [Theory]
    [InlineData("https", true)]
    [InlineData("http", true)]
    [InlineData("https,http", true)]
    public void IsAllowed_AcceptsAnyForwardedProtoInManagedContainerApp(
        string forwardedProto,
        bool expected)
    {
        var policy = CreatePolicy("managed", "api--revision-1");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;

        Assert.Equal(expected, policy.IsAllowed(context.Request));
    }

    [Theory]
    [InlineData("https", true)]
    [InlineData("http", true)]
    [InlineData("https,http", true)]
    public void IsAllowed_AcceptsAnyForwardedProtoInSelfHostedProxiedMode(
        string forwardedProto,
        bool expected)
    {
        var policy = CreatePolicy("self-hosted-proxied");
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Forwarded-Proto"] = forwardedProto;

        Assert.Equal(expected, policy.IsAllowed(context.Request));
    }

    [Fact]
    public void IsAllowed_AllowsHttpsSchemeInSelfHostedProxiedModeWithoutForwardedHeader()
    {
        var policy = CreatePolicy("self-hosted-proxied");
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";

        Assert.True(policy.IsAllowed(context.Request));
    }

    private static WebSocketTransportPolicy CreatePolicy(
        string? deploymentMode,
        string? containerAppRevision = null,
        string? securityMode = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:Deployment:Mode"] = deploymentMode,
                ["CONTAINER_APP_REVISION"] = containerAppRevision,
                ["OpenJibo:Security:Mode"] = securityMode
            })
            .Build();
        return new WebSocketTransportPolicy(configuration);
    }
}
