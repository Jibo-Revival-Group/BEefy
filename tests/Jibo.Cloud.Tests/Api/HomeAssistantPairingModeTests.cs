using System.Text.Json;
using Jibo.Cloud.Api.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jibo.Cloud.Tests.Api;

public sealed class HomeAssistantPairingModeTests
{
    [Fact]
    public async Task LegacyHaSocket_ReturnsGoneWithRobotPairingInstructions()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await new HomeAssistantWebSocketHandler().HandleAsync(context);
        Assert.Equal(StatusCodes.Status410Gone, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("robot_pairing_required", json.RootElement.GetProperty("error").GetString());
        Assert.Contains("Yes/No", json.RootElement.GetProperty("message").GetString());
    }
}
