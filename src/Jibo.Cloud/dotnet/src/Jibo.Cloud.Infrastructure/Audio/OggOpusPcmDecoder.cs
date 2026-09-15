using Concentus;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>
/// Decodes robot Ogg/Opus WebSocket frames to 16 kHz mono float PCM in-process.
/// </summary>
internal static class OggOpusPcmDecoder
{
    private const int OpusSampleRate = 48000;
    private const int TargetSampleRate = 16000;
    private const int DownsampleFactor = OpusSampleRate / TargetSampleRate;

    internal static float[] DecodeTo16kMono(IReadOnlyList<byte[]> frames)
    {
        var packets = OggOpusAudioNormalizer.EnumerateOpusPacketPayloads(frames).ToArray();
        if (packets.Length == 0)
            return [];

        var decoder = OpusCodecFactory.CreateDecoder(OpusSampleRate, 1);
        var pcm48k = new List<float>(packets.Length * 960);
        var scratch = new float[5760];

        foreach (var packet in packets)
        {
            if (packet.Length == 0)
                continue;

            try
            {
                var sampleCount = decoder.Decode(packet, scratch, scratch.Length, false);
                if (sampleCount > 0)
                    pcm48k.AddRange(scratch.AsSpan(0, sampleCount).ToArray());
            }
            catch
            {
                // Skip corrupt packets; keep decoding the rest of the turn.
            }
        }

        if (pcm48k.Count == 0)
            return [];

        var sampleCount16k = pcm48k.Count / DownsampleFactor;
        var pcm16k = new float[sampleCount16k];
        for (var i = 0; i < sampleCount16k; i++)
            pcm16k[i] = pcm48k[i * DownsampleFactor];

        return pcm16k;
    }
}
