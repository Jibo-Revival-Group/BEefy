using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class SherpaAccuracyTests
{
    [Fact]
    public void PausedUtterance_PreservesCommittedWords_AndReplacesPartialHypotheses()
    {
        var transcript = new StreamingTranscriptAccumulator();
        transcript.Update("turn on");
        transcript.Update("turn on the");
        transcript.Commit();
        transcript.Commit(); // repeated reset must not duplicate words
        transcript.Update("living");
        Assert.Equal("turn on the living", transcript.Text);
        transcript.Update("living room lights");
        Assert.Equal("turn on the living room lights", transcript.Text);
        transcript.Commit();
        transcript.Update("");
        Assert.Equal("turn on the living room lights", transcript.Text);
    }

    [Fact]
    public async Task ConcurrentSessions_KeepIndependentTranscripts_AndSerializeRecognizerAccess()
    {
        using var provider = Provider(new BufferedAudioSttOptions());
        var active = 0;
        var tasks = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            var transcript = new StreamingTranscriptAccumulator();
            for (var segment = 0; segment < 10; segment++)
            {
                provider.WithRecognizerLock(() =>
                {
                    Assert.Equal(1, Interlocked.Increment(ref active));
                    transcript.Update($"{index}:{segment}");
                    transcript.Commit();
                    Interlocked.Decrement(ref active);
                });
            }
            return transcript.Text;
        }));
        var results = await Task.WhenAll(tasks);
        for (var index = 0; index < results.Length; index++)
            Assert.Equal(string.Join(" ", Enumerable.Range(0, 10).Select(segment => $"{index}:{segment}")), results[index]);
    }

    [Theory]
    [InlineData("greedy_search", 4)]
    [InlineData("modified_beam_search", 2)]
    [InlineData("modified_beam_search", 4)]
    public void ResolvedConfig_UsesSherpaControls_WithoutWhisperThreadsOrHotwords(string method, int paths)
    {
        using var provider = Provider(new BufferedAudioSttOptions
        {
            SherpaDecodingMethod = method, SherpaMaxActivePaths = paths, SherpaThreads = 2, WhisperThreads = 8
        });
        var config = provider.BuildConfig(new SherpaModelLocator.ModelPaths("model", "encoder", "decoder", "joiner", "tokens", "bad-hotwords"), 20);
        Assert.Equal(method, config.DecodingMethod);
        Assert.Equal(paths, config.MaxActivePaths);
        Assert.Equal(2, config.ModelConfig.NumThreads);
        Assert.True(string.IsNullOrEmpty(config.HotwordsFile));
        Assert.Equal(0.4f, config.Rule1MinTrailingSilence);
        Assert.Equal(0.6f, config.Rule2MinTrailingSilence);
    }

    [Theory]
    [InlineData("invalid", 4, 0)]
    [InlineData("modified_beam_search", 0, 0)]
    [InlineData("modified_beam_search", 17, 0)]
    [InlineData("greedy_search", 4, -1)]
    public void InvalidDecoderSettings_AreRejected(string method, int paths, int threads)
    {
        using var provider = Provider(new BufferedAudioSttOptions
        { SherpaDecodingMethod = method, SherpaMaxActivePaths = paths, SherpaThreads = threads });
        Assert.Throws<ArgumentException>(() => provider.BuildConfig(
            new SherpaModelLocator.ModelPaths("model", "encoder", "decoder", "joiner", "tokens", null), 20));
    }

    private static SherpaOnlineRecognizerProvider Provider(BufferedAudioSttOptions options) => new(options,
        new ListenEndpointingOptions(), new SherpaModelLocator(NullLogger<SherpaModelLocator>.Instance));
}
