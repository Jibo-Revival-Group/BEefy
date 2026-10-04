using Concentus;
using Jibo.Cloud.Application.Audio;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Stateful Opus→16 kHz PCM decoder that only consumes newly arrived packets.
/// </summary>
internal sealed class OggOpusIncrementalPcmDecoder : IDisposable
{
    private const int TargetSampleRate = 16000;

    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(TargetSampleRate, 1);
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

        var pcm16k = new List<float>((packets.Length - _consumedPackets) * 320);
        var scratch = new float[1920];

        for (var i = _consumedPackets; i < packets.Length; i++)
        {
            var packet = packets[i];
            if (packet.Length == 0)
                continue;

            try
            {
                var sampleCount = _decoder.Decode(packet, scratch, scratch.Length, false);
                if (sampleCount > 0)
                    pcm16k.AddRange(scratch.AsSpan(0, sampleCount).ToArray());
            }
            catch
            {
                // Skip corrupt packets; keep decoding the rest of the turn.
            }
        }

        _consumedPackets = packets.Length;

        if (pcm16k.Count == 0)
            return [];

        // Decode at Sherpa's rate: dropping 48 kHz samples without a low-pass
        // filter aliases high-frequency noise into the speech band.
        return pcm16k.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_decoder as IDisposable)?.Dispose();
    }
}
