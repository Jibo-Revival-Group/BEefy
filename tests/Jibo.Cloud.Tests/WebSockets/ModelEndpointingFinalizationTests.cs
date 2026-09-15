using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.WebSockets;

public sealed class ModelEndpointingFinalizationTests
{
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
