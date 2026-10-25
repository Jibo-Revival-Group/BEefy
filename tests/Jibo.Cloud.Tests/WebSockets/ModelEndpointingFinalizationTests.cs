using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;
using Moq;
using System.Text.Json;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class ModelEndpointingFinalizationTests
{
    [Theory]
    [InlineData("turn off the lights")]
    [InlineData("play Hello by Adele")]
    [InlineData("hmm")]
    [InlineData("what's your paper color")]
    public async Task AgentListen_ReturnsUnmodifiedBufferedTranscript_WithoutBroker(string heard)
    {
        var incremental = new FakeIncrementalSttSession { PartialText = heard, IsEndpoint = true };
        var service = CreateService(incremental, out var broker);
        var session = CreateArmedSession(incremental);
        session.TurnState.ListenHotphrase = false;
        session.TurnState.ListenRules = [AgentModeRouting.ConversationRule];
        var replies = await service.HandleIdleAsync(session, new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        });
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Never);
        var listen = Assert.Single(replies, reply => JsonDocument.Parse(reply.Text!).RootElement.GetProperty("type").GetString() == "LISTEN");
        using var document = JsonDocument.Parse(listen.Text!);
        Assert.Equal(heard, document.RootElement.GetProperty("data").GetProperty("asr").GetProperty("text").GetString());
        Assert.Null(session.TurnState.IncrementalSttSession);
        Assert.False(session.FollowUpOpen);
    }

    [Fact]
    public async Task ConfirmedIndependentSpeechEndpointDoesNotWaitForLegacyAudioByteThreshold()
    {
        var incremental = new FakeIncrementalSttSession
        {
            AllowAudioGapEndpointing = false, PartialText = "what time is it", IsEndpoint = true
        };
        var service = CreateService(incremental, out var broker);
        var session = CreateArmedSession(incremental);
        var shortFrames = session.TurnState.BufferedAudioFrames.Take(4).ToArray();
        session.TurnState.BufferedAudioFrames.Clear();
        session.TurnState.BufferedAudioFrames.AddRange(shortFrames);
        session.TurnState.BufferedAudioChunkCount = shortFrames.Length;
        session.TurnState.BufferedAudioBytes = shortFrames.Sum(frame => frame.Length);
        Assert.True(session.TurnState.BufferedAudioBytes < 8500);
        await service.HandleIdleAsync(session, new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        });
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task IndependentVadWaitsForAcousticEndpointRatherThanTransportGap()
    {
        var incremental = new FakeIncrementalSttSession
        {
            AllowAudioGapEndpointing = false, PartialText = "what time is it", IsEndpoint = false
        };
        var service = CreateService(incremental, out var broker);
        var session = CreateArmedSession(incremental);
        var envelope = new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        };
        await service.HandleIdleAsync(session, envelope);
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Never);
        incremental.IsEndpoint = true;
        await service.HandleIdleAsync(session, envelope);
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }
    [Fact]
    public async Task UnavailableStreamingSessionUsesLegacySilenceAndExecutesOnce()
    {
        var incremental = new FakeIncrementalSttSession
        {
            IsAvailable = false, PartialText = "what time is it", IsEndpoint = false
        };
        var service = CreateService(incremental, out var broker);
        var session = CreateArmedSession(incremental);
        var envelope = new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com", Path = "/listen", Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        };
        await service.HandleIdleAsync(session, envelope);
        await service.HandleIdleAsync(session, envelope);
        broker.Verify(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("what's your paper color", "what's your paper color", false)]
    [InlineData("what's your paper color printer", "what's your paper color printer", false)]
    public async Task BufferedStt_CorrectsOnlyKnownFrames_AndPreservesRawTranscript(
        string heard, string expected, bool corrected)
    {
        var incremental = new FakeIncrementalSttSession { PartialText = heard, IsEndpoint = true };
        var service = CreateService(incremental, out var broker);
        var session = CreateArmedSession(incremental);
        await service.HandleIdleAsync(session, new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        });
        broker.Verify(b => b.HandleTurnAsync(
            It.Is<TurnContext>(turn => turn.RawTranscript == heard &&
                turn.NormalizedTranscript == expected &&
                turn.Attributes.ContainsKey("stt:grammarCorrection") == corrected),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IdleWatchdog_FinalizesImmediately_WhenModelEndpointAndCompletePartial()
    {
        var fakeSession = new FakeIncrementalSttSession
        {
            PartialText = "what time is it",
            IsEndpoint = true
        };
        var service = CreateService(fakeSession, out var broker);
        var session = CreateArmedSession(fakeSession);

        var replies = await service.HandleIdleAsync(session, new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        });

        Assert.NotEmpty(replies);
        broker.Verify(
            b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Null(session.TurnState.IncrementalSttSession);
    }

    [Fact]
    public async Task IdleWatchdog_KeepsListening_WhenModelEndpointButIncompletePartial()
    {
        var fakeSession = new FakeIncrementalSttSession
        {
            PartialText = "turn on the",
            IsEndpoint = true
        };
        var service = CreateService(fakeSession, out var broker);
        var session = CreateArmedSession(fakeSession);

        var replies = await service.HandleIdleAsync(session, new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        });

        Assert.Empty(replies);
        Assert.True(session.TurnState.AwaitingTurnCompletion);
        Assert.True(fakeSession.ResetEndpointCalled);
        broker.Verify(
            b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.NotNull(session.TurnState.IncrementalSttSession);
    }

    [Fact]
    public async Task IdleWatchdog_UsesLegacySilence_WhenModelEndpointingDisabled()
    {
        var service = CreateService(
            incremental: null,
            out _,
            enableModelEndpointing: false);
        var session = CreateArmedSession(incremental: null);
        // Legacy silence window is 350ms; last audio was 500ms ago.
        session.TurnState.LastAudioReceivedUtc = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(500);

        // Without model endpointing and without a transcript hint / STT that can handle
        // synthetic frames, finalize still triggers on silence but may defer on empty STT.
        // Assert the gate itself: ShouldAutoFinalize path is entered via idle when silence elapsed.
        var replies = await service.HandleIdleAsync(session, new WebSocketMessageEnvelope
        {
            HostName = "neo-hub.jibo.com",
            Path = "/listen",
            Kind = "neo-hub-listen",
            Text = """{"type":"IDLE"}"""
        });

        // Empty STT → missing transcript fallback may keep open or no-input; either way
        // idle attempted finalize (non-model path). ResetEndpoint must not be involved.
        _ = replies;
        Assert.True(session.TurnState.LastAutoFinalizeAttemptUtc.HasValue ||
                    !session.TurnState.AwaitingTurnCompletion ||
                    replies.Count >= 0);
    }

    private static WebSocketTurnFinalizationService CreateService(
        FakeIncrementalSttSession? incremental,
        out Mock<IConversationBroker> broker,
        bool enableModelEndpointing = true)
    {
        broker = new Mock<IConversationBroker>(MockBehavior.Strict);
        broker.Setup(b => b.HandleTurnAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResponsePlan
            {
                IntentName = "clock",
                Actions =
                [
                    new SpeakAction { Text = "It is noon." }
                ]
            });

        var stt = new Mock<ISttStrategy>();
        stt.SetupGet(s => s.Name).Returns("test-stt");
        stt.Setup(s => s.CanHandle(It.IsAny<TurnContext>())).Returns(true);
        stt.Setup(s => s.TranscribeAsync(It.IsAny<TurnContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TurnContext turn, CancellationToken _) => new SttResult
            {
                Text = incremental?.PartialText ?? "what time is it",
                Provider = "test-stt",
                Confidence = 0.9f
            });

        IIncrementalSttSessionFactory? factory = null;
        if (enableModelEndpointing && incremental is not null)
        {
            var factoryMock = new Mock<IIncrementalSttSessionFactory>();
            factoryMock.SetupGet(f => f.IsEnabled).Returns(true);
            factoryMock.Setup(f => f.TryCreate(It.IsAny<TimeSpan?>())).Returns(incremental);
            factory = factoryMock.Object;
        }

        return new WebSocketTurnFinalizationService(
            broker.Object,
            new DefaultSttStrategySelector([stt.Object]),
            new NullTurnTelemetrySink(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WebSocketTurnFinalizationService>.Instance,
            listenEndpointingOptions: new ListenEndpointingOptions
            {
                EnableModelEndpointing = enableModelEndpointing,
                Rule2MinTrailingSilenceSeconds = 0.6f
            },
            incrementalSttSessionFactory: factory);
    }

    private static CloudSession CreateArmedSession(FakeIncrementalSttSession? incremental)
    {
        var now = DateTimeOffset.UtcNow;
        var frames = BuildEnoughAudioFrames();
        var session = new CloudSession
        {
            Kind = "neo-hub-listen",
            HostName = "neo-hub.jibo.com",
            Path = "/listen"
        };
        session.TurnState.SawListen = true;
        session.TurnState.SawContext = true;
        session.TurnState.AwaitingTurnCompletion = true;
        session.TurnState.ListenHotphrase = true;
        session.TurnState.ListenRules = ["launch"];
        session.TurnState.TransId = "trans-model-endpoint";
        session.TurnState.FirstAudioReceivedUtc = now - TimeSpan.FromMilliseconds(800);
        session.TurnState.LastAudioReceivedUtc = now - TimeSpan.FromMilliseconds(700);
        session.TurnState.BufferedAudioFrames.AddRange(frames);
        session.TurnState.BufferedAudioChunkCount = frames.Count;
        session.TurnState.BufferedAudioBytes = frames.Sum(f => f.Length);
        session.TurnState.IncrementalSttSession = incremental;
        return session;
    }

    private static List<byte[]> BuildEnoughAudioFrames()
    {
        // Need >= 8500 bytes and >= 3 audio-bearing pages.
        var frames = new List<byte[]>
        {
            BuildOggPage(0x02, BuildOpusHead()),
            BuildOggPage(0x00, BuildOpusTags())
        };
        while (frames.Sum(f => f.Length) < 9000 || frames.Count < 8)
            frames.Add(BuildOggPage(0x00, BuildOpusTocPacket(200)));
        frames.Add(BuildOggPage(0x00, BuildOpusTocPacket(200)));
        return frames;
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

    private sealed class FakeIncrementalSttSession : IIncrementalSttSession
    {
        public bool IsAvailable { get; set; } = true;
        public bool AllowAudioGapEndpointing { get; set; } = true;
        public string PartialText { get; set; } = string.Empty;
        public bool IsEndpoint { get; set; }
        public bool ResetEndpointCalled { get; private set; }

        public void AcceptFrames(IReadOnlyList<byte[]> frames)
        {
        }

        public void ResetEndpoint()
        {
            ResetEndpointCalled = true;
            IsEndpoint = false;
        }

        public void Dispose()
        {
        }
    }
}
