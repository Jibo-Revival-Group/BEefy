using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Calendar;
using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class MemberCalendarProtocolTests
{
    private const string FeedUrl = "https://calendar.example/private/secret-feed-token.ics";

    [Fact]
    public async Task SetAndGet_DoNotEchoTheFeedUrl()
    {
        using var harness = CreateHarness();
        var set = await harness.Service.DispatchAsync(Signed(harness, "SetCalendarFeed", new
        {
            memberId = "looper-zane",
            icalUrl = FeedUrl,
            isEnabled = true
        }));

        Assert.Equal(200, set.StatusCode);
        Assert.DoesNotContain("secret-feed-token", set.BodyText);
        using (var setPayload = JsonDocument.Parse(set.BodyText))
        {
            Assert.True(setPayload.RootElement.GetProperty("configured").GetBoolean());
            Assert.Equal("calendar.example", setPayload.RootElement.GetProperty("host").GetString());
            Assert.False(setPayload.RootElement.TryGetProperty("icalUrl", out _));
        }

        var get = await harness.Service.DispatchAsync(Signed(harness, "GetCalendarFeeds", new { }));
        Assert.Equal(200, get.StatusCode);
        Assert.DoesNotContain("secret-feed-token", get.BodyText);
        Assert.Contains("looper-zane", get.BodyText);

        var stored = harness.Store.FindMemberCalendarFeed("openjibo-default-loop", "looper-zane");
        Assert.Equal(FeedUrl, stored?.IcalUrl);
    }

    [Fact]
    public async Task Set_RejectsANonHttpsUrl()
    {
        using var harness = CreateHarness();
        var result = await harness.Service.DispatchAsync(Signed(harness, "SetCalendarFeed", new
        {
            memberId = "looper-zane",
            icalUrl = "http://calendar.example/feed.ics"
        }));

        Assert.Equal(400, result.StatusCode);
        Assert.Contains("https", result.BodyText);
        Assert.Null(harness.Store.FindMemberCalendarFeed("openjibo-default-loop", "looper-zane"));
    }

    [Fact]
    public async Task Clear_RemovesTheFeed()
    {
        using var harness = CreateHarness();
        await harness.Service.DispatchAsync(Signed(harness, "SetCalendarFeed", new
        {
            memberId = "looper-jon",
            icalUrl = FeedUrl
        }));

        var cleared = await harness.Service.DispatchAsync(Signed(harness, "ClearCalendarFeed", new
        {
            memberId = "looper-jon"
        }));

        Assert.Equal(200, cleared.StatusCode);
        using var payload = JsonDocument.Parse(cleared.BodyText);
        Assert.True(payload.RootElement.GetProperty("cleared").GetBoolean());
        Assert.Null(harness.Store.FindMemberCalendarFeed("openjibo-default-loop", "looper-jon"));
    }

    [Fact]
    public async Task Test_ReturnsProbeCountsWithoutTheUrl()
    {
        using var harness = CreateHarness();
        var result = await harness.Service.DispatchAsync(Signed(harness, "TestCalendarFeed", new
        {
            memberId = "looper-zane",
            icalUrl = FeedUrl
        }));

        Assert.Equal(200, result.StatusCode);
        Assert.DoesNotContain("secret-feed-token", result.BodyText);
        using var payload = JsonDocument.Parse(result.BodyText);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, payload.RootElement.GetProperty("todayEventCount").GetInt32());
        Assert.Equal("calendar.example", payload.RootElement.GetProperty("host").GetString());
    }

    [Fact]
    public async Task Set_RejectsAKnownKeyWithABadSignature()
    {
        using var harness = CreateHarness();
        var envelope = Signed(harness, "SetCalendarFeed", new
        {
            memberId = "looper-zane",
            icalUrl = FeedUrl
        });
        envelope.Headers["Authorization"] = envelope.Headers["Authorization"].Replace("Signature=", "Signature=AAAA");

        var result = await harness.Service.DispatchAsync(envelope);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task Set_AcceptsAnUnknownRobotKeyWhenTheRobotHeaderIsPresent()
    {
        using var harness = CreateHarness();
        var envelope = Signed(harness, "SetCalendarFeed", new
        {
            memberId = "looper-amber",
            icalUrl = FeedUrl
        }, accessKeyId: "robot-key-not-stored", secret: "robot-secret", deviceId: "kitchen-jibo");

        var result = await harness.Service.DispatchAsync(envelope);
        Assert.Equal(200, result.StatusCode);
        Assert.NotNull(harness.Store.FindMemberCalendarFeed("openjibo-default-loop", "looper-amber"));
    }

    [Fact]
    public async Task Set_RejectsConflictingRobotIdentity()
    {
        using var harness = CreateHarness();
        harness.Cloud.UpsertDevice(new DeviceRegistration
        {
            DeviceId = "bound-robot",
            RobotId = "bound-robot",
            FriendlyName = "Bound"
        });
        harness.Cloud.BindAwsCredentialFingerprint(
            "bound-robot",
            AwsRequestAccessKey.Fingerprint(harness.Cloud.GetAccount().AccessKeyId),
            "test");

        var envelope = Signed(harness, "SetCalendarFeed", new
        {
            memberId = "looper-zane",
            icalUrl = FeedUrl
        }, deviceId: "other-robot");

        var result = await harness.Service.DispatchAsync(envelope);
        Assert.Equal(401, result.StatusCode);
        Assert.Contains("conflict", result.BodyText, StringComparison.OrdinalIgnoreCase);
    }

    private static Harness CreateHarness()
    {
        var cloud = new InMemoryCloudStateStore();
        var path = Path.Combine(Path.GetTempPath(), $"openjibo-cal-protocol-{Guid.NewGuid():N}.json");
        var feeds = new InMemoryUserIntegrationStore(
            new EncryptedUserDataSnapshotStore(path, new UserDataEncryptionService()));
        var probe = new StubProbe();
        var service = new JiboCloudProtocolService(cloud, userIntegrationStore: feeds, calendarFeedProbe: probe);
        return new Harness(service, cloud, feeds, path);
    }

    private static ProtocolEnvelope Signed(
        Harness harness,
        string operation,
        object body,
        string? accessKeyId = null,
        string? secret = null,
        string deviceId = "observed-runtime-robot")
    {
        var account = harness.Cloud.GetAccount();
        var bodyText = JsonSerializer.Serialize(body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = "api.jibo.com",
            ["X-Amz-Date"] = "Sat, 03 Oct 2026 13:00:00 GMT",
            ["X-Amz-Target"] = "Loop_20160324." + operation
        };
        var authorization = Aws3Signature.BuildAuthorization(
            accessKeyId ?? account.AccessKeyId,
            secret ?? account.SecretAccessKey,
            "POST",
            headers,
            bodyText);
        headers["Authorization"] = authorization;
        return new ProtocolEnvelope
        {
            Method = "POST",
            HostName = "api.jibo.com",
            ServicePrefix = "Loop_20160324",
            Operation = operation,
            DeviceId = deviceId,
            BodyText = bodyText,
            Headers = headers
        };
    }

    private sealed record Harness(
        JiboCloudProtocolService Service,
        InMemoryCloudStateStore Cloud,
        InMemoryUserIntegrationStore Store,
        string Path) : IDisposable
    {
        public void Dispose()
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }

    private sealed class StubProbe : IMemberCalendarFeedProbe
    {
        public bool TryValidateHttpsPublicUrl(string? rawUrl, out string? error) =>
            IcalUrlValidator.TryValidateHttpsPublicUrl(rawUrl, out _, out error);

        public string? TryGetSafeHost(string? rawUrl) => IcalUrlValidator.TryGetSafeHost(rawUrl);

        public Task<MemberCalendarFeedProbeResult> ProbeAsync(string icalUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemberCalendarFeedProbeResult(true, null, 2, 1, ["Dentist"]));
    }
}
