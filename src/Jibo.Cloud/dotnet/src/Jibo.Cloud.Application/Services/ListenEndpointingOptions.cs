namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Controls model-driven listen endpointing (Sherpa IsEndpoint + completeness).
/// When disabled, BEefy keeps the legacy Opus byte-density / arrival-gap silence windows.
/// </summary>
public sealed class ListenEndpointingOptions
{
    /// <summary>
    /// Prefer Sherpa streaming endpoint detection over the fixed 350ms silence timer.
    /// Requires <c>OpenJibo:Stt:EnableStreamingSherpa</c> and a resolvable Zipformer model.
    /// Default false so existing tests and deployments keep current timing until opted in.
    /// </summary>
    public bool EnableModelEndpointing { get; set; }

    /// <summary>
    /// Sherpa Rule2 trailing-silence threshold in seconds (measured on decoded speech).
    /// Shorter = faster finalize for continuous speakers; longer = more mid-sentence pause tolerance.
    /// </summary>
    public float Rule2MinTrailingSilenceSeconds { get; set; } = 0.6f;

    /// <summary>
    /// Sherpa Rule1 trailing silence (shorter rule). Kept slightly below Rule2.
    /// </summary>
    public float Rule1MinTrailingSilenceSeconds { get; set; } = 0.4f;

    /// <summary>
    /// Fallback max utterance length in seconds when the robot does not advertise
    /// <c>maxSpeechTimeout</c>. Rule3 forces an endpoint after this much speech.
    /// </summary>
    public float DefaultMaxUtteranceSeconds { get; set; } = 20f;
}
