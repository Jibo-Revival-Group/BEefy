using System.Net;
using System.Text;
using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Commute;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class TomTomCommuteTests
{
    private const string Robot = "Auburn-Motor-Salad-Angora";

    [Fact]
    public async Task Routing_ParsesTomTomGeocodeAndLiveTrafficRoute()
    {
        var urls = new List<string>();
        var handler = new StubHandler(url =>
        {
            urls.Add(url);
            return url.Contains("/geocode/")
                ? """{"results":[{"position":{"lat":42.66,"lon":-71.42},"address":{"freeformAddress":"1 Main St, Tyngsborough, MA 01879","municipality":"Tyngsborough","countrySubdivisionName":"Massachusetts","countrySubdivisionCode":"MA","country":"United States","countryCode":"US"}}]}"""
                : """{"routes":[{"summary":{"travelTimeInSeconds":1530,"trafficDelayInSeconds":420}}]}""";
        });
        Environment.SetEnvironmentVariable("TOMTOM_API_KEY", "test-key");
        try
        {
            var routing = new TomTomCommuteRouting(new HttpClient(handler), NullLogger<TomTomCommuteRouting>.Instance);
            var home = await routing.GeocodeAsync("1 main st tyngsborough");
            var route = await routing.RouteAsync(42.66, -71.42, 42.36, -71.06, "driving");

            Assert.Equal(new GeocodedAddress("1 Main St, Tyngsborough, MA 01879", 42.66, -71.42, "Tyngsborough",
                "Massachusetts", "MA", "United States", "US"), home);
            Assert.Equal(new CommuteRoute(1530, 420), route);
            Assert.Contains("/calculateRoute/42.66,-71.42:42.36,-71.06/json?traffic=true&departAt=now&travelMode=car", urls[1]);
        }
        finally { Environment.SetEnvironmentVariable("TOMTOM_API_KEY", null); }
    }

    [Fact]
    public async Task Provider_UsesLiveTravelTimeFromHomeToTheSpeakersJob()
    {
        using var store = new TempStore();
        var scope = MemberCalendarFeedRecord.RobotScope(Robot);
        store.Store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = scope, MemberId = CommuteAddressRecord.HomeMemberId, Address = "home", Latitude = 1, Longitude = 1, TimeZoneId = "America/New_York" });
        store.Store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = scope, MemberId = "zane", Address = "zane job", Latitude = 2, Longitude = 2, MemberName = "Zane Vincent" });
        store.Store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = scope, MemberId = "zoe", Address = "zoe job", Latitude = 3, Longitude = 3, MemberName = "Zoe Vincent" });
        var routing = new FakeRouting();
        var provider = new TomTomCommuteReportProvider(store.Store, routing, new InMemoryCloudStateStore(), new NullProvider());

        var report = await provider.GetReportAsync(new TurnContext
        {
            DeviceId = Robot,
            Attributes = new Dictionary<string, object?> { ["personalReportUserName"] = "zoe" }
        });

        Assert.Equal((1d, 1d, 3d, 3d), routing.Last);
        Assert.Equal(26, report!.DurationMinutes); // 1530 s rounds up
        Assert.Equal(7, report.ExtraMinutes); // traffic delay
        Assert.Null(await provider.GetReportAsync(new TurnContext { DeviceId = "other-robot" })); // falls back
    }

    [Fact]
    public async Task Protocol_GeocodesAndSavesAddressesPerRobot()
    {
        using var store = new TempStore();
        var cloud = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(cloud, userIntegrationStore: store.Store, commuteRouting: new FakeRouting());

        var set = await service.DispatchAsync(Signed(cloud, "SetCommuteAddress", new { memberId = "zane", address = "Work St", memberName = "Zane" }));
        var home = await service.DispatchAsync(Signed(cloud, "SetCommuteAddress", new { address = "Home St", timeZone = "America/New_York" }));
        var list = await service.DispatchAsync(Signed(cloud, "GetCommuteAddresses", new { }));

        Assert.Equal(200, set.StatusCode);
        Assert.Contains("\"city\":\"Testville\"", home.BodyText);
        var saved = store.Store.GetCommuteAddresses(MemberCalendarFeedRecord.RobotScope(Robot));
        Assert.Equal(["Geocoded Home St", "Geocoded Work St"], saved.Select(item => item.Address).Order().ToArray());
        Assert.Equal("America/New_York", saved.Single(item => item.MemberId == "home").TimeZoneId);
        Assert.Equal("Testville", saved.Single(item => item.MemberId == "home").City); // weather's name for home
        Assert.Contains("Geocoded Work St", list.BodyText);
    }

    [Fact]
    public async Task Provider_UsesTheSpeakersOwnHome_WhenBEaconHasOne()
    {
        using var store = new TempStore();
        var scope = MemberCalendarFeedRecord.RobotScope(Robot);
        store.Store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = scope, MemberId = CommuteAddressRecord.HomeMemberId, Address = "home", Latitude = 1, Longitude = 1 });
        store.Store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = scope, MemberId = CommuteAddressRecord.HomeOf("zoe"), Address = "dorm", Latitude = 5, Longitude = 5 });
        store.Store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = scope, MemberId = "zoe", Address = "school", Latitude = 3, Longitude = 3, MemberName = "Zoe Vincent" });
        store.Store.UpsertCommuteAddress(new CommuteAddressRecord { Scope = scope, MemberId = "zane", Address = "job", Latitude = 2, Longitude = 2, MemberName = "Zane Vincent" });
        var routing = new FakeRouting();
        var provider = new TomTomCommuteReportProvider(store.Store, routing, new InMemoryCloudStateStore(), new NullProvider());

        await provider.GetReportAsync(Speaker("zoe"));
        Assert.Equal((5d, 5d, 3d, 3d), routing.Last); // her own home, not the household's (and not a "job")
        await provider.GetReportAsync(Speaker("zane"));
        Assert.Equal((1d, 1d, 2d, 2d), routing.Last); // no home of his own: the household's

        static TurnContext Speaker(string name) => new()
        {
            DeviceId = Robot, Attributes = new Dictionary<string, object?> { ["personalReportUserName"] = name }
        };
    }

    [Fact]
    public async Task NewsTopics_SavedPerPersonInBEacon_DecideTheSpeakersBriefingTopics()
    {
        using var store = new TempStore();
        var cloud = new InMemoryCloudStateStore();
        var protocol = new JiboCloudProtocolService(cloud, userIntegrationStore: store.Store, commuteRouting: new FakeRouting());
        var set = await protocol.DispatchAsync(Signed(cloud, "SetNewsTopics",
            new { memberId = "zoe", memberName = "Zoe Vincent", disabledTopics = new[] { "sports", "Health", "not-a-topic" } }));
        await protocol.DispatchAsync(Signed(cloud, "SetNewsTopics", new { memberId = "zane", memberName = "Zane", disabledTopics = new[] { "general" } }));
        var list = await protocol.DispatchAsync(Signed(cloud, "GetNewsTopics", new { }));
        Assert.Equal(200, set.StatusCode);
        Assert.Contains("\"disabledTopics\":[\"sports\",\"health\"]", list.BodyText);

        var news = new CapturingNews();
        var service = new JiboInteractionService(new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstItemRandomizer(), new InMemoryPersonalMemoryStore(), newsBriefingProvider: news, userIntegrationStore: store.Store);
        Task Ask(string text, string? speaker) => service.BuildDecisionAsync(new TurnContext
        {
            RawTranscript = text, NormalizedTranscript = text, DeviceId = Robot,
            Attributes = speaker is null ? new Dictionary<string, object?>() : new Dictionary<string, object?> { ["userName"] = speaker }
        });

        await Ask("tell me the news", "Zoe");
        Assert.Equal(["general", "world", "nation", "business", "technology", "entertainment", "science"], news.Last!.PreferredCategories);
        await Ask("tell me the science news", "Zoe"); // asking for a topic still works
        Assert.Equal(["science"], news.Last!.PreferredCategories);
        await Ask("tell me the news", "Someone Else"); // two people set topics, and this isn't either of them
        Assert.Empty(news.Last!.PreferredCategories);
    }

    private sealed class CapturingNews : INewsBriefingProvider
    {
        public NewsBriefingRequest? Last;
        public Task<NewsBriefingSnapshot?> GetBriefingAsync(NewsBriefingRequest request, CancellationToken cancellationToken = default)
        {
            Last = request;
            return Task.FromResult<NewsBriefingSnapshot?>(null);
        }
    }

    private sealed class FirstItemRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
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
        public (double, double, double, double) Last;
        public bool IsConfigured => true;
        public Task<GeocodedAddress?> GeocodeAsync(string address, CancellationToken cancellationToken = default) =>
            Task.FromResult<GeocodedAddress?>(new GeocodedAddress("Geocoded " + address, 1, 2, "Testville", null, null, null, null));
        public Task<CommuteRoute?> RouteAsync(double fromLat, double fromLng, double toLat, double toLng, string mode,
            CancellationToken cancellationToken = default)
        {
            Last = (fromLat, fromLng, toLat, toLng);
            return Task.FromResult<CommuteRoute?>(new CommuteRoute(1530, 420));
        }
    }

    private sealed class NullProvider : ICommuteReportProvider
    {
        public Task<CommuteReportSnapshot?> GetReportAsync(TurnContext turn, CancellationToken cancellationToken = default) =>
            Task.FromResult<CommuteReportSnapshot?>(null);
    }

    private sealed class StubHandler(Func<string, string> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(respond(request.RequestUri!.ToString()), Encoding.UTF8, "application/json")
            });
    }

    private sealed class TempStore : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"commute-{Guid.NewGuid():N}.json");
        public TempStore() => Store = new InMemoryUserIntegrationStore(new EncryptedUserDataSnapshotStore(_path, new UserDataEncryptionService()));
        public InMemoryUserIntegrationStore Store { get; }
        public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }
    }
}
