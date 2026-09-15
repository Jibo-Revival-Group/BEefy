using Concentus;
using Jibo.Cloud.Application.Audio;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Stateful Opus→16 kHz PCM decoder that only consumes newly arrived packets.
/// </summary>
internal sealed class OggOpusIncrementalPcmDecoder : IDisposable
{
    private const int OpusSampleRate = 48000;
    private const int TargetSampleRate = 16000;
    private const int DownsampleFactor = OpusSampleRate / TargetSampleRate;

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, 1);
    private int _consumedPackets;
    private bool _disposed;

    /// <summary>
    /// Decode packets that have not yet been consumed from <paramref name="frames"/>.
    /// Returns an empty array when there is no new audio.
    /// </summary>
    public float[] DecodeNewTo16kMono(IReadOnlyList<byte[]> frames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var packets = OggOpusAudioNormalizer.EnumerateOpusPacketPayloads(frames).ToArray();
        if (packets.Length <= _consumedPackets)
            return [];

        var pcm48k = new List<float>((packets.Length - _consumedPackets) * 960);
        var scratch = new float[5760];

        for (var i = _consumedPackets; i < packets.Length; i++)
        {
            var packet = packets[i];
            if (packet.Length == 0)
                continue;

            try
            {
                var sampleCount = _decoder.Decode(packet, scratch, scratch.Length, false);
                if (sampleCount > 0)
                    pcm48k.AddRange(scratch.AsSpan(0, sampleCount).ToArray());
            }
            catch
            {
                // Skip corrupt packets; keep decoding the rest of the turn.
            }
        }

        _consumedPackets = packets.Length;

        if (pcm48k.Count == 0)
            return [];

        var sampleCount16k = pcm48k.Count / DownsampleFactor;
        var pcm16k = new float[sampleCount16k];
        for (var i = 0; i < sampleCount16k; i++)
            pcm16k[i] = pcm48k[i * DownsampleFactor];

        return pcm16k;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_decoder as IDisposable)?.Dispose();
    }
}
