namespace Jibo.Cloud.Infrastructure.Audio;

internal static class OggOpusAudioNormalizer
{
    public static byte[] Normalize(IReadOnlyList<byte[]> pages)
        => Application.Audio.OggOpusAudioNormalizer.Normalize(pages);

    public static IEnumerable<byte[]> EnumerateOpusPacketPayloads(IReadOnlyList<byte[]> pages)
        => Application.Audio.OggOpusAudioNormalizer.EnumerateOpusPacketPayloads(pages);
}
