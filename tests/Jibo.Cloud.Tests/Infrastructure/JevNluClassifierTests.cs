using System.Net;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Nlu;
using Jibo.Cloud.Application.Services;
using Microsoft.Extensions.Logging;
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
            Assert.Equal("typesafe/jev-1.13", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("application/json", request.Content.Headers.ContentType!.MediaType);
            var questions = json.RootElement.GetProperty("questions");
            var seen = new HashSet<string>();
            foreach (var question in questions.EnumerateObject())
            {
                Assert.Equal("choice", question.Value.GetProperty("type").GetString());
                var criteria = question.Value.GetProperty("criteria");
                Assert.InRange(criteria.EnumerateObject().Count(), 2, 255);
                Assert.Equal(question.Name != "intent_group", criteria.TryGetProperty("unknown", out _));
                if (question.Name != "intent_group")
                    foreach (var option in criteria.EnumerateObject().Where(p => p.Name != "unknown"))
                        Assert.True(seen.Add(option.Name), $"Duplicate leaf intent: {option.Name}");
            }
            Assert.NotEmpty(seen);
            return Response(Answer("time", "0.9"));
        });
        var result = await Client(handler).ClassifyAsync("hello");
        Assert.Equal("time", result!.Intent);
        Assert.Equal(0.9, result.Probability); // response.confidence is deliberately 0.1
        Assert.Equal("typesafe/test-snapshot", result.Model);
        Assert.Equal(JevNluClassifier.BuildQuestionBatches("hello")!.Count, handler.Calls);
    }

    [Fact]
    public void GreetingGroupExplicitlyCoversObservedChristmasMishearing()
    {
        var description = JevIntentQuestions.GroupCriteria["greetings_and_holidays"];
        Assert.Contains("mary christmas", description);
        Assert.True(JevIntentQuestions.Groups["greetings_and_holidays"].ContainsKey("holiday_greeting/christmas"));
        Assert.Contains("mary christmas", NluIntentCatalog.Criteria["holiday_greeting/christmas"]);
    }

    [Fact]
    public async Task UnknownSelectorCannotVetoConfidentHolidayLeaf()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(JsonSerializer.Serialize(new
        {
            answers = new Dictionary<string, object>
            {
                ["intent_group"] = Choice("unknown", 0.66),
                ["greetings_and_holidays"] = Choice("holiday_greeting/christmas", 0.87)
            }
        }))));
        var result = await Client(handler).ClassifyAsync("mary christmas");
        Assert.Equal("holiday_greeting/christmas", result!.Intent);
        Assert.Equal(0.87, result.Probability);
        Assert.Equal(JevNluClassifier.BuildQuestionBatches("hello")!.Count, handler.Calls);
    }

    [Fact]
    public void HierarchyCoversEveryResponseExactlyOnceWithinChoiceLimit()
    {
        var seen = new HashSet<string>();
        Assert.DoesNotContain("unknown", JevIntentQuestions.GroupCriteria.Keys);
        var batches = JevNluClassifier.BuildQuestionBatches("merry christmas")!;
        Assert.True(batches.Count > 1);
        Assert.All(batches, batch => Assert.True(JevNluClassifier.FitsContextBudgets("merry christmas", batch)));
        Assert.Equal(JevIntentQuestions.Questions.Count, batches.Sum(batch => batch.Count));
        void Visit(string name)
        {
            var criteria = JevIntentQuestions.Groups[name];
            Assert.InRange(criteria.Count, 2, 255);
            Assert.True(JevNluClassifier.FitsContextBudgets("merry christmas",
                new Dictionary<string, JevIntentQuestions.ChoiceQuestion>
                { [name] = new("choice", "Which response matches?", criteria) }), name);
            foreach (var key in criteria.Keys.Where(k => k != "unknown"))
                if (JevIntentQuestions.Groups.ContainsKey(key)) Visit(key);
                else Assert.True(seen.Add(key), $"Duplicate intent {key}");
        }
        foreach (var name in JevIntentQuestions.TopLevelGroups) Visit(name);
        Assert.Equal(NluIntentCatalog.Criteria.Keys.Where(k => k != "unknown").Order(), seen.Order());
        Assert.Equal(4656, NativeScriptedResponseCatalog.Commands.Count);
    }

    [Fact]
    public async Task OversizedTranscriptIsRejectedBeforeCallingJev()
    {
        var handler = new Handler((_, _) => throw new InvalidOperationException("Should not call"));
        Assert.Null(await Client(handler).ClassifyAsync(new string('x', 32_000)));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task LargeScriptedResponse_AllLeafGroupsAreCheckedRegardlessOfTopSelector()
    {
        var intent = NativeScriptedResponseCatalog.Commands.First(p =>
            p.Value.Memo.GetProperty("mim").GetString() == "RI_JBO_LikesCats").Key;
        var seen = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var handler = new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var questions = body.RootElement.GetProperty("questions");
            var answers = new Dictionary<string, object>();
            foreach (var question in questions.EnumerateObject())
            {
                if (question.Name == "intent_group")
                {
                    Assert.False(question.Value.GetProperty("criteria").TryGetProperty("unknown", out _));
                    answers[question.Name] = Choice("greetings_and_holidays", 1);
                    continue;
                }
                Assert.True(seen.TryAdd(question.Name, 0));
                var criteria = question.Value.GetProperty("criteria");
                Assert.InRange(criteria.EnumerateObject().Count(), 2, 255);
                answers[question.Name] = criteria.TryGetProperty(intent, out _) ? Choice(intent, 0.95) : Choice("unknown", 1);
            }
            return Response(JsonSerializer.Serialize(new { model = "test", answers }));
        });
        Assert.Equal(intent, (await Client(handler).ClassifyAsync("you seem fond of cats"))!.Intent);
        Assert.Equal(JevIntentQuestions.Questions.Keys.Where(k => k != "intent_group").Order(), seen.Keys.Order());
        Assert.True(handler.Calls > 1);
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
        Assert.Equal(JevNluClassifier.BuildQuestionBatches("hello")!.Count, handler.Calls);
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
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpError_FallsBackWithoutRetry(int status)
    {
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        Assert.Null(await Client(handler).ClassifyAsync("hello"));
        Assert.Equal(JevNluClassifier.BuildQuestionBatches("hello")!.Count, handler.Calls);
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
        Assert.True(handler.Calls > 0);
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

    [Theory]
    [InlineData("robot_can_dance")]
    [InlineData("robot_favorite_color")]
    [InlineData("fun_fact")]
    public async Task GroupedProtocol_PreservesIntentsAcrossTheFullCatalog(string intent)
    {
        Assert.Equal(intent, (await Client(new Handler((_, _) => Task.FromResult(Response(Answer(intent, "0.95")))))
            .ClassifyAsync("hello"))!.Intent);
    }

    [Theory]
    [InlineData(0.95, 0.9, true)]
    [InlineData(0.9, 0.9, true)]
    [InlineData(0.8, 1, true)]
    [InlineData(1, 0.849, false)]
    public async Task LeafProbabilityAloneDeterminesAcceptance(double groupProbability, double leafProbability, bool accepted)
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Answer("time",
            leafProbability.ToString(System.Globalization.CultureInfo.InvariantCulture), groupProbability))));
        var result = await Client(handler).ClassifyAsync("hello");
        Assert.Equal(accepted, result is not null);
        if (accepted) Assert.Equal(leafProbability, result!.Probability);
        Assert.Equal(JevNluClassifier.BuildQuestionBatches("hello")!.Count, handler.Calls);
    }

    [Theory]
    [InlineData("invented", "time")]
    [InlineData("robot_abilities", "time")]
    [InlineData("unknown", "time")]
    public async Task InvalidGroupOrCrossGroupLeaf_IsRejected(string group, string intent)
    {
        var response = JsonSerializer.Serialize(new { answers = new Dictionary<string, object>
        {
            ["intent_group"] = Choice(group, 1), [group] = Choice(intent, 1)
        } });
        Assert.Null(await Client(new Handler((_, _) => Task.FromResult(Response(response)))).ClassifyAsync("hello"));
    }

    [Fact]
    public async Task HighestLeafProbabilityWinsAcrossGroups()
    {
        var body = JsonSerializer.Serialize(new { answers = new Dictionary<string, object>
        {
            ["intent_group"] = Choice("commands_and_user", 1),
            ["commands_and_user"] = Choice("time", 0.95),
            ["robot_abilities"] = Choice("robot_can_dance", 1)
        } });
        Assert.Equal("robot_can_dance", (await Client(new Handler((_, _) => Task.FromResult(Response(body)))).ClassifyAsync("hello"))!.Intent);
    }

    [Fact]
    public async Task EnvThresholdIsAppliedDirectlyToBestLeaf()
    {
        var handler = new Handler((_, _) => Task.FromResult(Response(Answer("holiday_greeting/christmas", "0.87", 0.32))));
        var config = new ConfigurationBuilder().Build();
        var env = new Dictionary<string, string?>
        {
            ["OPENJIBO_JEV_ENABLED"] = "true", ["OPENJIBO_JEV_API_KEY"] = "test-key",
            ["OPENJIBO_JEV_MIN_PROBABILITY"] = "0.9"
        };
        Assert.Null(await Client(handler, JevNluOptions.Resolve(config, name => env.GetValueOrDefault(name)))
            .ClassifyAsync("mary christmas"));
    }

    [Fact]
    public async Task AllGroupsUnknownReturnNoMatch()
    {
        var handler = new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var answers = body.RootElement.GetProperty("questions").EnumerateObject()
                .ToDictionary(q => q.Name, q => Choice(q.Name == "intent_group" ? "greetings_and_holidays" : "unknown", 1));
            return Response(JsonSerializer.Serialize(new { answers }));
        });
        Assert.Null(await Client(handler).ClassifyAsync("unrelated unsupported speech"));
    }

    [Fact]
    public async Task RejectedRequest_LogsValidationReason_WithoutCredentialsOrTranscript()
    {
        var logs = new Mock<ILogger<JevNluClassifier>>();
        var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new
            { message = "Choice accepts at most 255 options: test-key; hello; sk-or-othersecret\n" } }))
        }));
        var classifier = new JevNluClassifier(new HttpClient(handler), new JevNluOptions
        { Enabled = true, ApiKey = "test-key" }, logs.Object);
        Assert.Null(await classifier.ClassifyAsync("hello"));
        var messages = logs.Invocations.Where(i => i.Method.Name == "Log")
            .Select(i => i.Arguments[2].ToString()).ToArray();
        Assert.Contains(messages, message => message!.Contains("HTTP 400") && message.Contains("255 options"));
        Assert.DoesNotContain(messages, message => message!.Contains("test-key") || message.Contains("hello") || message.Contains("sk-or-othersecret"));
    }

    private static object Choice(string choice, double probability) => new
    { type = "choice", choice, probabilities = new Dictionary<string, double> { [choice] = probability } };

    private static string Answer(string intent, string probability, double groupProbability = 1)
    {
        using var parsed = JsonDocument.Parse(probability);
        var group = JevIntentQuestions.Groups.FirstOrDefault(g => intent != "unknown" && g.Value.ContainsKey(intent)).Key
            ?? "commands_and_user";
        return JsonSerializer.Serialize(new { model = "typesafe/test-snapshot", answers = new Dictionary<string, object>
        {
            ["intent_group"] = Choice(group, groupProbability),
            [group] = new { type = "choice", choice = intent, confidence = 0.1,
                probabilities = new Dictionary<string, JsonElement> { [intent] = parsed.RootElement.Clone() } }
        } });
    }
    private static HttpResponseMessage Response(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static JevNluClassifier Client(Handler handler, JevNluOptions? options = null, ITransportMetrics? metrics = null) =>
        new(new HttpClient(handler), options ?? new JevNluOptions { Enabled = true, ApiKey = "test-key" },
            NullLogger<JevNluClassifier>.Instance, metrics);
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return send(request, cancellationToken);
        }
    }
}
