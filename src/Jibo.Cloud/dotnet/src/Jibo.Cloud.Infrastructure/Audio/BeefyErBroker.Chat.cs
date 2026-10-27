using System.Net.WebSockets;
using System.Text.Json;

namespace Jibo.Cloud.Infrastructure.Audio;

public sealed record FreeAgentChatMessage(string Role, string Content);
public sealed class FreeAgentRelayException(string code, int status) : Exception(code)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public sealed partial class BeefyErBroker
{
    internal sealed class PendingChat
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly TaskCompletionSource<string> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Timer? Deadline;
        public bool CancelSent;
    }

    private void ExpireChat(Connection connection, PendingChat chat)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(connection.Chat, chat)) return;
            chat.Deadline?.Dispose();
            chat.Completion.TrySetException(new FreeAgentRelayException("free_timeout", 504));
            SendChatCancel(connection, chat);
            // Retain the chat lease until a terminal frame confirms cleanup. ASR stays live.
        }
    }

    private static void SendChatCancel(Connection connection, PendingChat chat)
    {
        if (chat.CancelSent) return;
        chat.CancelSent = connection.ChatOutgoing.Writer.TryWrite((JsonSerializer.SerializeToUtf8Bytes(new
            { version = 1, generation = connection.Generation, type = "chat_cancel", requestId = chat.Id }, JsonOptions), WebSocketMessageType.Text));
    }

    public static byte[] ChatEnvelope(IReadOnlyList<FreeAgentChatMessage> messages, string generation, string requestId) =>
        JsonSerializer.SerializeToUtf8Bytes(new { version = 1, generation, type = "chat_start", requestId, messages }, JsonOptions);

    public async Task<string> ChatAsync(IReadOnlyList<FreeAgentChatMessage> messages, CancellationToken token, TimeSpan leaseDuration)
    {
        PendingChat chat;
        Connection selected;
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_connection is not { Ready: true, ChatCapacity: 1 } connection)
                throw new FreeAgentRelayException("free_unavailable", 503);
            if (connection.Chat is not null) throw new FreeAgentRelayException("free_busy", 503);
            selected = connection;
            chat = new PendingChat();
            // A canceled HTTP waiter may leave this task to finish on a later terminal/disconnect.
            _ = chat.Completion.Task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            connection.Chat = chat;
            var envelope = ChatEnvelope(messages, connection.Generation, chat.Id);
            if (envelope.Length > 32000) { connection.Chat = null; throw new FreeAgentRelayException("invalid_request", 413); }
            chat.Deadline = new Timer(_ => ExpireChat(selected, chat), null,
                leaseDuration <= TimeSpan.Zero ? TimeSpan.Zero : leaseDuration, Timeout.InfiniteTimeSpan);
            if (connection.Outgoing.Reader.Count >= options.SendQueueCapacity ||
                !connection.ChatOutgoing.Writer.TryWrite((envelope, WebSocketMessageType.Text)))
            {
                connection.Chat = null;
                chat.Deadline.Dispose();
                throw new FreeAgentRelayException("free_unavailable", 503);
            }
        }
        try { return await chat.Completion.Task.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                if (selected.Ready && ReferenceEquals(selected.Chat, chat))
                    SendChatCancel(selected, chat);
                // The lease remains quarantined until a terminal frame or disconnect.
            }
            throw;
        }
    }
}
