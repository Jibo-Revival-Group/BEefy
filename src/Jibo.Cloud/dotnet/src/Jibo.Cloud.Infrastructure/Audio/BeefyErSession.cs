using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Infrastructure.Audio;

internal sealed class BeefyErSession(
    BeefyErBroker broker, BeefyErBroker.Connection connection, BeefyErOptions options,
    string sessionId, string turnId) : IDisposable
{
    private readonly object _gate = new();
    private readonly OggOpusIncrementalPcmDecoder _decoder = new();
    private readonly TaskCompletionSource<SttResult> _final = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _sequence;
    private long _receivedSequence;
    private string _text = "";
    private bool _endpoint;
    private bool _finishing;
    private bool _disposed;
    private string? _failure;
    private long _finishStarted;
    private long _firstAudioStarted;
    private bool _sawPartial;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    private readonly long _created = Stopwatch.GetTimestamp();
    /// <summary>How long this turn may hold a worker slot; no real turn lasts longer.</summary>
    internal TimeSpan Lifetime { get; init; } = TimeSpan.MaxValue;
    internal bool IsStale => Stopwatch.GetElapsedTime(_created) > Lifetime;
    public bool Failed { get { lock (_gate) return _failure is not null; } }
    public string Failure { get { lock (_gate) return _failure ?? "unavailable"; } }
    public string PartialText { get { lock (_gate) return _text; } }
    public bool IsEndpoint { get { lock (_gate) return _endpoint; } }
    public bool AllowAudioGapEndpointing => !connection.IndependentEndpointing;

    internal bool Send(string type, int endpointMs = 600, double maxSpeechSeconds = 20,
        IReadOnlyList<string>? hints = null)
        => connection.Enqueue(JsonSerializer.SerializeToUtf8Bytes(new BeefyErMessage
        {
            Type = type, Generation = connection.Generation, StreamId = Id, SessionId = sessionId,
            TurnId = turnId, Sequence = _sequence, EndpointMs = endpointMs, MaxSpeechSeconds = maxSpeechSeconds,
            Hints = hints ?? []
        }, BeefyErBroker.JsonOptions), WebSocketMessageType.Text);

    public void AcceptFrames(IReadOnlyList<byte[]> frames)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null) return;
            var pcm = _decoder.DecodeNewTo16kMono(frames);
            if (pcm.Length == 0) return;
            if (_finishing) { Fail("audio_after_finish"); return; }
            // Limit each message to 100 ms of PCM, including on initial buffered replay.
            for (var offset = 0; offset < pcm.Length; offset += 1600)
                if (!SendPcm(pcm.AsSpan(offset, Math.Min(1600, pcm.Length - offset)))) return;
        }
    }

    internal bool SendPcm(ReadOnlySpan<float> pcm)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null || _finishing) return false;
            if (_firstAudioStarted == 0) _firstAudioStarted = Stopwatch.GetTimestamp();
            var packet = new byte[24 + pcm.Length * 2];
            Convert.FromHexString(Id).CopyTo(packet, 0);
            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(16), ++_sequence);
            for (var i = 0; i < pcm.Length; i++)
                BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(24 + i * 2),
                    (short)Math.Clamp(MathF.Round(pcm[i] * 32768), short.MinValue, short.MaxValue));
            return connection.Enqueue(packet, WebSocketMessageType.Binary);
        }
    }

    internal void Receive(BeefyErMessage message)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null) return;
            if (message.SessionId != sessionId || message.TurnId != turnId || message.Sequence < _receivedSequence ||
                message.Sequence > _sequence)
            {
                Fail("invalid_result_identity_or_sequence");
                return;
            }
            _receivedSequence = message.Sequence;
            double? firstPartialLatency = null;
            if (message.Type == "partial" && !_sawPartial && _firstAudioStarted != 0)
            {
                _sawPartial = true;
                firstPartialLatency = Stopwatch.GetElapsedTime(_firstAudioStarted).TotalMilliseconds;
            }
            broker.RecordProgress(firstPartialLatency, message.BacklogSeconds);
            switch (message.Type)
            {
                case "partial":
                    _text = message.Text ?? "";
                    _endpoint = false;
                    break;
                case "endpoint":
                    _text = message.Text ?? "";
                    _endpoint = true;
                    break;
                case "progress": break;
                case "speech_started": _endpoint = false; break;
                case "final":
                    if (!_finishing || message.Sequence != _sequence) { Fail("unexpected_final"); break; }
                    _text = message.Text ?? "";
                    var latency = Stopwatch.GetElapsedTime(_finishStarted).TotalMilliseconds;
                    if (_final.TrySetResult(new SttResult
                    {
                        Text = _text, Provider = "beefyer-nemotron-streaming", Locale = "en-US",
                        Metadata = new Dictionary<string, object?>
                        {
                            ["model"] = connection.Model, ["finalLatencyMs"] = latency,
                            ["audioSequence"] = _sequence
                        }
                    })) broker.RecordFinal(latency);
                    break;
                case "error": Fail("worker_error"); break;
                default: Fail("unknown_worker_event"); break;
            }
        }
    }

    public void ResetEndpoint()
    {
        lock (_gate) _endpoint = false;
        // The worker commits acoustic segments itself; resetting this latch never discards words.
    }

    public async Task<SttResult> FinishAsync(CancellationToken token)
    {
        lock (_gate)
        {
            if (_failure is not null) throw new IOException(_failure);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_finishing)
            {
                _finishing = true;
                _finishStarted = Stopwatch.GetTimestamp();
                Send("finish");
            }
        }
        try { return await _final.Task.WaitAsync(options.FinalTimeout, token); }
        catch (TimeoutException) { Fail("final_timeout"); throw; }
    }

    internal void Fail(string reason)
    {
        lock (_gate)
        {
            if (_failure is not null || _disposed) return;
            _failure = reason;
            _final.TrySetException(new IOException(reason));
            // Observe faults even when a failure happens before finalization is requested.
            _ = _final.Task.Exception;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _final.TrySetCanceled();
            Send("cancel");
            connection.Sessions.TryRemove(Id, out _);
            _decoder.Dispose();
        }
    }
}
