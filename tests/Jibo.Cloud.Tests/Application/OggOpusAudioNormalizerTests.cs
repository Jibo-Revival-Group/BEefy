using System.Buffers.Binary;
using System.Diagnostics;
using Concentus;
using Concentus.Enums;
using Jibo.Cloud.Application.Audio;
using Jibo.Cloud.Infrastructure.Audio;
using Normalizer = Jibo.Cloud.Application.Audio.OggOpusAudioNormalizer;

namespace Jibo.Cloud.Tests.Application;

public sealed class OggOpusAudioNormalizerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Theory]
    [InlineData(-4793230771707309754L)]
    [InlineData(4793230771707309754L)]
    [InlineData(long.MaxValue - 1000)]
    public void Normalize_RepairsArbitraryOrigins_PreservingPacketsAndTrim(long origin)
    {
        var valid = CreateStream();
        var broken = ShiftTimeline(valid, origin);
        var repaired = SplitPages(Normalizer.Normalize(broken));
        Assert.Equal(valid.Count, repaired.Count);
        for (var i = 0; i < valid.Count; i++)
        {
            Assert.Equal(Granule(valid[i]), Granule(repaired[i]));
            Assert.Equal(valid[i].AsSpan(27 + valid[i][26]).ToArray(),
                repaired[i].AsSpan(27 + repaired[i][26]).ToArray());
            AssertValidChecksum(repaired[i]);
        }
        Assert.Equal(Normalizer.Normalize(valid), Normalizer.Normalize(broken));
        Assert.Equal(OggOpusPcmDecoder.DecodeTo16kMono(valid), OggOpusPcmDecoder.DecodeTo16kMono(broken));
        using var incremental = new OggOpusIncrementalPcmDecoder();
        var pcm = new List<float>();
        for (var count = 1; count <= broken.Count; count++)
            pcm.AddRange(incremental.DecodeNewTo16kMono(broken.Take(count).ToArray()));
        Assert.Equal(OggOpusPcmDecoder.DecodeTo16kMono(valid), pcm.ToArray());
    }

    [Theory]
    [InlineData(-4793230771707309754L)]
    [InlineData(4793230771707309754L)]
    public void Normalize_RestoresIdenticalFfmpegPcm(long origin)
    {
        var directory = Path.Combine(Path.GetTempPath(), "beefy-ogg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var valid = CreateStream();
            var broken = ShiftTimeline(valid, origin);
            var expected = DecodeFfmpeg(directory, "valid", valid.SelectMany(p => p).ToArray());
            if (expected is null) return; // FFmpeg integration is optional on developer machines.
            Assert.NotEmpty(expected);
            if (origin < 0)
            {
                var damaged = DecodeFfmpeg(directory, "damaged", broken.SelectMany(p => p).ToArray(), requireSuccess: false);
                Assert.Empty(damaged ?? []);
            }
            var actual = DecodeFfmpeg(directory, "repaired", Normalizer.Normalize(broken));
            Assert.Equal(expected, actual);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(0, 480)]
    [InlineData(1, 960)]
    [InlineData(2, 1920)]
    [InlineData(3, 2880)]
    [InlineData(7, 2880)]
    [InlineData(11, 2880)]
    public void PacketDuration_HandlesSilkFrames(int configuration, int samples)
    {
        var page = Page([(byte)(configuration << 3), 1], 0, 0, 0);
        var packet = Assert.Single(Normalizer.EnumerateAudioPackets([page]));
        Assert.Equal((ulong)samples, packet.SampleCount);
        Assert.Equal((ulong)samples, Granule(Normalizer.Normalize([page])));
    }

    [Fact]
    public void Normalize_ContinuedPacket_UsesNoCompletedPacketSentinel()
    {
        var stream = CreateStream();
        var packet = new byte[300];
        packet[0] = 0xf8; // One 20 ms CELT frame; only the TOC matters for timing.
        var first = new byte[27 + 1 + 255];
        "OggS"u8.CopyTo(first);
        first[26] = 1;
        first[27] = 255;
        packet.AsSpan(0, 255).CopyTo(first.AsSpan(28));
        var second = Page(packet[255..], 123456789, 3, 0x01);
        var repaired = SplitPages(Normalizer.Normalize([stream[0], stream[1], first, second]));
        Assert.Equal(ulong.MaxValue, Granule(repaired[2]));
        Assert.Equal(960UL, Granule(repaired[3]));
        Assert.Equal(1, repaired[3][5] & 1);
        Assert.Equal(packet, Assert.Single(Normalizer.EnumerateOpusPacketPayloads(repaired)));
        foreach (var page in repaired) AssertValidChecksum(page);
    }

    [Fact]
    public void Normalize_SingleAudioPage_PreservesPreSkipAndEndTrim()
    {
        var stream = CreateStream();
        var last = stream[2].ToArray();
        last[5] |= 4;
        BinaryPrimitives.WriteUInt64LittleEndian(last.AsSpan(6), 840);
        var repaired = SplitPages(Normalizer.Normalize([stream[0], stream[1], last]));
        Assert.Equal(840UL, Granule(repaired[2]));
        Assert.Equal(312, BinaryPrimitives.ReadUInt16LittleEndian(repaired[0].AsSpan(28 + 10)));
    }

    private static List<byte[]> CreateStream()
    {
        var head = new byte[19];
        "OpusHead"u8.CopyTo(head);
        head[8] = 1;
        head[9] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(head.AsSpan(10), 312);
        BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(12), 48000);
        var tags = new byte[16];
        "OpusTags"u8.CopyTo(tags);
        var pages = new List<byte[]> { Page(head, 0, 0, 2), Page(tags, 0, 1, 0) };
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        using var lifetime = encoder as IDisposable;
        for (var frame = 0; frame < 12; frame++)
        {
            var pcm = Enumerable.Range(0, 960)
                .Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (frame * 960 + i) / 48000)))
                .ToArray();
            var buffer = new byte[4000];
            var length = encoder.Encode(pcm, pcm.Length, buffer, buffer.Length);
            pages.Add(Page(buffer[..length], (ulong)((frame + 1) * 960 - (frame == 11 ? 120 : 0)),
                (uint)(frame + 2), frame == 11 ? (byte)4 : (byte)0));
        }
        return pages;
    }

    private static List<byte[]> ShiftTimeline(List<byte[]> pages, long origin) => pages.Select((page, i) =>
    {
        var copy = page.ToArray();
        if (i >= 2) BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(6), unchecked(Granule(page) + (ulong)origin));
        SetChecksum(copy);
        return copy;
    }).ToList();

    private static byte[] Page(byte[] packet, ulong granule, uint sequence, byte flags)
    {
        var segments = packet.Length / 255 + 1;
        var page = new byte[27 + segments + packet.Length];
        "OggS"u8.CopyTo(page);
        page[5] = flags;
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(6), granule);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(14), 123);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(18), sequence);
        page[26] = (byte)segments;
        for (var i = 0; i < segments; i++) page[27 + i] = (byte)Math.Min(255, packet.Length - i * 255);
        packet.CopyTo(page, 27 + segments);
        SetChecksum(page);
        return page;
    }

    private static ulong Granule(byte[] page) => BinaryPrimitives.ReadUInt64LittleEndian(page.AsSpan(6));

    private static List<byte[]> SplitPages(byte[] stream)
    {
        var pages = new List<byte[]>();
        for (var offset = 0; offset < stream.Length;)
        {
            var segments = stream[offset + 26];
            var length = 27 + segments;
            for (var i = 0; i < segments; i++) length += stream[offset + 27 + i];
            pages.Add(stream.AsSpan(offset, length).ToArray());
            offset += length;
        }
        return pages;
    }

    private static void SetChecksum(byte[] page)
    {
        page.AsSpan(22, 4).Clear();
        uint crc = 0;
        foreach (var value in page)
        {
            crc ^= (uint)value << 24;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04c11db7 : crc << 1;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), crc);
    }

    private static void AssertValidChecksum(byte[] page)
    {
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22));
        var copy = page.ToArray();
        SetChecksum(copy);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(copy.AsSpan(22)));
    }

    private byte[]? DecodeFfmpeg(string directory, string name, byte[] ogg, bool requireSuccess = true)
    {
        var input = Path.Combine(directory, name + ".ogg");
        var pcm = Path.Combine(directory, name + ".pcm");
        File.WriteAllBytes(input, ogg);
        var start = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "-v", "error", "-y", "-i", input, "-f", "s16le", "-ac", "1", "-ar", "16000", pcm })
            start.ArgumentList.Add(argument);
        Process process;
        try { process = Process.Start(start)!; }
        catch (System.ComponentModel.Win32Exception)
        {
            output.WriteLine("FFmpeg unavailable; PCM integration check was not run.");
            return null;
        }
        using (process)
        {
            var error = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(15000), "FFmpeg timed out.");
            if (requireSuccess) Assert.True(process.ExitCode == 0, error);
            return File.Exists(pcm) ? File.ReadAllBytes(pcm) : [];
        }
    }
}
