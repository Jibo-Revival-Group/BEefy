using System.Text;
using System.Text.Json;
using Jibo.Cloud.Infrastructure.Audio;
using Jibo.Cloud.Infrastructure.Agent;

namespace Jibo.Cloud.Api.Hosting;

public static class FreeAgentEndpoints
{
    public static IServiceCollection AddFreeAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(provider => provider.GetRequiredService<IConfiguration>().GetSection("OpenJibo:Agent:Free").Get<FreeAgentOptions>() ?? new());
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<FreeAgentReplayStore>();
        services.AddSingleton<FreeAgentRateLimiter>();
        return services;
    }
    public static IApplicationBuilder UseFreeAgentTransportGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api/agent/free", StringComparison.OrdinalIgnoreCase))
        {
            // This namespace must never fall through to legacy protocol capture or token auth.
            if (!context.Request.Path.Value!.TrimEnd('/').Equals("/api/agent/free/chat", StringComparison.OrdinalIgnoreCase))
            { await Error("invalid_request", 404).ExecuteAsync(context); return; }
            if (!HttpMethods.IsPost(context.Request.Method))
            { await Error("invalid_request", 405).ExecuteAsync(context); return; }
            if (context.Connection.RemoteIpAddress is null)
            { await Error("free_unauthorized", 403).ExecuteAsync(context); return; }
        }
        await next();
    });

    public static IEndpointRouteBuilder MapFreeAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/agent/free/chat", HandleAsync);
        return app;
    }
    private static async Task<IResult> HandleAsync(HttpContext context, FreeAgentOptions options,
        BeefyErBroker broker, FreeAgentReplayStore replay, FreeAgentRateLimiter limiter)
    {
        if (!options.Enabled) return Error("free_unavailable", 503);
        if (!context.Request.IsHttps) return Error("free_unauthorized", 403);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        try
        {
            if (context.Request.ContentLength > 32000) return Error("invalid_request", 413);
            using var stream = new MemoryStream();
            var buffer = new byte[4096];
            int count;
            while ((count = await context.Request.Body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (stream.Length + count > 32000) return Error("invalid_request", 413);
                stream.Write(buffer, 0, count);
            }
            var body = new UTF8Encoding(false, true).GetString(stream.GetBuffer(), 0, (int)stream.Length);
            var request = FreeAgentRequest.Parse(body);
            if (request is null) return Error("invalid_request", 400);
            if (BeefyErBroker.ChatEnvelope(request.Messages, new string('0', 32), new string('0', 32)).Length > 32000)
                return Error("invalid_request", 413);
            if (context.Connection.RemoteIpAddress is not { } ip) return Error("free_unauthorized", 403);
            if (!limiter.TryAcquire(request.RobotId, ip, out var retryAfter))
            {
                context.Response.Headers.RetryAfter = retryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Error("free_rate_limited", 429);
            }
            // Rate-limit before allocating replay tombstones so anonymous request
            // floods cannot fill the bounded global replay table without quota cost.
            if (!replay.TryReserve((ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString(), request.RequestId, out var duplicate))
                return duplicate ? Error("invalid_request", 400) : Error("free_busy", 503);
            var text = await broker.ChatAsync(request.Messages, deadline.Token, TimeSpan.FromSeconds(120));
            return Results.Json(new { requestId = request.RequestId, text, model = FreeAgentOptions.Model });
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException) { return Error("invalid_request", 400); }
        catch (FreeAgentRelayException ex) { return Error(ex.Code, ex.Status); }
        catch (OperationCanceledException) { return Error("free_timeout", 504); }
        catch (BadHttpRequestException ex) { return Error("invalid_request", ex.StatusCode == 413 ? 413 : 400); }
        // Never let dependency/transport exceptions reach global exception logging with input attached.
        catch (Exception) { return Error("free_unavailable", 503); }
    }
    private static IResult Error(string code, int status) => Results.Json(new { error = code }, statusCode: status);
}
