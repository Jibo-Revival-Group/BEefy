using System.Net;
using System.Text;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.News;
using Jibo.Cloud.Infrastructure.Weather;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class ProviderCachingTests
{
    [Fact]
    public async Task OpenWeatherReportProvider_ReusesCachedWeatherAndGeocodeResponses()
    {
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/geo/1.0/direct" => JsonResponse(
                    """[{"name":"Boston","state":"Massachusetts","country":"US","lat":42.3601,"lon":-71.0589}]"""),
                "/data/3.0/onecall" => JsonResponse(
                    """
                    {
                        "lat":42.3601,
                        "lon":-71.0589,
                        "timezone":"America/New_York",
                        "current":{"dt":1710000000,"temp":70.2,"weather":[{"main":"Clouds","description":"overcast clouds"}]},
                        "daily":[
                            {"dt":1710000000,"temp":{"day":70.2,"min":66.1,"max":72.9},"weather":[{"main":"Clouds","description":"overcast clouds"}]}
                        ]
                    }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = new OpenWeatherReportProvider(
            new HttpClient(handler),
            new OpenWeatherOptions
            {
                ApiKey = "test-key",
                // Success weather entries expire at the next UTC half-hour; these TTL
                // options no longer control successful payload lifetime.
                CurrentCacheTtlSeconds = 1,
                GeocodeCacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<OpenWeatherReportProvider>.Instance);

        var request = new WeatherReportRequest("Boston,US", null, null, false, false, 0);
        var first = await provider.GetReportAsync(request);
        var second = await provider.GetReportAsync(request);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, handler.GetCallCount("/geo/1.0/direct"));
        Assert.Equal(1, handler.GetCallCount("/data/3.0/onecall"));
    }

    [Fact]
    public async Task OpenWeatherReportProvider_UsesCurrentHiLoForCurrentDay_WhenCurrentBandDiffers()
    {
        var offset = TimeSpan.FromHours(-5);
        var localNow = DateTimeOffset.UtcNow.ToOffset(offset);
        var todayNoon = new DateTimeOffset(localNow.Date.AddHours(12), offset);
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/geo/1.0/direct" => JsonResponse(
                    """[{"name":"Lone Jack","country":"US","lat":38.8708,"lon":-94.1733}]"""),
                "/data/3.0/onecall" => JsonResponse(
                    """
                    {
                        "lat":38.8708,
                        "lon":-94.1733,
                        "timezone":"America/Chicago",
                        "current":{"dt":1710000000,"temp":83.0,"weather":[{"main":"Clouds","description":"overcast clouds"}]},
                        "daily":[
                            {"dt":1710000000,"temp":{"day":83.0,"min":82.0,"max":83.0},"weather":[{"main":"Clouds","description":"overcast clouds"}]}
                        ]
                    }
                    """),
                "/data/2.5/forecast" => JsonResponse(
                    BuildForecastResponseJson(
                        "Lone Jack",
                        "US",
                        -18000,
                        [(todayNoon, 82, 82, 78, "Clouds", "overcast clouds")])),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = new OpenWeatherReportProvider(
            new HttpClient(handler),
            new OpenWeatherOptions
            {
                ApiKey = "test-key",
                CurrentCacheTtlSeconds = 300,
                ForecastCacheTtlSeconds = 300,
                GeocodeCacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<OpenWeatherReportProvider>.Instance);

        var report =
            await provider.GetReportAsync(new WeatherReportRequest("Lone Jack,US", null, null, false, false, 0));

        Assert.NotNull(report);
        Assert.Equal(83, report.Temperature);
        Assert.Equal(82, report.HighTemperature);
        Assert.Equal(78, report.LowTemperature);
        Assert.Equal(1, handler.GetCallCount("/data/3.0/onecall"));
        Assert.Equal(1, handler.GetCallCount("/data/2.5/forecast"));
    }

    [Fact]
    public async Task OpenWeatherReportProvider_UsesOneCallDailyForecastForTomorrow()
    {
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/geo/1.0/direct" => JsonResponse(
                    """[{"name":"Chicago","country":"US","lat":41.8781,"lon":-87.6298}]"""),
                "/data/3.0/onecall" => JsonResponse(
                    """
                    {
                        "lat":41.8781,
                        "lon":-87.6298,
                        "timezone":"America/Chicago",
                        "current":{"dt":1710000000,"temp":61.0,"weather":[{"main":"Clouds","description":"scattered clouds"}]},
                        "daily":[
                            {"dt":1710000000,"temp":{"day":61.0,"min":55.0,"max":66.0},"weather":[{"main":"Clouds","description":"scattered clouds"}]},
                            {"dt":1710086400,"temp":{"day":74.0,"min":60.0,"max":76.0},"summary":"A warmer day ahead","weather":[{"main":"Rain","description":"light rain"}]}
                        ]
                    }
                    """),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = new OpenWeatherReportProvider(
            new HttpClient(handler),
            new OpenWeatherOptions
            {
                ApiKey = "test-key",
                CurrentCacheTtlSeconds = 300,
                ForecastCacheTtlSeconds = 300,
                GeocodeCacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<OpenWeatherReportProvider>.Instance);

        var report = await provider.GetReportAsync(new WeatherReportRequest("Chicago,US", null, null, true, false, 1));

        Assert.NotNull(report);
        Assert.Equal(74, report.Temperature);
        Assert.Equal(76, report.HighTemperature);
        Assert.Equal(60, report.LowTemperature);
        Assert.Equal(1, handler.GetCallCount("/geo/1.0/direct"));
        Assert.Equal(1, handler.GetCallCount("/data/3.0/onecall"));
    }

    [Fact]
    public async Task OpenWeatherReportProvider_FallsBackToLegacyWeatherWhenOneCallIsUnauthorized()
    {
        var offset = TimeSpan.FromHours(-5);
        var localNow = DateTimeOffset.UtcNow.ToOffset(offset);
        var todayNoon = new DateTimeOffset(localNow.Date.AddHours(12), offset);
        var handler = new CountingHttpMessageHandler(message =>
        {
            var path = message.RequestUri?.AbsolutePath ?? string.Empty;
            return path switch
            {
                "/geo/1.0/direct" => JsonResponse(
                    """[{"name":"Boston","state":"Massachusetts","country":"US","lat":42.3601,"lon":-71.0589}]"""),
                "/data/3.0/onecall" => new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(
                        """{"cod":401,"message":"One Call 3.0 requires a subscription"}""",
                        Encoding.UTF8,
                        "application/json")
                },
                "/data/2.5/weather" => JsonResponse(
                    """{"name":"Boston","weather":[{"main":"Clouds","description":"overcast clouds"}],"main":{"temp":70.0,"temp_max":83.0,"temp_min":82.0}}"""),
                "/data/2.5/forecast" => JsonResponse(
                    BuildForecastResponseJson(
                        "Boston",
                        "US",
                        -18000,
                        [(todayNoon, 70, 72, 66, "Clouds", "overcast clouds")])),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });
        var provider = new OpenWeatherReportProvider(
            new HttpClient(handler),
            new OpenWeatherOptions
            {
                ApiKey = "test-key",
                CurrentCacheTtlSeconds = 300,
                ForecastCacheTtlSeconds = 300,
                GeocodeCacheTtlSeconds = 300,
                FailureCacheTtlSeconds = 30
            },
            NullLogger<OpenWeatherReportProvider>.Instance);

        var report = await provider.GetReportAsync(new WeatherReportRequest("Boston,US", null, null, false, false, 0));

        Assert.NotNull(report);
        Assert.Equal(70, report.Temperature);
        Assert.Equal(72, report.HighTemperature);
        Assert.Equal(66, report.LowTemperature);
        Assert.Equal(1, handler.GetCallCount("/data/3.0/onecall"));
        Assert.Equal(1, handler.GetCallCount("/data/2.5/weather"));
        Assert.Equal(1, handler.GetCallCount("/data/2.5/forecast"));
    }

    private static string GoogleNewsFeed(params string[] titles) =>
        "<rss><channel>" + string.Concat(titles.Select(title =>
            $"<item><title>{System.Security.SecurityElement.Escape(title)} - AP News</title><link>https://news.google.com/x</link><source url=\"https://apnews.com\">AP News</source></item>")) +
        "</channel></rss>";

    private static (GoogleNewsBriefingProvider Provider, CountingHttpMessageHandler Handler) GoogleNews(
        Func<string, HttpResponseMessage> feedForPath, bool feedRedirect = false)
    {
        var handler = new CountingHttpMessageHandler(message =>
        {
            var response = feedForPath(message.RequestUri?.AbsolutePath ?? "");
            // Like Google: the topic feed lands on /rss/topics/<id>, whose web page is /topics/<id>.
            if (feedRedirect && message.RequestUri!.AbsolutePath.StartsWith("/rss/headlines/section/topic/"))
                message.RequestUri = new Uri(message.RequestUri.ToString().Replace("/rss/headlines/section/topic/", "/rss/topics/"));
            response.RequestMessage = message;
            return response;
        });
        return (new GoogleNewsBriefingProvider(new HttpClient(handler), new GoogleNewsOptions(),
            NullLogger<GoogleNewsBriefingProvider>.Instance), handler);
    }

    private static HttpResponseMessage Xml(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/rss+xml") };

    [Fact]
    public async Task GoogleNewsBriefingProvider_TakesTheTopHeadlineOfEachDefaultCategory()
    {
        var (provider, handler) = GoogleNews(path => Xml(path switch
        {
            "/rss/headlines/section/topic/TECHNOLOGY" => GoogleNewsFeed("Tech one is the top technology story", "Tech two"),
            "/rss/headlines/section/topic/SCIENCE" => GoogleNewsFeed("Science one is the top science story", "Science two"),
            "/rss/headlines/section/topic/SPORTS" => GoogleNewsFeed("Sports one is the top sports story", "Sports two"),
            _ => GoogleNewsFeed("Unexpected feed")
        }), feedRedirect: true);

        var result = await provider.GetBriefingAsync(new NewsBriefingRequest([]));
        await provider.GetBriefingAsync(new NewsBriefingRequest([]));

        Assert.NotNull(result);
        Assert.Equal("success", result.ProviderStatus);
        Assert.Equal(
            ["Tech one is the top technology story", "Science one is the top science story", "Sports one is the top sports story"],
            result.Headlines.Select(headline => headline.Title));
        Assert.Equal(["technology", "science", "sports"], result.Headlines.Select(headline => headline.Category));
        Assert.All(result.Headlines, headline => Assert.Equal("AP News", headline.SourceName));
        Assert.Equal(1, handler.GetCallCount("/rss/headlines/section/topic/SPORTS")); // cached
    }

    [Fact]
    public async Task GoogleNewsBriefingProvider_FillsFromOneAskedCategory_AndSkipsUnsafeHeadlines()
    {
        var (provider, _) = GoogleNews(path => Xml(path == "/rss/headlines/section/topic/SPORTS"
            ? GoogleNewsFeed("Correction: an earlier score", "Fan arrested after stadium murder", "Bills beat the Vikings", "Celtics win again", "Red Sox sign a pitcher", "Spare")
            : GoogleNewsFeed("Wrong feed")));

        var result = await provider.GetBriefingAsync(new NewsBriefingRequest(["sports"]));

        Assert.NotNull(result);
        Assert.Equal(["Bills beat the Vikings", "Celtics win again", "Red Sox sign a pitcher"],
            result.Headlines.Select(headline => headline.Title));
    }

    [Fact]
    public async Task GoogleNewsBriefingProvider_AttachesEachStorysPhoto_FromTheTopicPage_PreferringStoriesWithPhotos()
    {
        // Smallest JPEG prefix with a SOF0 frame header: 640 wide, 360 high.
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0x68, 0x02, 0x80, 0x03];
        const string page = """
            <img class="Quavad vwBmvb" srcset="x" alt="" src="/api/attachments/LAB=-w280-h168-p-df-rw" />
            <button aria-label="More - Robotics lab opens">
            <img class="Quavad vwBmvb" src="/api/attachments/WIN=-w280-h168-p-df-rw" />
            <button aria-label="More - Students win the coding cup">
            """;
        var (provider, _) = GoogleNews(path => path switch
        {
            "/rss/headlines/section/topic/SCIENCE" => Xml(GoogleNewsFeed("A story with no photo at all", "Robotics lab opens", "Students win the coding cup")),
            "/topics/SCIENCE" => Xml(page),
            "/api/attachments/LAB=-w1280-h720-p-df" or "/api/attachments/WIN=-w1280-h720-p-df" =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        }, feedRedirect: true);

        var result = await provider.GetBriefingAsync(new NewsBriefingRequest(["science"]));

        Assert.NotNull(result);
        // Like the stock skill, stories without a photo are left out while the category has photo stories.
        Assert.Equal(["Robotics lab opens", "Students win the coding cup"], result.Headlines.Select(headline => headline.Title));
        Assert.Equal(new NewsImage("https://news.google.com/api/attachments/LAB=-w1280-h720-p-df", 640, 360), result.Headlines[0].Image);
    }

    [Fact]
    public async Task GoogleNewsBriefingProvider_ReportsHttpError_WhenEveryFeedFails()
    {
        var (provider, _) = GoogleNews(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var result = await provider.GetBriefingAsync(new NewsBriefingRequest(["general"]));

        Assert.NotNull(result);
        Assert.Empty(result.Headlines);
        Assert.Equal("http_error", result.ProviderStatus);
        Assert.Equal(503, result.ProviderHttpStatusCode);
        Assert.StartsWith("https://news.google.com/rss?", result.ProviderEndpoint);
    }

    private static HttpResponseMessage JsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static string BuildForecastResponseJson(
        string cityName,
        string country,
        int timezoneSeconds,
        IReadOnlyList<(DateTimeOffset Timestamp, int Temp, int High, int Low, string Main, string Description)> entries)
    {
        var list = string.Join(
            ",",
            entries.Select(entry =>
                $$"""{"dt":{{entry.Timestamp.ToUnixTimeSeconds()}},"main":{"temp":{{entry.Temp}},"temp_min":{{entry.Low}},"temp_max":{{entry.High}}},"weather":[{"main":"{{entry.Main}}","description":"{{entry.Description}}"}]}"""));

        return
            $$"""{"city":{"name":"{{cityName}}","country":"{{country}}","timezone":{{timezoneSeconds}}},"list":[{{list}}]}""";
    }

    private sealed class CountingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _callsByPath = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _gate = new();

        public int GetCallCount(string path)
        {
            lock (_gate)
            {
                return _callsByPath.GetValueOrDefault(path, 0);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            lock (_gate)
            {
                _callsByPath[path] = _callsByPath.TryGetValue(path, out var count) ? count + 1 : 1;
            }

            return Task.FromResult(responseFactory(request));
        }
    }
}