using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class OggOpusIncrementalPcmDecoderTests
{
    [Fact]
    public void DecodeNewTo16kMono_ReturnsEmpty_WhenNoFrames()
    {
        using var decoder = new OggOpusIncrementalPcmDecoder();
        Assert.Empty(decoder.DecodeNewTo16kMono([]));
    }

    [Fact]
    public void DecodeNewTo16kMono_DoesNotReconsumeSamePackets()
    {
        using var decoder = new OggOpusIncrementalPcmDecoder();
        var frames = new List<byte[]>
        {
            BuildOggPage(0x02, BuildOpusHead()),
            BuildOggPage(0x00, BuildOpusTags()),
            BuildOggPage(0x00, BuildOpusTocPacket(40)),
            BuildOggPage(0x04, BuildOpusTocPacket(40))
        };

        decoder.DecodeNewTo16kMono(frames);
        var second = decoder.DecodeNewTo16kMono(frames);
        Assert.Empty(second);

        frames.Add(BuildOggPage(0x00, BuildOpusTocPacket(40)));
        // New packet may or may not decode to samples; must not throw and must accept the cursor advance.
        _ = decoder.DecodeNewTo16kMono(frames);
        Assert.Empty(decoder.DecodeNewTo16kMono(frames));
    }

    private static byte[] BuildOpusHead()
    {
        var packet = new byte[19];
        "OpusHead"u8.CopyTo(packet);
        packet[8] = 1;
        packet[9] = 1;
        return packet;
    }

    private static byte[] BuildOpusTags()
    {
        var packet = new byte[16];
        "OpusTags"u8.CopyTo(packet);
        return packet;
    }

    private static byte[] BuildOpusTocPacket(int byteLength)
    {
        var packet = new byte[Math.Max(1, byteLength)];
        packet[0] = 0x68;
        for (var index = 1; index < packet.Length; index += 1)
            packet[index] = 0x11;
        return packet;
    }

    private static byte[] BuildOggPage(byte headerType, byte[] payload)
    {
        var page = new byte[27 + 1 + payload.Length];
        "OggS"u8.CopyTo(page);
        page[4] = 0;
        page[5] = headerType;
        page[26] = 1;
        page[27] = (byte)payload.Length;
        payload.CopyTo(page.AsSpan(28));
        return page;
    }
}
