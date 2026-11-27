using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Calendar;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

/// <summary>Local-first robots (CONTEXT runtime.localFirst) keep people and report settings on the robot.</summary>
public sealed class LocalFirstTests : IDisposable
{
    private const string Robot = "Lf-Robot-Name";
    private static readonly string Scope = MemberCalendarFeedRecord.RobotScope(Robot);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"local-first-{Guid.NewGuid():N}");

    public LocalFirstTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    private const string LocalFirstJson =
        """{"calendarFeeds":[{"memberId":"p-ada","icalUrl":"https://calendar.example/ada.ics","memberName":"Ada","timeZone":"America/New_York"}],"commuteAddresses":[{"memberId":"home","address":"1 Local St","lat":42.1,"lng":-71.2,"city":"Localville","mode":null}],"newsTopics":[{"memberId":"p-ada","memberName":"Ada","disabledTopics":["sports","bogus"]}]}""";

    private static string Context(bool localFirst, string date = "2026-05-21T15:00:00-05:00") =>
        "{\"runtime\":{\"location\":{\"iso\":\"" + date + "\"},\"perception\":{\"speaker\":\"p-ada\",\"peoplePresent\":[{\"id\":\"p-ada\"}]}," +
        "\"loop\":{\"users\":[{\"id\":\"p-ada\",\"firstName\":\"Ada\",\"lastName\":\"Lovelace\",\"type\":\"owner\"},{\"id\":\"p-bo\",\"firstName\":\"Bo\"}]}" +
        (localFirst ? ",\"localFirst\":" + LocalFirstJson : "") + "}}";

    private static TurnContext Turn(string transcript, bool localFirst, string? date = null) => new()
    {
        RawTranscript = transcript,
        NormalizedTranscript = transcript,
        DeviceId = Robot,
        Attributes = new Dictionary<string, object?>
        {
            ["messageType"] = transcript.Length == 0 ? "TRIGGER" : "LISTEN",
            ["triggerSource"] = "PRESENCE",
            ["context"] = date is null ? Context(localFirst) : Context(localFirst, date)
        }
    };

    private InMemoryUserIntegrationStore OpenStore() =>
        new(new EncryptedUserDataSnapshotStore(Path.Combine(_dir, "user-integrations.json"), new UserDataEncryptionService()));

    private static JiboInteractionService Service(ICloudStateStore cloud, IUserIntegrationStore? store = null,
        IPersonalMemoryStore? memory = null) =>
        new(new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()), new FirstItemRandomizer(),
            memory ?? new InMemoryPersonalMemoryStore(), cloudStateStore: cloud, userIntegrationStore: store);

    [Fact]
    public void IsEnabled_OnlyForTheLocalFirstTag()
    {
        Assert.True(LocalFirst.IsEnabled(Turn("hi", true)));
        Assert.False(LocalFirst.IsEnabled(Turn("hi", false)));
        // Unlike BetaFeatures, a newer release alone doesn't make a robot local-first.
        Assert.False(LocalFirst.IsEnabled(new TurnContext { FirmwareVersion = "9.9.9", Attributes = new Dictionary<string, object?>() }));
        Assert.True(LocalFirst.IsInContext("{\"type\":\"CONTEXT\",\"data\":" + Context(true) + "}"));
        Assert.False(LocalFirst.IsInContext("{\"type\":\"CONTEXT\",\"data\":" + Context(false) + "}"));
    }

    [Fact]
    public async Task LocalFirstTurn_PersistsNoPeopleLoopPresenceOrObservations()
    {
        var cloud = new InMemoryCloudStateStore();
        var loops = cloud.GetLoops().Count;

        var decision = await Service(cloud).BuildDecisionAsync(Turn(string.Empty, true));

        Assert.Equal("proactive_greeting", decision.IntentName);
        Assert.DoesNotContain(cloud.GetPeople(), person => person.PersonId is "p-ada" or "p-bo");
        Assert.Equal(loops, cloud.GetLoops().Count);
        Assert.All(cloud.GetLoops(), loop => Assert.Empty(cloud.GetGreetingPresences(loop.LoopId)));
        Assert.Empty(cloud.GetGreetingPresences("openjibo-default-loop"));
        Assert.Empty(cloud.GetRecognitionObservations("openjibo-default-loop"));
    }

    [Fact]
    public async Task OrdinaryTurn_StillPersistsPeopleAndPresence()
    {
        var cloud = new InMemoryCloudStateStore();

        await Service(cloud).BuildDecisionAsync(Turn(string.Empty, false));

        Assert.Contains(cloud.GetPeople(), person => person.PersonId == "p-ada");
        Assert.Contains(cloud.GetGreetingPresences("openjibo-default-loop"), presence => presence.PersonId == "p-ada");
    }

    [Fact]
    public async Task LocalFirstTurn_WhatIsTheLoop_NamesTheTurnRoster()
    {
        var cloud = new InMemoryCloudStateStore();

        var decision = await Service(cloud).BuildDecisionAsync(Turn("what is the loop", true));

        Assert.Equal("robot_what_is_the_loop", decision.IntentName);
        Assert.Contains("my Loop is Ada Lovelace and Bo.", decision.ReplyText);
    }

    [Fact]
    public async Task LocalFirstTurn_BirthdayComesFromTheLoopNotCloudMemory()
    {
        var memory = new InMemoryPersonalMemoryStore();
        memory.SetBirthday(new PersonalMemoryTenantScope("acct-bday", "loop-bday", Robot, "p-ada"), "March 14");
        TurnContext BirthdayTurn(bool localFirst)
        {
            var turn = Turn(string.Empty, localFirst, "2026-03-14T09:00:00-05:00");
            turn.Attributes["accountId"] = "acct-bday";
            turn.Attributes["loopId"] = "loop-bday";
            return turn;
        }

        var ordinary = await Service(new InMemoryCloudStateStore(), memory: memory).BuildDecisionAsync(BirthdayTurn(false));
        var localFirst = await Service(new InMemoryCloudStateStore(), memory: memory).BuildDecisionAsync(BirthdayTurn(true));

        Assert.Equal("proactive_birthday_greeting", ordinary.IntentName);
        Assert.Equal("proactive_greeting", localFirst.IntentName);
    }

    [Fact]
    public async Task LocalFirstTurn_ReportSettingsComeFromTheTurn_AndNothingIsSaved()
    {
        var store = OpenStore();
        store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = Scope, MemberId = "home", Address = "Old Cloud St" });

        await Service(new InMemoryCloudStateStore(), store).BuildDecisionAsync(Turn("tell me a joke", true));

        Assert.Equal("1 Local St", store.GetCommuteAddresses(Scope).Single().Address);
        Assert.Equal(("Localville", 42.1, -71.2), (store.GetCommuteAddresses(Scope)[0].City, store.GetCommuteAddresses(Scope)[0].Latitude, store.GetCommuteAddresses(Scope)[0].Longitude));
        Assert.Equal(["sports"], store.GetNewsTopics(Scope).Single().DisabledTopics);
        var feed = store.GetMemberCalendarFeeds(Scope).Single();
        Assert.Equal(("https://calendar.example/ada.ics", "Ada", "America/New_York"), (feed.IcalUrl, feed.MemberName, feed.TimeZoneId));
        Assert.Equal(feed.IcalUrl, store.FindMemberCalendarFeed(Scope, "P-ADA")?.IcalUrl);

        // Writes for this robot change nothing (e.g. the report's feed sync status).
        Assert.Null(store.UpdateMemberCalendarFeedSyncStatus(Scope, "p-ada", DateTimeOffset.UtcNow, null));
        store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = Scope, MemberId = "home", Address = "Written St" });
        store.UpsertNewsTopics(new NewsTopicsRecord { Scope = Scope, MemberId = "p-ada", DisabledTopics = ["world"] });
        store.UpsertMemberCalendarFeed(Scope, "p-bo", "https://calendar.example/bo.ics");
        Assert.Null(store.ClearCommuteAddress(Scope, "home"));
        Assert.Equal("1 Local St", store.GetCommuteAddresses(Scope).Single().Address);

        // A refreshed turn replaces the settings, same feed id so the report's fetch cache holds.
        await Service(new InMemoryCloudStateStore(), store).BuildDecisionAsync(Turn("tell me a joke", true));
        Assert.Equal(feed.FeedId, store.GetMemberCalendarFeeds(Scope).Single().FeedId);

        var reopened = OpenStore(); // what's on disk
        Assert.Equal("Old Cloud St", reopened.GetCommuteAddresses(Scope).Single().Address);
        Assert.Empty(reopened.GetNewsTopics(Scope));
        Assert.Empty(reopened.GetMemberCalendarFeeds(Scope));
    }

    [Fact]
    public async Task OrdinaryTurn_ReadsAndWritesTheSavedSettingsAsBefore()
    {
        var store = OpenStore();
        store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = Scope, MemberId = "home", Address = "Old Cloud St" });

        await Service(new InMemoryCloudStateStore(), store).BuildDecisionAsync(Turn("tell me a joke", false));
        store.UpsertNewsTopics(new NewsTopicsRecord { Scope = Scope, MemberId = "p-ada", DisabledTopics = ["world"] });

        Assert.Equal("Old Cloud St", store.GetCommuteAddresses(Scope).Single().Address);
        Assert.Equal(["world"], OpenStore().GetNewsTopics(Scope).Single().DisabledTopics);
    }

    [Fact]
    public async Task Http_LocalFirstFlag_AnswersWithoutReadingOrSavingTheStore()
    {
        var store = OpenStore();
        store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = Scope, MemberId = "home", Address = "Old Cloud St" });
        store.UpsertNewsTopics(new NewsTopicsRecord { Scope = Scope, MemberId = "p-ada", DisabledTopics = ["world"] });
        var cloud = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(cloud, userIntegrationStore: store, commuteRouting: new FakeRouting(),
            calendarFeedProbe: new IcalMemberCalendarFeedProbe(null!));

        var set = await service.DispatchAsync(Signed(cloud, "SetCommuteAddress", new { memberId = "p-ada", address = "Work St", localFirst = true }));
        Assert.Equal(200, set.StatusCode);
        Assert.Contains("\"address\":\"Geocoded Work St\"", set.BodyText);
        Assert.Contains("\"city\":\"Testville\"", set.BodyText);

        var addresses = await service.DispatchAsync(Signed(cloud, "GetCommuteAddresses", new { localFirst = true }));
        Assert.Contains("\"addresses\":[]", addresses.BodyText);
        Assert.Contains("\"available\":true", addresses.BodyText);
        var topics = await service.DispatchAsync(Signed(cloud, "GetNewsTopics", new { localFirst = true }));
        Assert.Contains("\"people\":[]", topics.BodyText);
        Assert.Contains("\"sports\"", topics.BodyText);

        var feed = await service.DispatchAsync(Signed(cloud, "SetCalendarFeed",
            new { memberId = "p-ada", icalUrl = "https://calendar.example/ada.ics", localFirst = true }));
        Assert.Equal(200, feed.StatusCode);
        Assert.Contains("\"configured\":true", feed.BodyText);
        Assert.DoesNotContain("ada.ics", feed.BodyText);
        Assert.Equal(400, (await service.DispatchAsync(Signed(cloud, "SetCalendarFeed",
            new { memberId = "p-ada", icalUrl = "http://127.0.0.1/x.ics", localFirst = true }))).StatusCode);

        var disk = OpenStore();
        Assert.Equal("Old Cloud St", disk.GetCommuteAddresses(Scope).Single().Address);
        Assert.Empty(disk.GetMemberCalendarFeeds(Scope));

        // Without the flag (every other robot): unchanged.
        Assert.Contains("Old Cloud St", (await service.DispatchAsync(Signed(cloud, "GetCommuteAddresses", new { }))).BodyText);
        Assert.Contains("\"world\"", (await service.DispatchAsync(Signed(cloud, "GetNewsTopics", new { }))).BodyText);
    }

    private static ProtocolEnvelope Signed(InMemoryCloudStateStore cloud, string operation, object body)
    {
        var account = cloud.GetAccount();
        var bodyText = JsonSerializer.Serialize(body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = "api.jibo.com",
            ["X-Amz-Date"] = "Sat, 03 Oct 2026 13:00:00 GMT",
            ["X-Amz-Target"] = "Loop_20160324." + operation
        };
        headers["Authorization"] = Aws3Signature.BuildAuthorization(account.AccessKeyId, account.SecretAccessKey, "POST", headers, bodyText);
        return new ProtocolEnvelope
        {
            Method = "POST", HostName = "api.jibo.com", ServicePrefix = "Loop_20160324", Operation = operation,
            DeviceId = Robot, BodyText = bodyText, Headers = headers
        };
    }

    private sealed class FakeRouting : ICommuteRouting
    {
        public bool IsConfigured => true;
        public Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<GeocodedAddress?>(new GeocodedAddress("Geocoded " + address, 1, 2, "Testville", null, null, null, null));
        public Task<CommuteRoute?> RouteAsync(double fromLat, double fromLng, double toLat, double toLng, string mode,
            CancellationToken cancellationToken = default) => Task.FromResult<CommuteRoute?>(new CommuteRoute(60, 0));
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
