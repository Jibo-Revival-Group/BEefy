using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;

namespace Jibo.Cloud.Api.Hosting;

/// <summary>Trust forwarded scheme/address only from loopback and explicitly configured proxy IPs.</summary>
public static class TrustedProxyConfiguration
{
    public const string Section = "OpenJibo:Hosting:TrustedProxies";

    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownProxies.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        foreach (var entry in configuration.GetSection(Section).GetChildren())
        {
            if (!IPAddress.TryParse(entry.Value, out var address))
                throw new ArgumentException($"{Section}:{entry.Key} must be an explicit proxy IP address.");
            if (!options.KnownProxies.Contains(address)) options.KnownProxies.Add(address);
        }
    }
}
