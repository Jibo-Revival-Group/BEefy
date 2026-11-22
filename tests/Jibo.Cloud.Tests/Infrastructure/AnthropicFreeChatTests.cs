using System.Net;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Infrastructure.Agent;
using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Tests.Infrastructure;

// Free mode's Anthropic call: what it sends, and that failures come back only as codes (never Anthropic's text).
public sealed class AnthropicFreeChatTests
{
    [Fact]
    public async Task SendsModelSystemAndMessages_ReturnsTheText()
    {
        using var keyFile = new TempFile("sk-test-key\n");
        var handler = new Handler(HttpStatusCode.OK, "{\"content\":[{\"type\":\"text\",\"text\":\" Hi there! \"},{\"type\":\"thinking\",\"thinking\":\"x\"}]}");
        var chat = new AnthropicFreeChat(new HttpClient(handler), new FreeAgentOptions { KeyFile = keyFile.Path });

        var text = await chat.ChatAsync("You are Jibo.", [new("user", "hello")], "claude-sonnet-5-5", CancellationToken.None);

        Assert.Equal("Hi there!", text);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal("sk-test-key", handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        var body = JsonDocument.Parse(handler.Body!).RootElement;
        Assert.Equal("claude-sonnet-5-5", body.GetProperty("model").GetString());
        Assert.Equal("You are Jibo.", body.GetProperty("system").GetString());
        Assert.Equal(1024, body.GetProperty("max_tokens").GetInt32());
        Assert.Equal("hello", body.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"secret detail\"}}", "free_busy", 503)]
    [InlineData((HttpStatusCode)529, "{}", "free_busy", 503)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"secret detail\"}}", "free_unavailable", 502)]
    [InlineData(HttpStatusCode.Unauthorized, "{}", "free_unavailable", 502)]
    [InlineData(HttpStatusCode.OK, "{\"content\":[]}", "free_unavailable", 502)]
    [InlineData(HttpStatusCode.OK, "{\"content\":[{\"type\":\"text\",\"text\":\"   \"}]}", "free_unavailable", 502)]
    public async Task Failures_AreOnlyCodes(HttpStatusCode status, string reply, string code, int httpStatus)
    {
        using var keyFile = new TempFile("sk-test-key");
        var chat = new AnthropicFreeChat(new HttpClient(new Handler(status, reply)), new FreeAgentOptions { KeyFile = keyFile.Path });
        var error = await Assert.ThrowsAsync<FreeAgentRelayException>(() => chat.ChatAsync("s", [new("user", "hi")], "m", CancellationToken.None));
        Assert.Equal(code, error.Code);
        Assert.Equal(httpStatus, error.Status);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task WithoutAKey_NothingIsSent()
    {
        var handler = new Handler(HttpStatusCode.OK, "{}");
        var chat = new AnthropicFreeChat(new HttpClient(handler), new FreeAgentOptions { KeyFile = "/nonexistent/anthropic-key" });
        var error = await Assert.ThrowsAsync<FreeAgentRelayException>(() => chat.ChatAsync("s", [new("user", "hi")], "m", CancellationToken.None));
        Assert.Equal(503, error.Status);
        Assert.Null(handler.Request);
    }

    private sealed class Handler(HttpStatusCode status, string reply) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class TempFile : IDisposable
    {
        public readonly string Path = System.IO.Path.GetTempFileName();
        public TempFile(string text) => File.WriteAllText(Path, text);
        public void Dispose() => File.Delete(Path);
    }
}
