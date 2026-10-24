using Jibo.Cloud.Application.Services;

namespace Jibo.Cloud.Tests.Application;

public sealed class HomeAssistantEntityCommandParserTests
{
    [Theory]
    [InlineData("set the bedroom lights blue", "light", "color", "area", "bedroom", "blue")]
    [InlineData("set the lights to 40 percent", "light", "brightness", "room", null, "40")]
    [InlineData("set the desk lamp to 42.5%", "light", "brightness", "named", "desk", "42.5")]
    [InlineData("set the lights to warm white", "light", "color_temperature", "room", null, "warm white")]
    [InlineData("set the lights to cool white", "light", "color_temperature", "room", null, "cool white")]
    [InlineData("set the lights to 3000 kelvin", "light", "color_temperature", "room", null, "3000")]
    [InlineData("set the thermostat to heat", "climate", "hvac_mode", "room", null, "heat")]
    [InlineData("turn off the thermostat", "climate", "hvac_mode", "room", null, "off")]
    [InlineData("set the bedroom thermostat to 20.5 celsius", "climate", "temperature", "named", "bedroom", "20.5")]
    [InlineData("turn on the fan", "fan", "turn_on", "room", null, null)]
    [InlineData("turn the desk fan off", "fan", "turn_off", "named", "desk", null)]
    [InlineData("set the office fan to 50 percent", "fan", "percentage", "named", "office", "50")]
    [InlineData("turn on the desk switch", "switch", "turn_on", "named", "desk", null)]
    [InlineData("open the bedroom blinds", "cover", "open", "area", "bedroom", null)]
    [InlineData("close the blinds", "cover", "close", "room", null, null)]
    [InlineData("stop the curtains", "cover", "stop", "room", null, null)]
    [InlineData("set the blinds to 25 percent", "cover", "position", "room", null, "25")]
    [InlineData("activate scene movie time", "scene", "activate", "named", "movie time", null)]
    [InlineData("run the bedtime script", "script", "activate", "named", "bedtime", null)]
    [InlineData("turn off all lights", "light", "turn_off", "all", null, null)]
    [InlineData("turn on the lights in the bedroom", "light", "turn_on", "area", "bedroom", null)]
    [InlineData("all lights off", "light", "turn_off", "all", null, null)]
    [InlineData("turn on the fans in this room", "fan", "turn_on", "room", null, null)]
    [InlineData("set the thermostat mode to cool", "climate", "hvac_mode", "room", null, "cool")]
    public void Parses(string text, string domain, string action, string scope, string? target, string? value)
    {
        Assert.True(HomeAssistantEntityCommandParser.TryParse(text, out var command));
        Assert.Equal((domain, action, scope, target, value),
            (command.Domain, command.Action, command.Scope, command.TargetName, command.Value));
    }

    [Theory]
    [InlineData("set hue lights blue")]
    [InlineData("open the pod bay doors")]
    [InlineData("tell me about fans")]
    [InlineData("turn on the lights")]
    [InlineData("set the bedroom thermostat to 72")]
    public void PreservesOtherRoutes(string text) => Assert.False(HomeAssistantEntityCommandParser.TryParse(text, out _));
}
