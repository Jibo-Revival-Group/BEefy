using System.Collections.Concurrent;
using System.Text.Json;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Delivers Home Assistant commands to a robot that paired locally, then waits
/// for the robot to post the command result back when the turn needs it.
/// </summary>
public sealed class HomeAssistantRobotRelay
{
    public const string CallbackPath = "/v1/homeassistant/robot-result";
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, TaskCompletionSource<HomeAssistantCommandResult>> _pending =
        new(StringComparer.Ordinal);

    public bool IsHaLocal(TurnContext turn) => IsLocal(turn);

    public static bool IsLocal(TurnContext turn)
    {
        if (!turn.Attributes.TryGetValue("haLocal", out var value) || value is null)
            return false;

        return value is true ||
               string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<HomeAssistantCommandResult?> SendAsync(
        string command,
        IReadOnlyDictionary<string, string>? parameters,
        bool waitForResult,
        CancellationToken cancellationToken)
    {
        var send = AmbientTurnProgressPublisher.TryGetSendAsync();
        if (send is null)
            return null;

        var requestId = Guid.NewGuid().ToString("N");
        string? callbackToken = null;
        TaskCompletionSource<HomeAssistantCommandResult>? pending = null;
        if (waitForResult)
        {
            callbackToken = Guid.NewGuid().ToString("N");
            pending = new TaskCompletionSource<HomeAssistantCommandResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[callbackToken] = pending;
        }

        try
        {
            var data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["command"] = command,
                ["requestId"] = requestId
            };
            if (parameters is not null)
            {
                foreach (var pair in parameters)
                    data[pair.Key] = pair.Value;
            }

            if (callbackToken is not null)
            {
                data["callbackToken"] = callbackToken;
                data["callbackPath"] = CallbackPath;
            }

            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["type"] = "HA_COMMAND",
                ["data"] = data
            });
            await send(new WebSocketReply { Text = json }, cancellationToken);

            if (!waitForResult)
                return new HomeAssistantCommandResult(requestId, "ok");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResultTimeout);
            return await pending!.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HomeAssistantCommandResult.Timeout(requestId);
        }
        finally
        {
            if (callbackToken is not null)
                _pending.TryRemove(callbackToken, out _);
        }
    }

    public bool TryComplete(string callbackToken, HomeAssistantCommandResult result)
    {
        if (string.IsNullOrWhiteSpace(callbackToken))
            return false;
        if (!_pending.TryRemove(callbackToken, out var pending))
            return false;

        pending.TrySetResult(result);
        return true;
    }
}
