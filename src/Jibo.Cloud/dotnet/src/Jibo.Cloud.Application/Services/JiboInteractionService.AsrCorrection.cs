using System.Diagnostics;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    internal const string ModelCorrectedTranscriptKey = "stt:modelCorrectedTranscript";

    private async Task<(string Transcript, string Intent)?> TryCorrectUnrecognizedCommandAsync(
        TurnContext turn, string transcript, Func<string, string> resolveIntent, CancellationToken cancellationToken)
    {
        if (!_asrCorrectionOptions.Enabled || asrCorrectionModel is null ||
            transcript.Length is 0 or > 256 ||
            (turn.Locale is { } locale && !locale.StartsWith("en", StringComparison.OrdinalIgnoreCase)))
            return null;

        var start = Stopwatch.GetTimestamp();
        var outcome = "unavailable";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Math.Clamp(_asrCorrectionOptions.TimeoutMilliseconds, 1, 500));
            var correction = await asrCorrectionModel.TryCorrectAsync(transcript, deadline.Token).WaitAsync(deadline.Token);
            if (correction is null) return null;
            outcome = "rejected";
            var minimumConfidence = double.IsFinite(_asrCorrectionOptions.MinimumConfidence)
                ? Math.Clamp(_asrCorrectionOptions.MinimumConfidence, 0, 1) : 0.8;
            if (!double.IsFinite(correction.Confidence) || correction.Confidence is < 0 or > 1 ||
                correction.Confidence < minimumConfidence ||
                string.IsNullOrWhiteSpace(correction.Text) ||
                !AsrCorrectionAcceptance.IsConservativeEdit(transcript, correction.Text))
                return null;

            var candidate = TranscriptTextNormalizer.NormalizeLooseText(correction.Text);
            deadline.Token.ThrowIfCancellationRequested();
            var intent = resolveIntent(candidate);
            if (IsFallbackOrEchoIntent(intent)) return null;

            outcome = "accepted";
            turn.Attributes[ModelCorrectedTranscriptKey] = candidate;
            turn.Attributes["stt:correctionModel"] = correction.Model;
            turn.Attributes["stt:correctionContextScore"] = correction.Confidence;
            turn.Attributes["stt:correctionDurationMs"] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return (candidate, intent);
        }
        catch (OperationCanceledException)
        {
            outcome = cancellationToken.IsCancellationRequested ? "canceled" : "timeout";
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception ex)
        {
            outcome = "failure";
            logger?.LogDebug(ex, "Local ASR correction failed; retaining original transcript.");
            return null;
        }
        finally
        {
            _asrCorrectionMetrics.TurnPhaseCompleted("asr_correction", outcome,
                Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    private static TurnContext WithModelCorrectedTranscript(TurnContext turn, string transcript) => new()
    {
        TurnId = turn.TurnId,
        SessionId = turn.SessionId,
        TimestampUtc = turn.TimestampUtc,
        InputMode = turn.InputMode,
        SourceKind = turn.SourceKind,
        WakePhrase = turn.WakePhrase,
        RawTranscript = turn.RawTranscript,
        NormalizedTranscript = transcript,
        DeviceId = turn.DeviceId,
        HostName = turn.HostName,
        RequestId = turn.RequestId,
        ProtocolService = turn.ProtocolService,
        ProtocolOperation = turn.ProtocolOperation,
        FirmwareVersion = turn.FirmwareVersion,
        ApplicationVersion = turn.ApplicationVersion,
        Locale = turn.Locale,
        TimeZone = turn.TimeZone,
        IsFollowUpEligible = turn.IsFollowUpEligible,
        Attributes = turn.Attributes
    };
}
