using Concentus;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Decodes robot Ogg/Opus WebSocket frames to 16 kHz mono float PCM in-process.
/// </summary>
internal static class OggOpusPcmDecoder
{
    private const int TargetSampleRate = 16000;

    internal static float[] DecodeTo16kMono(IReadOnlyList<byte[]> frames)
    {
        var packets = OggOpusAudioNormalizer.EnumerateOpusPacketPayloads(frames).ToArray();
        if (packets.Length == 0)
            return [];

        var decoder = OpusCodecFactory.CreateDecoder(TargetSampleRate, 1);
        using var decoderLifetime = decoder as IDisposable;
        var pcm16k = new List<float>(packets.Length * 320);
        var scratch = new float[1920];

        foreach (var packet in packets)
        {
            if (packet.Length == 0)
                continue;

            try
            {
                var sampleCount = decoder.Decode(packet, scratch, scratch.Length, false);
                if (sampleCount > 0)
                    pcm16k.AddRange(scratch.AsSpan(0, sampleCount).ToArray());
            }
            catch
            {
                // Skip corrupt packets; keep decoding the rest of the turn.
            }
        }

        if (pcm16k.Count == 0)
            return [];

        // Decode at Sherpa's rate: dropping 48 kHz samples without a low-pass
        // filter aliases high-frequency noise into the speech band.
        return pcm16k.ToArray();
    }
}
