using System.Net.Http.Json;
using System.Text.Json;
using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Infrastructure.Agent;

public interface IFreeAgentChat
{
    Task<string> ChatAsync(string system, IReadOnlyList<FreeAgentChatMessage> messages, string model, CancellationToken token);
}

/// <summary>Free mode's replies, from Anthropic's Messages API with 5x1's key. Failures surface only as codes.</summary>
public sealed class AnthropicFreeChat(HttpClient http, FreeAgentOptions options) : IFreeAgentChat
{
    public const string ClientName = "free-agent-anthropic";
    private const int MaxTokens = 1024; // Jibo answers in a sentence or two; this caps a runaway reply's cost

    public async Task<string> ChatAsync(string system, IReadOnlyList<FreeAgentChatMessage> messages, string model, CancellationToken token)
    {
        var key = options.ApiKey() ?? throw new FreeAgentRelayException("free_unavailable", 503);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model, max_tokens = MaxTokens, system,
                messages = messages.Select(m => new { role = m.Role, content = m.Content })
            })
        };
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw (int)response.StatusCode is 429 or 529
                ? new FreeAgentRelayException("free_busy", 503)
                : new FreeAgentRelayException("free_unavailable", 502);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var text = document.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n", content.EnumerateArray()
                .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "text" && part.TryGetProperty("text", out _))
                .Select(part => part.GetProperty("text").GetString()))
            : "";
        text = text.Trim();
        if (text.Length is 0 or > 12000) throw new FreeAgentRelayException("free_unavailable", 502);
        return text;
    }
}
