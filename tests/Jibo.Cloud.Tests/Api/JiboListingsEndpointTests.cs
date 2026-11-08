using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;

namespace Jibo.Cloud.Tests.Api;

public sealed class JiboListingsEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jibo-listings-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private WebApplicationFactory<Program> Factory(string? token = "test-token") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OpenJibo:JiboListings:Path"] = Path.Combine(_dir, "jibo-listings.json"),
                ["OpenJibo:JiboListings:Token"] = token,
                ["OpenJibo:Stt:EnableStreamingSherpa"] = "false",
            }));
        });

    private static HttpRequestMessage Put(string body, string? auth = "Bearer test-token")
    {
        var request = new HttpRequestMessage(HttpMethod.Put, "/jibo-listings") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (auth is not null) request.Headers.TryAddWithoutValidation("Authorization", auth);
        return request;
    }

    [Fact]
    public async Task ServesTheLatestUploadFromTheScraper()
    {
        await using var factory = Factory();
        var client = FreeAgentEndpointTests.Client(factory);

        var empty = await client.GetAsync("/jibo-listings");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        Assert.Contains("\"listings\":[]", await empty.Content.ReadAsStringAsync());

        const string run = "{\"updated\":\"2026-11-08T23:37:19.702Z\",\"sources\":{},\"listings\":[{\"id\":\"ebay:1\",\"title\":\"Jibo Social Robot\"}]}";
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put(run))).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/jibo-listings");
        request.Headers.Add("Origin", "https://buy.jiborevival.com");
        var served = await client.SendAsync(request);
        Assert.Equal(run, await served.Content.ReadAsStringAsync());
        Assert.Equal("application/json", served.Content.Headers.ContentType?.MediaType);
        Assert.Equal("*", served.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.False(File.Exists(Path.Combine(_dir, "jibo-listings.json.tmp")));
    }

    [Theory]
    [InlineData(null, 401)]
    [InlineData("Bearer wrong", 401)]
    [InlineData("test-token", 401)]
    public async Task RejectsUploadsWithoutTheToken(string? auth, int status)
    {
        await using var factory = Factory();
        var client = FreeAgentEndpointTests.Client(factory);
        Assert.Equal(status, (int)(await client.SendAsync(Put("{\"listings\":[]}", auth))).StatusCode);
        Assert.False(File.Exists(Path.Combine(_dir, "jibo-listings.json")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"listings\":{}}")]
    public async Task RejectsMalformedUploadsAndKeepsThePreviousCopy(string body)
    {
        await using var factory = Factory();
        var client = FreeAgentEndpointTests.Client(factory);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Put("{\"listings\":[{\"id\":\"a\"}]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Put(body))).StatusCode);
        Assert.Contains("\"id\":\"a\"", await (await client.GetAsync("/jibo-listings")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task UploadsNeedHttpsAndAConfiguredTokenAndAreSizeCapped()
    {
        await using (var factory = Factory())
        {
            Assert.Equal(426, (int)(await FreeAgentEndpointTests.Client(factory, https: false).SendAsync(Put("{\"listings\":[]}"))).StatusCode);
            var big = "{\"listings\":[\"" + new string('x', 2_000_001) + "\"]}";
            Assert.Equal(413, (int)(await FreeAgentEndpointTests.Client(factory).SendAsync(Put(big))).StatusCode);
        }
        await using (var disabled = Factory(token: null))
            Assert.Equal(HttpStatusCode.NotFound, (await FreeAgentEndpointTests.Client(disabled).SendAsync(Put("{\"listings\":[]}"))).StatusCode);
    }
}
