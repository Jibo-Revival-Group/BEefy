using System.Net;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Nlu;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class JevNluClassifierTests
{
    [Fact]
    public async Task SendsTypedDecisionWithIndependentCredentials_UsesSelectedProbability()
    {
        var handler = new Handler(async (request, token) =>
        {
            Assert.Equal("https://openrouter.ai/api/alpha/decisions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("hello", json.RootElement.GetProperty("state").GetProperty("transcript").GetString());
            var question = json.RootElement.GetProperty("questions").GetProperty("intent");
            Assert.Equal("choice", question.GetProperty("type").GetString());
            Assert.True(question.GetProperty("criteria").TryGetProperty("time", out _));
            Assert.True(question.GetProperty("criteria").TryGetProperty("unknown", out _));
            return Response(Answer("time", "0.9"));
        });
        var result = await Client(handler).ClassifyAsync("hello");
        Assert.Equal("time", result!.Intent);
        Assert.Equal(0.9, result.Probability); // response.confidence is deliberately 0.1
        Assert.Equal("typesafe/test-snapshot", result.Model);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("time", "0.849", false)]
    [InlineData("time", "0.85", true)]
    [InlineData("time", "1.1", false)]
    [InlineData("time", "-1", false)]
    [InlineData("time", "\"0.9\"", false)]
    [InlineData("unknown", "0.99", false)]
    [InlineData("invented", "0.99", false)]
    public async Task GatesSelectedProbabilityAndAllowlist(string intent, string probability, bool accepted)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Answer(intent, probability))));
        Assert.Equal(accepted, await Client(handler).ClassifyAsync("hello") is not null);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"answers\":null}")]
    [InlineData("{\"answers\":{\"intent\":{\"type\":123}}}")]
    [InlineData("{\"answers\":{\"intent\":{\"type\":\"choice\",\"choice\":\"time\",\"probabilities\":{\"time\":0.9,\"joke\":0.95}}}}")]
    public async Task MalformedResponse_IsNoMatch(string body)
    {
        Assert.Null(await Client(new Handler((_, _) => Task.FromResult(Response(body)))).ClassifyAsync("hello"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpError_FallsBackWithoutRetry(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        Assert.Null(await Client(handler).ClassifyAsync("hello"));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Timeout_ReturnsNoMatch_RecordsOutcome()
    {
        var metrics = new Mock<ITransportMetrics>();
        var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response("{}");
        });
        Assert.Null(await Client(handler, new JevNluOptions { Enabled = true, ApiKey = "test-key", TimeoutMs = 20 }, metrics.Object)
            .ClassifyAsync("hello"));
        metrics.Verify(m => m.TurnPhaseCompleted("nlu", "timeout", It.Is<double>(value => value >= 0)), Times.Once);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new Handler(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Response("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).ClassifyAsync("hello", cancellation.Token));
    }

    [Fact]
    public async Task NetworkFailure_ReturnsNoMatch()
    {
        Assert.Null(await Client(new Handler((_, _) => throw new HttpRequestException("failed"))).ClassifyAsync("hello"));
    }

    [Fact]
    public async Task DisabledOrIncompleteConfiguration_DoesNotSend()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Should not call"));
        Assert.Null(await Client(handler, new JevNluOptions()).ClassifyAsync("hello"));
        Assert.Null(await Client(handler, new JevNluOptions { Enabled = true }).ClassifyAsync("hello"));
        Assert.Null(await Client(handler).ClassifyAsync(" "));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void Configuration_EnvironmentOverridesSection_AndRemainsIndependentOfSearch()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenJibo:Nlu:Jev:Model"] = "section-model",
            ["OpenJibo:Search:Primary"] = "chatgpt!other-key!other-model"
        }).Build();
        var env = new Dictionary<string, string?>
        {
            ["OPENJIBO_JEV_ENABLED"] = "true",
            ["OPENJIBO_JEV_API_KEY"] = "test-key",
            ["OPENJIBO_JEV_MODEL"] = "env-model",
            ["OPENJIBO_JEV_MIN_PROBABILITY"] = "0.9"
        };
        var options = JevNluOptions.Resolve(config, name => env.GetValueOrDefault(name));
        Assert.True(options.IsConfigured);
        Assert.Equal("env-model", options.Model);
        Assert.Equal(0.9, options.MinProbability);
        Assert.Equal("test-key", options.ApiKey);
    }

    [Theory]
    [InlineData(0, 0.85, "https://openrouter.ai/api/alpha/decisions")]
    [InlineData(1001, 0.85, "https://openrouter.ai/api/alpha/decisions")]
    [InlineData(1000, 1.1, "https://openrouter.ai/api/alpha/decisions")]
    [InlineData(1000, 0.85, "not-a-url")]
    public async Task InvalidConfiguration_DoesNotSend(int timeout, double probability, string endpoint)
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Should not call"));
        Assert.Null(await Client(handler, new JevNluOptions { Enabled = true, ApiKey = "test-key",
            TimeoutMs = timeout, MinProbability = probability, Endpoint = endpoint }).ClassifyAsync("hello"));
        Assert.Equal(0, handler.Calls);
    }

    private static string Answer(string intent, string probability)
    {
        using var parsed = JsonDocument.Parse(probability);
        return JsonSerializer.Serialize(new { model = "typesafe/test-snapshot", answers = new
        { intent = new { type = "choice", choice = intent, confidence = 0.1,
            probabilities = new Dictionary<string, JsonElement> { [intent] = parsed.RootElement.Clone() } } } });
    }
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static JevNluClassifier Client(Handler handler, JevNluOptions? options = null, ITransportMetrics? metrics = null) =>
        new(new HttpClient(handler), options ?? new JevNluOptions { Enabled = true, ApiKey = "test-key" },
            NullLogger<JevNluClassifier>.Instance, metrics);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }
}
