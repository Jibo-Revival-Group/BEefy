namespace Jibo.Cloud.Api.Hosting;

// Retained as a route tombstone so old integrations receive an explicit migration response.
internal sealed class HomeAssistantWebSocketHandler
{
    public HomeAssistantWebSocketHandler() { }

    internal async Task HandleAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status410Gone;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "robot_pairing_required",
            message = "BEefy supports Home Assistant pairing through the robot's Yes/No prompt. Select 5x1 or Self-Host BEefy in the integration."
        }, context.RequestAborted);
    }
}
