namespace Jibo.Cloud.Application.Abstractions;

public interface INluClassifier
{
    Task<NluClassification?> ClassifyAsync(string transcript, CancellationToken cancellationToken = default);
}

public sealed record NluClassification(string Intent, double Probability, string Provider, string Model);
