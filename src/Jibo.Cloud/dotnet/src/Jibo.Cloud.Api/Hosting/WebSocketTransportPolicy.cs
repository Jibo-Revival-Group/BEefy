namespace Jibo.Cloud.Api.Hosting;

/// <summary>
/// Requires TLS for managed, hybrid, and proxied WebSocket traffic while preserving the
/// explicitly isolated, single-robot HTTP compatibility deployment.
/// </summary>
internal sealed class WebSocketTransportPolicy(IConfiguration configuration)
{
    private const string DeploymentModeConfigurationKey = "OpenJibo:Deployment:Mode";
    private const string SecurityModeConfigurationKey = "OpenJibo:Security:Mode";
    private const string IsolatedSelfHostedMode = "self-hosted-isolated";
    private const string ProxiedSelfHostedMode = "self-hosted-proxied";
    private const string ManagedMode = "managed";

    internal WebSocketTransportPolicy(bool isolatedSelfHosted)
        : this(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DeploymentModeConfigurationKey] = isolatedSelfHosted ? IsolatedSelfHostedMode : ManagedMode
            })
            .Build())
    {
    }

    internal bool IsAllowed(HttpRequest request)
    {
        var deploymentMode = configuration[DeploymentModeConfigurationKey];
        if (string.Equals(deploymentMode, IsolatedSelfHostedMode, StringComparison.OrdinalIgnoreCase) &&
            !IsSecurityModeEnabled(deploymentMode))
            return true;

        // Prefer request.IsHttps so ASP.NET ForwardedHeaders (or direct TLS) can
        // mark the connection as secure before this policy runs.
        if (request.IsHttps)
            return true;

        // Fallbacks for TLS terminated in front of Kestrel when ForwardedHeaders
        // has not yet rewritten the scheme (or the header remains visible).
        if (TrustsForwardedHttps(deploymentMode))
        {
            var forwardedValues = request.Headers["X-Forwarded-Proto"];
            return forwardedValues.Count == 1 &&
                   string.Equals(forwardedValues[0], "https", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private bool TrustsForwardedHttps(string? deploymentMode)
    {
        if (string.Equals(deploymentMode, ProxiedSelfHostedMode, StringComparison.OrdinalIgnoreCase))
            return true;

        // Azure Container Apps terminates TLS before forwarding to Kestrel. Honor
        // its normalized single-value header only inside an identified managed
        // revision.
        return string.Equals(deploymentMode, ManagedMode, StringComparison.OrdinalIgnoreCase) &&
               !string.IsNullOrWhiteSpace(configuration["CONTAINER_APP_REVISION"]);
    }

    private bool IsSecurityModeEnabled(string? deploymentMode)
    {
        var configuredValue = configuration[SecurityModeConfigurationKey];
        if (bool.TryParse(configuredValue, out var enabled))
            return enabled;

        // Preserve stock-robot HTTP compatibility only for explicitly isolated
        // self-hosting. Every other deployment mode remains fail-closed.
        return !string.Equals(deploymentMode, IsolatedSelfHostedMode, StringComparison.OrdinalIgnoreCase);
    }
}
