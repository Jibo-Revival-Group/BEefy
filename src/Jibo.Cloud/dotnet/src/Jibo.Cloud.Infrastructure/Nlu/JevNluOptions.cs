using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Infrastructure.Nlu;

public sealed class JevNluOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "https://openrouter.ai/api/alpha/decisions";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "typesafe/jev-1.13";
    public int TimeoutMs { get; set; } = 1000;
    public double MinProbability { get; set; } = 0.85;

    public static JevNluOptions Resolve(IConfiguration? configuration,
        Func<string, string?>? environment = null)
    {
        var options = new JevNluOptions();
        configuration?.GetSection("OpenJibo:Nlu:Jev").Bind(options);
        environment ??= Environment.GetEnvironmentVariable;
        if (bool.TryParse(environment("OPENJIBO_JEV_ENABLED"), out var enabled)) options.Enabled = enabled;
        options.Endpoint = environment("OPENJIBO_JEV_ENDPOINT") ?? options.Endpoint;
        options.ApiKey = environment("OPENJIBO_JEV_API_KEY") ?? options.ApiKey;
        options.Model = environment("OPENJIBO_JEV_MODEL") ?? options.Model;
        if (int.TryParse(environment("OPENJIBO_JEV_TIMEOUT_MS"), out var timeout)) options.TimeoutMs = timeout;
        if (double.TryParse(environment("OPENJIBO_JEV_MIN_PROBABILITY"), CultureInfo.InvariantCulture, out var probability))
            options.MinProbability = probability;
        return options;
    }

    internal bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(Model) && TimeoutMs is > 0 and <= 1000 &&
        double.IsFinite(MinProbability) && MinProbability is >= 0 and <= 1 &&
        Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";
}
