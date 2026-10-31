using System.Net;
using System.Net.WebSockets;
using Jibo.Cloud.Infrastructure.Audio;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Jibo.Cloud.Infrastructure.Agent;
using Moq;

namespace Jibo.Cloud.Tests.Api;

public sealed class FreeAgentEndpointTests
{
    [Fact]
    public async Task AnonymousRobotIdReachesWorkerWithoutEnrollmentOrSignature()
    {
        await using var factory = Factory(enrolled: false, knownSecret: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent("{\"requestId\":\"0123456789abcdef0123456789abcdef\",\"robotId\":\"robot-1\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", Encoding.UTF8, "application/json") };
        var response = await Client(factory).SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("robot\n")]
    public async Task RejectsInvalidRobotId(string robotId)
    {
        await using var factory = Factory();
        var body = JsonSerializer.Serialize(new { requestId = Guid.NewGuid().ToString("N"), robotId, messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(factory).SendAsync(Signed(body))).StatusCode);
    }

    [Fact]
    public async Task RejectsRobotIdAbove128Characters()
    {
        await using var factory = Factory();
        Assert.Equal(HttpStatusCode.BadRequest, (await Client(factory).SendAsync(Signed(Body().Replace("robot-1", new string('a', 129))))).StatusCode);
    }

