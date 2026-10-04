namespace Jibo.Cloud.Application.Abstractions;

/// <summary>Optional local neural correction of an unrecognized speech transcript.</summary>
public interface IAsrCorrectionModel
{
    Task<AsrCorrection?> TryCorrectAsync(string transcript, CancellationToken cancellationToken = default);
}

/// <param name="Confidence">Contextual improvement score mapped to [0,1]; not calibrated intent confidence.</param>
public sealed record AsrCorrection(string Text, double Confidence, string Model);
