using Jibo.Cloud.Infrastructure.Audio;

namespace Jibo.Cloud.Api.Hosting;

public sealed class BeefyErWebSocketHandler(BeefyErOptions options, BeefyErBroker broker)
{
    public const string Path = "/beefyer/connect";

    public async Task HandleAsync(HttpContext context)
    {
        if (!options.Enabled) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        // Request.IsHttps includes only the forwarded scheme from trusted local proxies.
        if (!context.Request.IsHttps) { context.Response.StatusCode = StatusCodes.Status426UpgradeRequired; return; }
        if (!options.Authenticate(context.Request.Headers.Authorization.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await broker.RunAsync(socket, context.RequestAborted);
    }
}