    [Fact]
    public async Task ReplayIsKeyedByIpNotRobotId()
    {
        await using var factory = Factory();
        var client = Client(factory); var body = Body();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(Signed(body))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Signed(body.Replace("robot-1", "robot-2")))).StatusCode);
        factory.Services.GetRequiredService<ConnectionAddress>().Value = IPAddress.Parse("203.0.113.11");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(Signed(body))).StatusCode);
    }
    internal const string Path = "/api/agent/free/chat";
    internal const string Target = "AgentFree_20261026.Chat";
    internal const string Key = "test-native-key";
    internal const string Secret = "test-native-secret";
    internal static string Body(string? id = null) => JsonSerializer.Serialize(new { requestId = id ?? Guid.NewGuid().ToString("N"), robotId = "robot-1", messages = new[] { new { role = "user", content = "hello" } } });
    internal static WebApplicationFactory<Program> Factory(bool enabled = true, bool enrolled = true, bool active = true, bool knownSecret = true)
    {
        var store = new Mock<ICloudStateStore>();
        store.Setup(s => s.GetAccount()).Returns(new AccountProfile { AccessKeyId = knownSecret ? Key : "other", SecretAccessKey = Secret });
        store.Setup(s => s.GetDevices()).Returns(new[] { new DeviceRegistration { DeviceId = "canonical", IsActive = active }, new DeviceRegistration { DeviceId = "canonical2" } });
        store.Setup(s => s.FindUserByAccessKeyId("key2")).Returns(new UserRecord { AccessKeyId = "key2", SecretAccessKey = Secret });
        store.Setup(s => s.FindUserByAccessKeyId("alias-key")).Returns(new UserRecord { AccessKeyId = "alias-key", SecretAccessKey = Secret });
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:Agent:Free:Enabled"] = enabled.ToString(),
                ["OpenJibo:Agent:Free:Enrollments:" + Key] = enrolled ? "canonical" : "",
                ["OpenJibo:Agent:Free:Enrollments:key2"] = "canonical2",
                ["OpenJibo:Agent:Free:Enrollments:alias-key"] = "canonical",
                ["OpenJibo:Stt:EnableStreamingSherpa"] = "false",
                ["OpenJibo:Stt:Sherpa:WarmupOnStartup"] = "false"
            }));
            builder.ConfigureServices(services => {
                services.AddSingleton(store.Object);
                services.AddSingleton<ConnectionAddress>();
                services.AddSingleton<IStartupFilter, TestConnectionFilter>();
                var workerOptions = new BeefyErOptions { Enabled = true, SharedSecret = new string('s', 43) };
                services.AddSingleton(workerOptions);
                services.AddSingleton(new BeefyErBroker(workerOptions, NullLogger<BeefyErBroker>.Instance));
            });
        });
    }
    internal sealed class ConnectionAddress { public IPAddress? Value = IPAddress.Parse("203.0.113.10"); }
    private sealed class TestConnectionFilter(ConnectionAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) => { context.Connection.RemoteIpAddress = address.Value; await continuation(); });
            next(app);
        };
    }
    internal static HttpRequestMessage Signed(string body, string? date = null, string target = Target, string secret = Secret, string key = Key)
    {
        var headers = new Dictionary<string, string> { ["Host"] = "localhost", ["X-Amz-Target"] = target, ["X-Amz-Date"] = date ?? DateTimeOffset.UtcNow.ToString("r") };
        var request = new HttpRequestMessage(HttpMethod.Post, Path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var h in headers) request.Headers.TryAddWithoutValidation(h.Key, h.Value);
        request.Headers.TryAddWithoutValidation("Authorization", Aws3Signature.BuildAuthorization(key, secret, "POST", headers, body));
        return request;
    }
    internal static HttpClient Client(WebApplicationFactory<Program> factory, bool https = true) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(https ? "https://localhost" : "http://localhost") });

    [Theory]
    [InlineData(false, true, 503)]
    [InlineData(true, false, 403)]
    public async Task RequiresEnableAndHttps(bool enabled, bool https, int status)
    {
        await using var factory = Factory(enabled);
        var response = await Client(factory, https).SendAsync(Signed(Body()));
        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("{}", 400)]
    [InlineData("{\"requestId\":\"0123456789ABCDEF0123456789abcdef\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}", 400)]
    [InlineData("{\"requestId\":\"0123456789abcdef0123456789abcdef\",\"messages\":[{\"role\":\"system\",\"content\":\"hi\"}]}", 400)]
    [InlineData("{\"requestId\":\"0123456789abcdef0123456789abcdef\",\"messages\":[{\"role\":\"user\",\"content\":\" \"}]}", 400)]
    [InlineData("{\"requestId\":\"0123456789abcdef0123456789abcdef\",\"messages\":[{\"role\":\"assistant\",\"content\":\"hi\"}]}", 400)]
    [InlineData("{\"requestId\":\"0123456789abcdef0123456789abcdef\",\"messages\":[],\"model\":\"anything\"}", 400)]
    [InlineData("{\"requestId\":\"0123456789abcdef0123456789abcdef\",\"requestId\":\"0123456789abcdef0123456789abcdef\",\"messages\":[]}", 400)]
    [InlineData("not-json", 400)]
    public async Task InvalidContractsNeverDispatch(string body, int expected)
    {
        await using var factory = Factory();
        var response = await Client(factory).SendAsync(Signed(body));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BoundsBodyAndWorkerEnvelopeByUtf8Bytes()
    {
        await using var factory = Factory();
        var client = Client(factory);
        foreach (var body in new[] { Body().Replace("hello", new string('x', 32001)), Body().Replace("hello", new string('x', 31800)) })
        {
            var response = await client.SendAsync(Signed(body));
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
    }

    [Fact]
    public async Task DuplicateAuthenticatedRequestIdCannotDispatchAgain()
    {
        await using var factory = Factory();
        using var worker = await Worker.Connect(factory);
        var client = Client(factory);
        var body = Body();
        var first = client.SendAsync(Signed(body));
        var start = await worker.Read("chat_start");
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_result", requestId = start.GetProperty("requestId").GetString(), text = "hi" });
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        var duplicate = await client.SendAsync(Signed(body)).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task SignedChatRelaysFixedSystemAndReturnsBoundedReply()
    {
        await using var factory = Factory();
        using var worker = await Worker.Connect(factory);
        var body = Body();
        var responseTask = Client(factory).SendAsync(Signed(body));
        var start = await worker.Read("chat_start");
        Assert.Matches("^[0-9a-f]{32}$", start.GetProperty("requestId").GetString()!);
        Assert.Equal(2, start.GetProperty("messages").GetArrayLength());
        Assert.Equal("system", start.GetProperty("messages")[0].GetProperty("role").GetString());
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_result", requestId = start.GetProperty("requestId").GetString(), text = "Hi from Jibo." });
        var response = await responseTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("gemma3:4b", result.GetProperty("model").GetString());
        Assert.Equal("Hi from Jibo.", result.GetProperty("text").GetString());
        Assert.Equal(JsonDocument.Parse(body).RootElement.GetProperty("requestId").GetString(), result.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task UnsupportedMethodsCannotFallThroughToLegacyCapture()
    {
        var telemetry = new Mock<IProtocolTelemetrySink>();
        await using var factory = Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton(telemetry.Object)));
        var client = Client(factory);
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete })
        {
            using var request = Signed(Body());
            request.Method = method;
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.SendAsync(request)).StatusCode);
        }
        telemetry.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyKnownProxyCanSupplyHttpsAndClientIp(bool trusted)
    {
        await using var factory = Factory();
        var client = Client(factory, https: false);
        if (trusted) factory.Services.GetRequiredService<ConnectionAddress>().Value = IPAddress.Loopback;
        using var request = Signed(Body());
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "198.51.100.123");
        var response = await client.SendAsync(request);
        Assert.Equal(trusted ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RobotQuotaIsSpoofableButIpQuotaCannotBeBypassedWithForwardedHeader()
    {
        await using var factory = Factory();
        var client = Client(factory);
        for (var i = 0; i < 200; i++)
        {
            var body = Body().Replace("robot-1", $"robot-{i / 50}");
            using var request = Signed(body);
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{i % 250 + 1}");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(request)).StatusCode);
        }
        var denied = await client.SendAsync(Signed(Body().Replace("robot-1", "robot-new")));
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        factory.Services.GetRequiredService<ConnectionAddress>().Value = IPAddress.Parse("203.0.113.11");
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.SendAsync(Signed(Body()))).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(Signed(Body().Replace("robot-1", "robot-new")))).StatusCode);
    }

    [Fact]
    public async Task MissingTrustedRemoteAddressFailsClosedEvenWithForwardedHeader()
    {
        await using var factory = Factory();
        var client = Client(factory);
        factory.Services.GetRequiredService<ConnectionAddress>().Value = null;
        using var request = Signed(Body());
        request.Headers.Add("X-Forwarded-For", "198.51.100.123");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("oversize")]
    [InlineData("error")]
    [InlineData("disconnect")]
    [InlineData("generation")]
    [InlineData("frame")]
    public async Task WorkerFailuresAreSanitizedAndNeverReturnRawDetails(string failure)
    {
        await using var factory = Factory();
        using var worker = await Worker.Connect(factory);
        var pending = Client(factory).SendAsync(Signed(Body()));
        var start = await worker.Read("chat_start");
        if (failure == "disconnect") worker.Dispose();
        else await worker.Send(new { version = 1, generation = failure == "generation" ? "stale-generation" : worker.Generation,
            type = failure == "error" ? "chat_error" : "chat_result", requestId = start.GetProperty("requestId").GetString(),
            code = "sensitive worker error", text = failure == "empty" ? " " : failure == "frame" ? new string('x', 70000) : failure == "oversize" ? new string('x', 12001) : "sensitive response" });
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(failure is "disconnect" or "generation" or "frame" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.BadGateway, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sensitive", content);
        Assert.DoesNotContain("Exception", content);
    }

    [Fact]
    public async Task OldWorkerWithoutAdvertisedChatIsUnavailable()
    {
        await using var factory = Factory();
        using var worker = await Worker.Connect(factory, chat: false);
        var response = await Client(factory).SendAsync(Signed(Body()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"free_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CancellationSendsCancelButHoldsSlotUntilTerminalAndIgnoresLateIds()
    {
        await using var factory = Factory();
        using var worker = await Worker.Connect(factory);
        var client = Client(factory);
        using var stop = new CancellationTokenSource();
        var first = client.SendAsync(Signed(Body()), stop.Token);
        var start = await worker.Read("chat_start");
        var busy = await client.SendAsync(Signed(Body()));
        Assert.Equal("{\"error\":\"free_busy\"}", await busy.Content.ReadAsStringAsync());
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var cancel = await worker.Read("chat_cancel");
        Assert.Equal(start.GetProperty("requestId").GetString(), cancel.GetProperty("requestId").GetString());
        var stillBusy = await client.SendAsync(Signed(Body()));
        Assert.Equal("{\"error\":\"free_busy\"}", await stillBusy.Content.ReadAsStringAsync());
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_error", requestId = start.GetProperty("requestId").GetString(), code = "cancelled" });
        await Task.Delay(20);
        var next = client.SendAsync(Signed(Body()));
        var nextStart = await worker.Read("chat_start");
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_result", requestId = start.GetProperty("requestId").GetString(), text = "stale" });
        Assert.False(next.IsCompleted);
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_result", requestId = nextStart.GetProperty("requestId").GetString(), text = "current" });
        Assert.Contains("current", await (await next).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ExpiredChatQuarantinesOnlyChatUntilMatchingCancellationAck()
    {
        await using var factory = Factory();
        using var worker = await Worker.Connect(factory);
        var broker = factory.Services.GetRequiredService<BeefyErBroker>();
        FreeAgentChatMessage[] messages = [new("user", "hi")];
        var pending = broker.ChatAsync(messages, CancellationToken.None, TimeSpan.FromMilliseconds(100));
        var start = await worker.Read("chat_start");
        var id = start.GetProperty("requestId").GetString();
        var error = await Assert.ThrowsAsync<FreeAgentRelayException>(() => pending);
        Assert.Equal("free_timeout", error.Code);
        Assert.Equal(id, (await worker.Read("chat_cancel")).GetProperty("requestId").GetString());
        Assert.Equal("free_busy", (await Assert.ThrowsAsync<FreeAgentRelayException>(() => broker.ChatAsync(messages, CancellationToken.None, TimeSpan.FromSeconds(5)))).Code);
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_cancelled", requestId = new string('f', 32) });
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_cancelled", requestId = id });
        // Polling is bounded; the receive loop owns the state transition.
        Task<string>? next = null;
        for (var i = 0; i < 100; i++)
        {
            try { next = broker.ChatAsync(messages, CancellationToken.None, TimeSpan.FromSeconds(5)); await Task.Yield(); if (next.IsFaulted) { await next; } break; }
            catch (FreeAgentRelayException ex) when (ex.Code == "free_busy") { await Task.Delay(10); }
        }
        Assert.NotNull(next);
        var nextStart = await worker.Read("chat_start");
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_cancelled", requestId = id });
        await worker.Send(new { version = 1, generation = worker.Generation, type = "chat_result", requestId = nextStart.GetProperty("requestId").GetString(), text = "ok" });
        Assert.Equal("ok", await next!);
    }

    [Fact]
    public async Task TotalTimeoutIs120SecondsAndUnacknowledgedSlotIsNotReused()
    {
        await using var factory = Factory();
        using var worker = await Worker.Connect(factory);
        var client = Client(factory);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        client.Timeout = TimeSpan.FromSeconds(130);
        var pending = client.SendAsync(Signed(Body()));
        await worker.Read("chat_start");
        // Keep the real worker heartbeat alive while allowing its chat to stall.
        var cancelTask = worker.Read("chat_cancel", 125);
        await Task.Delay(TimeSpan.FromSeconds(47));
        Assert.False(pending.IsCompleted);
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(80));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(119), TimeSpan.FromSeconds(125));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("{\"error\":\"free_timeout\"}", await response.Content.ReadAsStringAsync());
        await cancelTask;
        await Task.Delay(100);
        var blocked = await client.SendAsync(Signed(Body()));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal("{\"error\":\"free_busy\"}", await blocked.Content.ReadAsStringAsync());
    }

    internal sealed class Worker(WebSocket socket, string generation) : IDisposable
    {
        public readonly string Generation = generation;
        public static async Task<Worker> Connect(WebApplicationFactory<Program> factory, bool chat = true)
        {
            var wsClient = factory.Server.CreateWebSocketClient();
            wsClient.ConfigureRequest = request => request.Headers.Authorization = "Bearer " + new string('s', 43);
            var socket = await wsClient.ConnectAsync(new Uri("wss://localhost/beefyer/connect"), CancellationToken.None);
            var worker = new Worker(socket, "");
            var hello = await worker.Read("hello");
            worker = new Worker(socket, hello.GetProperty("generation").GetString()!);
            await worker.Send(new { version = 1, generation = worker.Generation, type = "ready", model = "nemotron-test", capacity = 1,
                chatModel = chat ? "gemma3:4b" : null, chatCapacity = chat ? 1 : 0 });
            // A pong crosses the single send/receive loops after ready, avoiding a readiness race.
            await worker.Send(new { version = 1, generation = worker.Generation, type = "pong" });
            await Task.Delay(20);
            return worker;
        }
        public async Task Send(object message) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
        public async Task<JsonElement> Read(string type, int seconds = 5)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var bytes = new byte[65536];
            while (true)
            {
                var length = 0;
                ValueWebSocketReceiveResult frame;
                do { frame = await socket.ReceiveAsync(bytes.AsMemory(length), timeout.Token); length += frame.Count; } while (!frame.EndOfMessage);
                var message = JsonDocument.Parse(bytes.AsMemory(0, length)).RootElement.Clone();
                var receivedType = message.GetProperty("type").GetString();
                if (receivedType == "ping") { await Send(new { version = 1, generation = Generation, type = "pong" }); continue; }
                if (receivedType == type) return message;
            }
        }
        public void Dispose() => socket.Abort();
    }

}
