using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Jibo.Cloud.Api.Hosting;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Runtime.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class BeefyErTests
{
    private static BeefyErOptions Options() => new() { Enabled = true, SharedSecret = new string('s', 43) };

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RemoteLowMemoryModeDisablesDuplicateModelsButRetainsLocalSherpa(bool lowMemory, bool correctionEnabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenJibo:Stt:BeefyEr:Enabled"] = "true",
            ["OpenJibo:Stt:BeefyEr:SharedSecret"] = new string('s', 43),
            ["OpenJibo:Stt:BeefyEr:LowMemoryMode"] = lowMemory.ToString()
        }).Build();
        var services = new ServiceCollection();
        services.AddOpenJiboCloud(configuration);
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<BufferedAudioSttOptions>();
        Assert.True(options.EnableStreamingSherpa);
        Assert.Equal(lowMemory, options.DisableWhisperForRemoteMode);
        Assert.Equal(correctionEnabled, provider.GetRequiredService<AsrCorrectionOptions>().Enabled);
    }

    [Fact]
    public async Task RemoteLowMemoryStartupNeverLoadsLocalRecognizer()
    {
        var remote = Options();
        await new SherpaRecognizerWarmupHostedService(new BufferedAudioSttOptions { EnableStreamingSherpa = true },
            null!, NullLogger<SherpaRecognizerWarmupHostedService>.Instance, remote).StartAsync(CancellationToken.None);
    }

    [Fact]
    public void ExplicitNeuralCorrectionOptInStillWorksWithLowMemoryRemoteMode()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenJibo:Stt:BeefyEr:Enabled"] = "true",
            ["OpenJibo:Stt:BeefyEr:SharedSecret"] = new string('s', 43),
            ["OpenJibo:Stt:Correction:Enabled"] = "true"
        }).Build();
        var services = new ServiceCollection();
        services.AddOpenJiboCloud(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<AsrCorrectionOptions>().Enabled);
        Assert.True(provider.GetRequiredService<BufferedAudioSttOptions>().DisableWhisperForRemoteMode);
    }

    [Fact]
    public void LowMemoryModeCannotBeOverriddenByLegacyWhisperEnvironmentAlias()
    {
        var source = new BufferedAudioSttOptions
        {
            DisableWhisperForRemoteMode = true, EnableWhisperServer = true,
            EnableLocalWhisperCpp = true, AutoStartWhisperServer = true
        };
        var resolved = BufferedAudioSttPathResolver.Resolve(source, key => "true", _ => true, null,
            Jibo.Cloud.Infrastructure.Platform.OperatingSystemPlatformResolver.Resolve());
        Assert.False(resolved.EnableWhisperServer);
        Assert.False(resolved.EnableLocalWhisperCpp);
        Assert.False(resolved.AutoStartWhisperServer);
    }

    [Fact]
    public void AuthenticationRequiresEnabledWorkerAndCorrectSeparateSecret()
    {
        var options = Options();
        options.Validate();
        Assert.False(options.Authenticate(null));
        Assert.False(options.Authenticate("Bearer incorrect"));
        Assert.False(options.Authenticate("bearer " + options.SharedSecret));
        Assert.True(options.Authenticate("Bearer " + options.SharedSecret));
        options.Enabled = false;
        Assert.False(options.Authenticate("Bearer " + options.SharedSecret));
        Assert.Throws<ArgumentException>(() => new BeefyErOptions { Enabled = true, SharedSecret = "short" }.Validate());
    }

    [Theory]
    [InlineData(false, false, 404)]
    [InlineData(true, false, 426)]
    [InlineData(true, true, 401)]
    public async Task EndpointRejectsDisabledInsecureOrUnauthenticatedRequests(bool enabled, bool https, int status)
    {
        var options = Options();
        options.Enabled = enabled;
        var context = new DefaultHttpContext();
        context.Request.Scheme = https ? "https" : "http";
        var handler = new BeefyErWebSocketHandler(options, new BeefyErBroker(options, NullLogger<BeefyErBroker>.Instance));
        await handler.HandleAsync(context);
        Assert.Equal(status, context.Response.StatusCode);
    }

    [Fact]
    public async Task EndpointRejectsAuthenticatedNonWebSocketRequest()
    {
        var options = Options();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Headers.Authorization = "Bearer " + options.SharedSecret;
        await new BeefyErWebSocketHandler(options, new BeefyErBroker(options, NullLogger<BeefyErBroker>.Instance)).HandleAsync(context);
        Assert.Equal(400, context.Response.StatusCode);
    }

    [Fact]
    public async Task ReadyCapacityAndPcmFramingAreEnforced()
    {
        await using var worker = await Harness.Connect();
        Assert.Null(worker.Broker.TryCreate("robot", "turn", null));
        await worker.Ready();
        using var session = await worker.Create("robot", "turn");
        var start = await worker.Socket.ReadJson("start");
        Assert.Equal(session.Id, start.StreamId);
        Assert.Equal("robot", start.SessionId);
        Assert.Null(worker.Broker.TryCreate("other", "turn", null));
        Assert.True(session.SendPcm(new float[] { -1, 0, 0.5f, 1 }));
        var packet = await worker.Socket.ReadBinary();
        Assert.Equal(session.Id, Convert.ToHexString(packet.AsSpan(0, 16)).ToLowerInvariant());
        Assert.Equal(1, BinaryPrimitives.ReadInt64LittleEndian(packet.AsSpan(16)));
        Assert.Equal(short.MinValue, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(24)));
        Assert.Equal(16384, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(28)));
        Assert.Equal(short.MaxValue, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(30)));
    }

    [Fact]
    public async Task RobotHintsReachWorkerAndWorkerTuningIsVisibleInStatus()
    {
        await using var worker = await Harness.Connect();
        worker.Socket.Push(new BeefyErMessage
        {
            Type = "ready", Generation = worker.Generation, Model = "nemotron-test", Capacity = 1,
            Threads = 8, RightContext = 1
        });
        await Until(() => JsonSerializer.SerializeToElement(worker.Broker.Status).GetProperty("ready").GetBoolean());
        using var session = worker.Broker.TryCreate("robot", "turn", null,
            [" Samantha ", "samantha", "kitchen light", new string('x', 81)])!;
        var start = await worker.Socket.ReadJson("start");
        Assert.Equal(new[] { "Samantha", "kitchen light" }, start.Hints);
        var status = JsonSerializer.SerializeToElement(worker.Broker.Status);
        Assert.Equal(8, status.GetProperty("workerThreads").GetInt32());
        Assert.Equal(1, status.GetProperty("workerRightContext").GetInt32());
    }

    [Fact]
    public async Task IndependentVadEndpointClearsWhenSpeechResumes()
    {
        await using var worker = await Harness.Connect();
        worker.Socket.Push(new BeefyErMessage
        {
            Type = "ready", Generation = worker.Generation, Model = "vad-model", Capacity = 1,
            IndependentEndpointing = true
        });
        using var session = await worker.Create();
        Assert.False(session.AllowAudioGapEndpointing);
        worker.Result(session, "endpoint", "turn on the", sequence: 0);
        await Until(() => session.IsEndpoint);
        worker.Result(session, "speech_started", "turn on the", sequence: 0);
        await Until(() => !session.IsEndpoint);
        Assert.Equal("turn on the", session.PartialText);
    }

    [Fact]
    public async Task PartialEndpointsAndFinalDrainReuseTheLiveStream()
    {
        await using var worker = await Harness.Connect();
        await worker.Ready();
        using var session = await worker.Create();
        await worker.Socket.ReadJson("start");
        session.SendPcm(new float[1600]);
        await worker.Socket.ReadBinary();
        worker.Result(session, "partial", "set a timer for");
        await Until(() => session.PartialText == "set a timer for");
        Assert.False(session.IsEndpoint);
        worker.Result(session, "endpoint", "set a timer for");
        await Until(() => session.IsEndpoint);
        session.ResetEndpoint();
        Assert.Equal("set a timer for", session.PartialText);
        Assert.False(session.IsEndpoint);
        session.SendPcm(new float[1600]);
        await worker.Socket.ReadBinary();
        worker.Result(session, "partial", "set a timer for five minutes", sequence: 2);
        var final = session.FinishAsync(CancellationToken.None);
        var finish = await worker.Socket.ReadJson("finish");
        Assert.Equal(2, finish.Sequence);
        worker.Result(session, "final", "set a timer for five minutes", sequence: 2);
        var result = await final;
        Assert.Equal("set a timer for five minutes", result.Text);
        Assert.Equal("beefyer-nemotron-streaming", result.Provider);
        Assert.Same(result, await session.FinishAsync(CancellationToken.None));
    }

    [Fact]
    public async Task InvalidIdentityOrOutOfOrderResultsAbandonRemoteTurn()
    {
        await using var worker = await Harness.Connect();
        await worker.Ready();
        using var session = await worker.Create();
        await worker.Socket.ReadJson("start");
        session.SendPcm(new float[1600]);
        await worker.Socket.ReadBinary();
        worker.Result(session, "partial", "wrong robot", sessionId: "other");
        await Until(() => session.Failed);
        worker.Result(session, "partial", "late result");
        Assert.Equal("", session.PartialText);
        await Assert.ThrowsAsync<IOException>(() => session.FinishAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TwoRobotSessionsNeverShareTranscripts()
    {
        var options = Options();
        options.MaxConcurrentTurns = 2;
        await using var worker = await Harness.Connect(options);
        await worker.Ready(2);
        using var first = await worker.Create("robot1", "turn1");
        using var second = await worker.Create("robot2", "turn2");
        await worker.Socket.ReadJson("start");
        await worker.Socket.ReadJson("start");
        worker.Result(first, "partial", "hello", sequence: 0, sessionId: "robot1", turnId: "turn1");
        worker.Result(second, "partial", "weather", sequence: 0, sessionId: "robot2", turnId: "turn2");
        await Until(() => first.PartialText == "hello" && second.PartialText == "weather");
    }

    [Fact]
    public async Task FragmentedReadinessIsAcceptedAndStaleGenerationClosesConnection()
    {
        await using var worker = await Harness.Connect();
        worker.Socket.Push(new BeefyErMessage { Type = "ready", Generation = worker.Generation, Capacity = 1, Model = "test" }, fragmented: true);
        using var session = await worker.Create();
        worker.Socket.Push(new BeefyErMessage { Type = "pong", Generation = "old-generation" });
        await Until(() => session.Failed);
        Assert.Null(worker.Broker.TryCreate("robot", "next-turn", null));
    }

    [Fact]
    public async Task DisconnectRecoversLocalEndpointingAndBufferedTranscription()
    {
        await using var worker = await Harness.Connect();
        await worker.Ready();
        var remote = await worker.Create();
        var localSession = new FakeLocalSession();
        var localFactory = new FakeFactory(localSession);
        using var failover = new BeefyErSessionFactory.FailoverSession(remote, localFactory, null, worker.Broker);
        var frames = new byte[][] { [] };
        failover.AcceptFrames(frames);
        worker.Socket.Abort();
        await Until(() => remote.Failed);
        Assert.Equal("local partial", failover.PartialText);
        Assert.True(failover.IsEndpoint);
        Assert.True(failover.IsAvailable);
        Assert.Same(frames[0], localSession.Frames![0]);
        var localStrategy = new FakeStrategy();
        var strategy = new BeefyErSttStrategy(Options(), localFactory, [localStrategy]);
        var result = await strategy.TranscribeAsync(new TurnContext
        {
            Attributes = new Dictionary<string, object?>
            {
                ["bufferedAudioFrames"] = frames,
                [BeefyErSttStrategy.SessionAttribute] = failover
            }
        });
        Assert.Equal("local final", result.Text);
        Assert.Same(frames, localStrategy.Frames);
        Assert.Equal(1, localFactory.Creations);
    }

    [Fact]
    public async Task FinalTimeoutFallsBackAndLateFinalIsIgnored()
    {
        var options = Options();
        options.FinalTimeout = TimeSpan.FromMilliseconds(30);
        await using var worker = await Harness.Connect(options);
        await worker.Ready();
        var remote = await worker.Create();
        await worker.Socket.ReadJson("start");
        using var failover = new BeefyErSessionFactory.FailoverSession(remote, new FakeFactory(null), null, worker.Broker);
        var local = new FakeStrategy();
        var strategy = new BeefyErSttStrategy(options, new FakeFactory(null), [local]);
        var turn = new TurnContext { Attributes = new Dictionary<string, object?>
        {
            ["bufferedAudioFrames"] = new byte[][] { [] }, [BeefyErSttStrategy.SessionAttribute] = failover
        }};
        var result = await strategy.TranscribeAsync(turn);
        Assert.Equal("local final", result.Text);
        worker.Result(remote, "final", "stale remote", sequence: 0);
        Assert.Equal("", remote.PartialText);
        Assert.False(failover.IsAvailable);
        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public async Task CallerCancellationNeverStartsLocalTranscription()
    {
        await using var worker = await Harness.Connect();
        await worker.Ready();
        var remote = await worker.Create();
        using var failover = new BeefyErSessionFactory.FailoverSession(remote, new FakeFactory(null), null, worker.Broker);
        var local = new FakeStrategy();
        var strategy = new BeefyErSttStrategy(Options(), new FakeFactory(null), [local]);
        var turn = new TurnContext { Attributes = new Dictionary<string, object?>
        {
            ["bufferedAudioFrames"] = new byte[][] { [] }, [BeefyErSttStrategy.SessionAttribute] = failover
        }};
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => strategy.TranscribeAsync(turn, stop.Token));
        Assert.Equal(0, local.Calls);
    }

    [Fact]
    public async Task BoundedQueueOverflowAbandonsAllRemoteWork()
    {
        var options = Options();
        options.SendQueueCapacity = 4;
        var socket = new FakeSocket();
        var broker = new BeefyErBroker(options, NullLogger<BeefyErBroker>.Instance);
        using var connection = new BeefyErBroker.Connection(socket, options) { Ready = true };
        using var session = new BeefyErSession(broker, connection, options, "robot", "turn");
        connection.Sessions[session.Id] = session;
        for (var i = 0; i < 4; i++) Assert.True(session.SendPcm(new float[1600]));
        Assert.False(session.SendPcm(new float[1600]));
        await Until(() => session.Failed);
        Assert.Equal(WebSocketState.Aborted, socket.State);
    }

    [Fact]
    public async Task MissingHeartbeatAbandonsRemoteTurns()
    {
        var options = Options();
        options.HeartbeatTimeout = TimeSpan.FromMilliseconds(20);
        await using var worker = await Harness.Connect(options);
        await worker.Ready();
        using var session = await worker.Create();
        await Until(() => session.Failed);
    }

    [Fact]
    public async Task ReconnectCreatesANewGenerationAndNeverResumesOldTurns()
    {
        await using var first = await Harness.Connect();
        await first.Ready();
        using var old = await first.Create();
        first.Socket.Abort();
        await Until(() => old.Failed);
        await using var second = await Harness.Connect(broker: first.Broker);
        Assert.NotEqual(first.Generation, second.Generation);
        await second.Ready();
        using var current = await second.Create();
        Assert.NotEqual(old.Id, current.Id);
        second.Socket.Push(new BeefyErMessage
        {
            Type = "partial", Generation = second.Generation, StreamId = old.Id,
            SessionId = "robot", TurnId = "turn", Sequence = 0, Text = "stale old transcript"
        });
        second.Result(current, "partial", "new transcript", sequence: 0);
        await Until(() => current.PartialText == "new transcript");
        Assert.Equal("", old.PartialText);
    }

    [Fact]
    public async Task SilenceProgressDoesNotClearAnAcousticEndpoint()
    {
        await using var worker = await Harness.Connect();
        await worker.Ready();
        using var session = await worker.Create();
        worker.Result(session, "endpoint", "what time is it", sequence: 0);
        await Until(() => session.IsEndpoint);
        worker.Result(session, "progress", "", sequence: 0);
        Assert.True(session.IsEndpoint);
        Assert.Equal("what time is it", session.PartialText);
    }

    [Fact]
    public async Task RegressingSequenceAbandonsTurn()
    {
        await using var worker = await Harness.Connect();
        await worker.Ready();
        using var session = await worker.Create();
        session.SendPcm(new float[1600]);
        worker.Result(session, "partial", "current", sequence: 1);
        await Until(() => session.PartialText == "current");
        worker.Result(session, "partial", "old", sequence: 0);
        await Until(() => session.Failed);
        Assert.Equal("current", session.PartialText);
    }

    [Fact]
    public async Task TurnThatStartedLocallyNeverAcquiresARemoteStreamAtFinalization()
    {
        await using var worker = await Harness.Connect();
        var localFactory = new FakeFactory(new FakeLocalSession());
        using var session = new BeefyErSessionFactory.FailoverSession(null, localFactory, null, worker.Broker);
        session.AcceptFrames(new byte[][] { [] });
        await worker.Ready(); // A worker becomes ready in the middle of the local turn.
        var acquisitionFactory = new FakeFactory(null);
        var local = new FakeStrategy();
        var strategy = new BeefyErSttStrategy(Options(), acquisitionFactory, [local]);
        var result = await strategy.TranscribeAsync(new TurnContext
        {
            Attributes = new Dictionary<string, object?>
            {
                ["bufferedAudioFrames"] = new byte[][] { [] }, [BeefyErSttStrategy.SessionAttribute] = session
            }
        });
        Assert.Equal("local final", result.Text);
        Assert.Equal(0, acquisitionFactory.Creations);
        Assert.Equal(1, localFactory.Creations);
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, stop.Token);
    }

    private sealed class FakeFactory(IIncrementalSttSession? session) : IContextualIncrementalSttSessionFactory
    {
        public int Creations;
        public bool IsEnabled => true;
        public IIncrementalSttSession? TryCreate(TimeSpan? maxSpeechTimeout) { Creations++; return session; }
        public IIncrementalSttSession? TryCreate(string sessionId, string turnId, TimeSpan? maxSpeechTimeout) => TryCreate(maxSpeechTimeout);
    }

    private sealed class FakeLocalSession : IIncrementalSttSession
    {
        public IReadOnlyList<byte[]>? Frames;
        public string PartialText => "local partial";
        public bool IsEndpoint => true;
        public void AcceptFrames(IReadOnlyList<byte[]> frames) => Frames = frames;
        public void ResetEndpoint() { }
        public void Dispose() { }
    }

    private sealed class FakeStrategy : ISttStrategy
    {
        public int Calls;
        public object? Frames;
        public string Name => "local";
        public bool CanHandle(TurnContext turn) => true;
        public Task<SttResult> TranscribeAsync(TurnContext turn, CancellationToken cancellationToken = default)
        {
            Calls++;
            Frames = turn.Attributes["bufferedAudioFrames"];
            return Task.FromResult(new SttResult { Text = "local final", Provider = "local" });
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly FakeSocket Socket = new();
        public readonly BeefyErBroker Broker;
        private Task _run = Task.CompletedTask;
        public string Generation = "";
        private Harness(BeefyErOptions options, BeefyErBroker? broker) => Broker = broker ?? new BeefyErBroker(options, NullLogger<BeefyErBroker>.Instance);
        public static async Task<Harness> Connect(BeefyErOptions? options = null, BeefyErBroker? broker = null)
        {
            var harness = new Harness(options ?? Options(), broker);
            harness._run = harness.Broker.RunAsync(harness.Socket, CancellationToken.None);
            harness.Generation = (await harness.Socket.ReadJson("hello")).Generation;
            return harness;
        }
        public async Task Ready(int capacity = 1)
        {
            Socket.Push(new BeefyErMessage { Type = "ready", Generation = Generation, Model = "nemotron-test", Capacity = capacity });
            await Until(() => JsonSerializer.SerializeToElement(Broker.Status).GetProperty("ready").GetBoolean());
        }
        public async Task<BeefyErSession> Create(string sessionId = "robot", string turnId = "turn")
        {
            BeefyErSession? session = null;
            await Until(() => (session = Broker.TryCreate(sessionId, turnId, null)) is not null);
            return session!;
        }
        public void Result(BeefyErSession session, string type, string text, long sequence = 1,
            string sessionId = "robot", string turnId = "turn") => Socket.Push(new BeefyErMessage
            {
                Type = type, Generation = Generation, StreamId = session.Id, SessionId = sessionId,
                TurnId = turnId, Sequence = sequence, Text = text
            });
        public async ValueTask DisposeAsync()
        {
            Socket.Abort();
            await _run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly Channel<(byte[] Bytes, bool End)> _input = Channel.CreateUnbounded<(byte[], bool)>();
        private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> _output = Channel.CreateUnbounded<(byte[], WebSocketMessageType)>();
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() { _state = WebSocketState.Aborted; _input.Writer.TryComplete(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
            => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                var item = await _input.Reader.ReadAsync(cancellationToken);
                item.Bytes.CopyTo(buffer.AsSpan());
                return new WebSocketReceiveResult(item.Bytes.Length, WebSocketMessageType.Text, item.End);
            }
            catch (ChannelClosedException) { throw new WebSocketException("Closed test socket."); }
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        { _output.Writer.TryWrite((buffer.ToArray(), messageType)); return Task.CompletedTask; }
        public void Push(BeefyErMessage message, bool fragmented = false)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BeefyErBroker.JsonOptions);
            if (fragmented)
            {
                _input.Writer.TryWrite((bytes[..10], false));
                _input.Writer.TryWrite((bytes[10..], true));
            }
            else _input.Writer.TryWrite((bytes, true));
        }
        public async Task<BeefyErMessage> ReadJson(string type)
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var item = await _output.Reader.ReadAsync(stop.Token);
                if (item.Type != WebSocketMessageType.Text) continue;
                var message = JsonSerializer.Deserialize<BeefyErMessage>(item.Bytes, BeefyErBroker.JsonOptions)!;
                if (message.Type == type) return message;
            }
        }
        public async Task<byte[]> ReadBinary()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var item = await _output.Reader.ReadAsync(stop.Token);
                if (item.Type == WebSocketMessageType.Binary) return item.Bytes;
            }
        }
    }
}
