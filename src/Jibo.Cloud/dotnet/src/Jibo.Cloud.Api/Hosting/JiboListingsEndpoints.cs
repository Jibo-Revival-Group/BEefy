using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jibo.Cloud.Api.Hosting;

// buy.jiborevival.com reads its listings from here. The scraper runs on a home machine (eBay, Mercari and Facebook
// block datacenter IPs) and PUTs each run's results with the shared token; BEefy keeps the latest copy on disk.
public static class JiboListingsEndpoints
{
    private const int MaxBytes = 2_000_000;
    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    public static IEndpointRouteBuilder MapJiboListingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/jibo-listings", async (HttpContext context, IConfiguration configuration) =>
        {
            context.Response.Headers.CacheControl = "no-cache";
            var path = PathFor(configuration);
            return File.Exists(path)
                ? Results.Text(await File.ReadAllTextAsync(path, context.RequestAborted), "application/json", Encoding.UTF8)
                : Results.Json(new { updated = (string?)null, sources = new { }, listings = Array.Empty<object>() });
        });

        app.MapPut("/jibo-listings", async (HttpContext context, IConfiguration configuration) =>
        {
            var token = configuration["OpenJibo:JiboListings:Token"];
            if (string.IsNullOrWhiteSpace(token)) return Results.NotFound();
            if (!context.Request.IsHttps) return Results.StatusCode(426);
            var given = context.Request.Headers.Authorization.ToString();
            var expected = "Bearer " + token;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
                return Results.Unauthorized();
            if (context.Request.ContentLength > MaxBytes) return Results.StatusCode(413);

            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            if (body.Length > MaxBytes) return Results.StatusCode(413);
            try
            {
                using var document = JsonDocument.Parse(body.ToArray());
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
                    return Results.BadRequest(new { error = "listings_array_required" });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid_json" }); }

            var path = PathFor(configuration);
            await WriteLock.WaitAsync(context.RequestAborted);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                var temp = path + ".tmp";
                await File.WriteAllBytesAsync(temp, body.ToArray(), context.RequestAborted);
                File.Move(temp, path, overwrite: true); // readers never see a half-written file
            }
            finally { WriteLock.Release(); }
            return Results.Ok(new { ok = true });
        });
        return app;
    }

    private static string PathFor(IConfiguration configuration) =>
        configuration["OpenJibo:JiboListings:Path"] ??
        Path.Combine(Path.GetDirectoryName(configuration["OpenJibo:State:PersistencePath"] ?? "App_Data/cloud-state.json") ?? "App_Data", "jibo-listings.json");
}
