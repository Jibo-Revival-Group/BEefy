using Concentus;
using Concentus.Enums;
using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class OggOpusPcmDecoderTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void IncrementalDecode_MatchesBufferedDecode_WithoutRepeatingAudio()
    {
        var pages = EncodeTone(1000);
        var expected = OggOpusPcmDecoder.DecodeTo16kMono(pages);
        using var decoder = new OggOpusIncrementalPcmDecoder();
        var actual = new List<float>();
        for (var count = 1; count <= pages.Count; count++)
        {
            var received = pages.Take(count).ToArray();
            actual.AddRange(decoder.DecodeNewTo16kMono(received));
            Assert.Empty(decoder.DecodeNewTo16kMono(received));
        }
        Assert.Equal(pages.Count * 320, expected.Length);
        Assert.Equal(expected, actual.ToArray());
        Assert.All(expected, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void Decode_SuppressesHighFrequencyAliasing_AndPreservesSpeechBand()
    {
        var speech = OggOpusPcmDecoder.DecodeTo16kMono(EncodeTone(1000));
        var highPages = EncodeTone(11000);
        var filtered = OggOpusPcmDecoder.DecodeTo16kMono(highPages);
        var legacy = DecodeByDroppingSamples(highPages);
        Assert.True(Rms(speech) > 0.1, "Speech-band signal must be retained.");
        Assert.True(Rms(filtered) < Rms(legacy) * 0.25,
            $"Aliased noise should be suppressed: direct={Rms(filtered)}, legacy={Rms(legacy)}");
    }

    [Fact]
    public void Decode_EmptyOrInvalidFrames_ReturnsNoAudio()
    {
        Assert.Empty(OggOpusPcmDecoder.DecodeTo16kMono([]));
        Assert.Empty(OggOpusPcmDecoder.DecodeTo16kMono([new byte[] { 1, 2, 3 }]));
        using var decoder = new OggOpusIncrementalPcmDecoder();
        Assert.Empty(decoder.DecodeNewTo16kMono([]));
        Assert.Empty(decoder.DecodeNewTo16kMono([new byte[] { 1, 2, 3 }]));
    }

    [Fact]
    public void Decode_ReportsProcessingTimeAgainstLegacyDownsampling()
    {
        var pages = EncodeTone(1000);
        for (var i = 0; i < 5; i++)
        {
            OggOpusPcmDecoder.DecodeTo16kMono(pages);
            DecodeByDroppingSamples(pages);
        }
        var direct = new List<double>();
        var legacy = new List<double>();
        for (var i = 0; i < 30; i++)
        {
            // Alternate order to reduce warm-up and scheduling bias.
            if (i % 2 == 0)
            {
                Measure(() => OggOpusPcmDecoder.DecodeTo16kMono(pages), direct);
                Measure(() => DecodeByDroppingSamples(pages), legacy);
            }
            else
            {
                Measure(() => DecodeByDroppingSamples(pages), legacy);
                Measure(() => OggOpusPcmDecoder.DecodeTo16kMono(pages), direct);
            }
        }
        direct.Sort();
        legacy.Sort();
        output.WriteLine($"600 ms audio: direct median={direct[15]:F3} ms p95={direct[28]:F3} ms; legacy median={legacy[15]:F3} ms p95={legacy[28]:F3} ms");
        // Timing is reported, not asserted: shared CI hosts have variable load.
    }

    private static void Measure(Func<float[]> decode, List<double> timings)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var samples = decode();
        timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        Assert.NotEmpty(samples);
    }

    private static List<byte[]> EncodeTone(int frequency)
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, OpusApplication.OPUS_APPLICATION_AUDIO);
        using var lifetime = encoder as IDisposable;
        encoder.Bitrate = 128000;
        var pages = new List<byte[]>();
        for (var frame = 0; frame < 30; frame++)
        {
            var pcm = Enumerable.Range(0, 960)
                .Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * frequency * (frame * 960 + i) / 48000)))
                .ToArray();
            var packet = new byte[4000];
            var length = encoder.Encode(pcm, pcm.Length, packet, packet.Length);
            var segments = length / 255 + 1;
            var page = new byte[27 + segments + length];
            "OggS"u8.CopyTo(page);
            page[26] = (byte)segments;
            for (var i = 0; i < segments; i++)
                page[27 + i] = (byte)Math.Min(255, length - i * 255);
            packet.AsSpan(0, length).CopyTo(page.AsSpan(27 + segments));
            pages.Add(page);
        }
        return pages;
    }

    private static float[] DecodeByDroppingSamples(IReadOnlyList<byte[]> pages)
    {
        var decoder = OpusCodecFactory.CreateDecoder(48000, 1);
        using var lifetime = decoder as IDisposable;
        var output = new List<float>();
        var scratch = new float[5760];
        foreach (var packet in Jibo.Cloud.Application.Audio.OggOpusAudioNormalizer.EnumerateOpusPacketPayloads(pages))
        {
            var count = decoder.Decode(packet, scratch, scratch.Length, false);
            for (var i = 0; i < count; i += 3) output.Add(scratch[i]);
        }
        return output.ToArray();
    }

    private static double Rms(float[] samples) =>
        Math.Sqrt(samples.Skip(1600).Select(x => (double)x * x).Average());
}
