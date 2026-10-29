using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Infrastructure.Audio;

/// <summary>One outbound worker connection, with bounded multiplexed audio and per-turn leases.</summary>
public sealed partial class BeefyErBroker(BeefyErOptions options, ILogger<BeefyErBroker> logger,
    ListenEndpointingOptions? listenOptions = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private Connection? _connection;
    private long _fallbacks;
    private long _completed;
    private double _lastFinalLatencyMs;
    private double _lastPartialLatencyMs;
    private double _lastBacklogSeconds;

    public object Status
    {
        get
        {
            lock (_gate) return new
            {
                enabled = options.Enabled, connected = _connection is not null,
                ready = _connection?.Ready ?? false, model = _connection?.Model,
                activeTurns = _connection?.Sessions.Count ?? 0, capacity = _connection?.Capacity ?? 0,
                fallbackCount = Interlocked.Read(ref _fallbacks), completedTurns = Interlocked.Read(ref _completed),
                lastFinalLatencyMs = _lastFinalLatencyMs, lastFirstPartialLatencyMs = _lastPartialLatencyMs,
                lastBacklogSeconds = _lastBacklogSeconds,
                workerThreads = _connection?.Threads, workerRightContext = _connection?.RightContext,
                independentEndpointing = _connection?.IndependentEndpointing ?? false,
                lowMemoryMode = options.LowMemoryMode
            };
        }
    }

    internal void RecordFallback(string reason)
    {
        Interlocked.Increment(ref _fallbacks);
        logger.LogInformation("BEefyER local fallback reason={Reason}", reason);
    }

    internal void RecordFinal(double latencyMs)
    {
        Interlocked.Increment(ref _completed);
        lock (_gate) _lastFinalLatencyMs = latencyMs;
    }

    internal void RecordProgress(double? firstPartialLatencyMs, double backlogSeconds)
    {
        lock (_gate)
        {
            if (firstPartialLatencyMs.HasValue) _lastPartialLatencyMs = firstPartialLatencyMs.Value;
            _lastBacklogSeconds = backlogSeconds;
        }
    }

    internal BeefyErSession? TryCreate(string sessionId, string turnId, TimeSpan? maxSpeechTimeout,
        IReadOnlyList<string>? hints = null)
    {
        ReleaseStaleSessions();
        BeefyErSession session;
        Connection connection;
        var speechLimit = maxSpeechTimeout ?? TimeSpan.FromSeconds(20);
        lock (_gate)
        {
            if (_connection is not { Ready: true } current || current.Sessions.Count >= current.Capacity) return null;
            connection = current;
            session = new BeefyErSession(this, connection, options, sessionId, turnId)
            {
                Lifetime = speechLimit + options.FinalTimeout + options.StaleTurnGrace
            };
            connection.Sessions[session.Id] = session;
        }
        if (session.Send("start", endpointMs: connection.EndpointMs,
                maxSpeechSeconds: maxSpeechTimeout?.TotalSeconds ?? 20,
                hints: (hints ?? []).Where(hint => !string.IsNullOrWhiteSpace(hint)).Select(hint => hint.Trim())
                    .Where(hint => hint.Length <= 80).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray())) return session;
        session.Dispose();
        return null;
    }

    // A turn that is never finished or disposed would hold its worker slot until the worker reconnects;
    // with one slot, every later turn on the server fell back to local ASR for hours. Reclaim any
    // session past its lifetime. Runs outside _gate: Dispose takes the session lock, and a session
    // takes _gate while holding its own lock.
    private void ReleaseStaleSessions()
    {
        var current = _connection;
        if (current is null) return;
        foreach (var stale in current.Sessions.Values.Where(session => session.IsStale).ToArray())
        {
            logger.LogWarning("BEefyER reclaimed a stale turn slot stream={StreamId}", stale.Id);
            stale.Fail("stale_turn");
            stale.Dispose();
        }
    }

    public async Task RunAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        using var connection = new Connection(socket, options)
        {
            EndpointMs = Math.Max(200, (int)Math.Round((listenOptions?.Rule2MinTrailingSilenceSeconds ?? 0.6f) * 1000))
        };
        lock (_gate)
        {
            if (_connection is not null)
            {
                socket.Abort();
                return;
            }
            _connection = connection;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sender = SendLoopAsync(connection, stop.Token);
        var heartbeat = HeartbeatAsync(connection, stop.Token);
        try
        {
            connection.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new BeefyErMessage
            {
                Type = "hello", Generation = connection.Generation
            }, JsonOptions), WebSocketMessageType.Text);
            await ReceiveLoopAsync(connection, stop.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException or InvalidDataException)
        {
            logger.LogInformation("BEefyER connection ended category={Category}", ex.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                connection.Ready = false;
                if (ReferenceEquals(_connection, connection)) _connection = null;
            }
            connection.Fail("disconnected");
            stop.Cancel();
            socket.Abort();
            try { await Task.WhenAll(sender, heartbeat); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        }
    }

    private static async Task SendLoopAsync(Connection connection, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (connection.Outgoing.Reader.TryRead(out var item) || connection.ChatOutgoing.Reader.TryRead(out item))
                {
                    await connection.Socket.SendAsync(item.Bytes.AsMemory(), item.Type, true, token);
                    continue;
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                var asr = connection.Outgoing.Reader.WaitToReadAsync(wait.Token).AsTask();
                var chat = connection.ChatOutgoing.Reader.WaitToReadAsync(wait.Token).AsTask();
                await Task.WhenAny(asr, chat);
                wait.Cancel();
                token.ThrowIfCancellationRequested();
                if (connection.Outgoing.Reader.Completion.IsCompleted && connection.ChatOutgoing.Reader.Completion.IsCompleted) break;
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
            connection.Fail("send_failure");
            connection.Socket.Abort();
        }
    }

    private async Task HeartbeatAsync(Connection connection, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token))
        {
            if (Stopwatch.GetElapsedTime(Interlocked.Read(ref connection.LastPong)) > options.HeartbeatTimeout)
            {
                connection.Fail("heartbeat_timeout");
                connection.Socket.Abort();
                return;
            }
            connection.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new BeefyErMessage
            {
                Type = "ping", Generation = connection.Generation
            }, JsonOptions), WebSocketMessageType.Text);
        }
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken token)
    {
        var buffer = new byte[65536];
        while (connection.Socket.State == WebSocketState.Open)
        {
            var length = 0;
            ValueWebSocketReceiveResult result;
            do
            {
                if (length == buffer.Length) throw new InvalidDataException("Worker message too large.");
                result = await connection.Socket.ReceiveAsync(buffer.AsMemory(length), token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                if (result.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Expected worker JSON.");
                length += result.Count;
            } while (!result.EndOfMessage);
            var message = JsonSerializer.Deserialize<BeefyErMessage>(buffer.AsSpan(0, length), JsonOptions)
                ?? throw new InvalidDataException("Missing worker message.");
            HandleMessage(connection, message);
        }
    }

    internal void HandleMessage(Connection connection, BeefyErMessage message)
    {
        if (message.Version != 1 || message.Generation != connection.Generation)
            throw new InvalidDataException("Worker protocol or generation mismatch.");
        if (message.Type == "pong")
        {
            Interlocked.Exchange(ref connection.LastPong, Stopwatch.GetTimestamp());
            return;
        }
        if (message.Type == "ready")
        {
            lock (_gate)
            {
                if (connection.Ready || message.Capacity < 1 || string.IsNullOrWhiteSpace(message.Model))
                    throw new InvalidDataException("Invalid worker readiness.");
                connection.Model = message.Model;
                connection.Capacity = Math.Min(options.MaxConcurrentTurns, message.Capacity);
                connection.Threads = message.Threads;
                connection.RightContext = message.RightContext;
                connection.IndependentEndpointing = message.IndependentEndpointing;
                connection.ChatModel = message.ChatModel;
                connection.ChatCapacity = message.ChatModel == "gemma3:4b" && message.ChatCapacity == 1 ? 1 : 0;
                connection.Ready = true;
            }
            logger.LogInformation("BEefyER ready model={Model} capacity={Capacity}", connection.Model, connection.Capacity);
            return;
        }
        if (!connection.Ready) throw new InvalidDataException("Worker is not ready.");
        if (message.Type is "chat_result" or "chat_error" or "chat_cancelled")
        {
            lock (_gate)
            {
                if (connection.Chat is { } chat && message.RequestId == chat.Id
                    && (message.Type != "chat_cancelled" || chat.CancelSent))
                {
                    connection.Chat = null;
                    chat.Deadline?.Dispose();
                    if (message.Type == "chat_result" && !string.IsNullOrWhiteSpace(message.Text) && message.Text.Length <= 12000)
                        chat.Completion.TrySetResult(message.Text);
                    else chat.Completion.TrySetException(new FreeAgentRelayException("upstream", 502));
                }
            }
            return;
        }
        if (message.StreamId is not null && connection.Sessions.TryGetValue(message.StreamId, out var session))
            session.Receive(message);
        // Unknown streams are canceled/disposed turns; late results cannot resurrect them.
    }

    internal sealed class Connection : IDisposable
    {
        public readonly WebSocket Socket;
        public readonly string Generation = Guid.NewGuid().ToString("N");
        public readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> Outgoing;
        public readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> ChatOutgoing;
        public readonly ConcurrentDictionary<string, BeefyErSession> Sessions = new();
        public long LastPong = Stopwatch.GetTimestamp();
        public volatile bool Ready;
        public string? Model;
        public int Capacity;
        public string? ChatModel;
        public int ChatCapacity;
        public PendingChat? Chat;
        public int? Threads;
        public int? RightContext;
        public bool IndependentEndpointing;
        public int EndpointMs = 600;

        public Connection(WebSocket socket, BeefyErOptions options)
        {
            Socket = socket;
            // A prior late cancel may precede the current start and its cancel.
            ChatOutgoing = Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(3)
            {
                SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
            });
            Outgoing = Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(options.SendQueueCapacity)
            {
                SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
            });
        }

        public bool Enqueue(byte[] bytes, WebSocketMessageType type)
        {
            if (Outgoing.Writer.TryWrite((bytes, type))) return true;
            Ready = false;
            Outgoing.Writer.TryComplete();
            // Enqueue is called under individual session locks. Fail them outside those locks.
            _ = Task.Run(() => Fail("queue_overflow"));
            Socket.Abort(); // An unsent cancel must not leave an inference stream occupying capacity.
            return false;
        }

        public void Fail(string reason)
        {
            Ready = false;
            if (Chat is { } chat)
            {
                Chat = null;
                chat.Deadline?.Dispose();
                chat.Completion.TrySetException(new FreeAgentRelayException("free_unavailable", 503));
            }
            foreach (var session in Sessions.Values) session.Fail(reason);
            Outgoing.Writer.TryComplete();
            ChatOutgoing.Writer.TryComplete();
        }

        public void Dispose() { Outgoing.Writer.TryComplete(); ChatOutgoing.Writer.TryComplete(); }
    }
}

internal sealed class BeefyErMessage
{
    public int Version { get; set; } = 1;
    public string Type { get; set; } = "";
    public string Generation { get; set; } = "";
    public string? ChatModel { get; set; }
    public int ChatCapacity { get; set; }
    public string? RequestId { get; set; }
    public IReadOnlyList<FreeAgentChatMessage>? Messages { get; set; }
    public string? StreamId { get; set; }
    public string? SessionId { get; set; }
    public string? TurnId { get; set; }
    public long Sequence { get; set; }
    public string? Text { get; set; }
    public string? Model { get; set; }
    public string? Code { get; set; }
    public int Capacity { get; set; }
    public int SampleRate { get; set; } = 16000;
    public int EndpointMs { get; set; } = 600;
    public double MaxSpeechSeconds { get; set; } = 20;
    public double BacklogSeconds { get; set; }
    public IReadOnlyList<string> Hints { get; set; } = [];
    public int? Threads { get; set; }
    public int? RightContext { get; set; }
    public bool IndependentEndpointing { get; set; }
}
