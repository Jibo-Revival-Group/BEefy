using System.Text.Json;
using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Api.Hosting;

public static class HomeAssistantRobotEndpoints
{
    public static WebApplication MapHomeAssistantRobotEndpoints(this WebApplication app)
    {
        app.MapPost(HomeAssistantRobotRelay.CallbackPath, async (
            HttpRequest request,
            HomeAssistantRobotRelay relay,
            CancellationToken cancellationToken) =>
        {
            JsonDocument document;
            try
            {
                document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "invalid_json" });
            }

            using (document)
            {
                var root = document.RootElement;
                var token = root.TryGetProperty("callbackToken", out var tokenElement)
                    ? tokenElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(token))
                    return Results.BadRequest(new { error = "missing_callback_token" });

                var result = HomeAssistantCommandResult.FromJson(root);
                if (!relay.TryComplete(token, result))
                    return Results.NotFound(new { error = "unknown_callback" });

                return Results.Ok(new { ok = true });
            }
        });

        return app;
    }
}
