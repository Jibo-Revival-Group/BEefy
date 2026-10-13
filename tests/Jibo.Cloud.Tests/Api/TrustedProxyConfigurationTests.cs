using System.Net;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Infrastructure.Audio;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jibo.Cloud.Tests.Api;

public sealed class TrustedProxyConfigurationTests
{
    [Theory]
    [InlineData(null, "172.18.0.2", "https", 426)]
    [InlineData("172.18.0.2", "172.18.0.2", "https", 401)]
    [InlineData("172.18.0.2", "172.18.0.3", "https", 426)]
    [InlineData("172.18.0.2", "172.18.0.2", "http", 426)]
    [InlineData(null, "127.0.0.1", "https", 401)]
    [InlineData(null, "::1", "https", 401)]
    public async Task ForwardedHttpsIsAcceptedOnlyFromTrustedProxy(
        string? trustedProxy, string sourceIp, string forwardedScheme, int expectedStatus)
    {
        var values = new Dictionary<string, string?>();
        if (trustedProxy is not null) values[TrustedProxyConfiguration.Section + ":0"] = trustedProxy;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new ForwardedHeadersOptions();
        TrustedProxyConfiguration.Configure(options, configuration);
        var workerOptions = new BeefyErOptions { Enabled = true, SharedSecret = new string('s', 43) };
        var handler = new BeefyErWebSocketHandler(workerOptions,
            new BeefyErBroker(workerOptions, NullLogger<BeefyErBroker>.Instance));
        var middleware = new ForwardedHeadersMiddleware(handler.HandleAsync,
            NullLoggerFactory.Instance, Options.Create(options));
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(sourceIp);
        context.Request.Scheme = "http";
        context.Request.Headers["X-Forwarded-Proto"] = forwardedScheme;
        await middleware.Invoke(context);
        // Trusted HTTPS progresses to authentication (401), never bypassing it.
        Assert.Equal(expectedStatus, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("172.18.0.0/16")]
    [InlineData("proxy.example.com")]
    public void TrustConfigurationRejectsNonAddressEntries(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TrustedProxyConfiguration.Section + ":0"] = value
        }).Build();
        Assert.Throws<ArgumentException>(() => TrustedProxyConfiguration.Configure(new ForwardedHeadersOptions(), configuration));
    }
}
