using Jibo.Cloud.Application.Services;
using System.Text.Json;

namespace Jibo.Cloud.Tests.Application;

public sealed class NativeGrammarTests
{
    [Fact]
    public void AllPinnedGrammarsLoad() => Assert.True(NativeGrammar.Instance.RuleNames.Count > 100);

    [Theory]
    [InlineData("open settings", "@be/settings", "menu")]
    [InlineData("battery", "@be/settings", "battery")]
    [InlineData("show storage status", "@be/settings", "storageStatus")]
    [InlineData("show wifi status", "@be/settings", "wifiStatus")]
    [InlineData("open main menu", "@be/main-menu", "launchMainMenu")]
    [InlineData("play circuit saver", "@be/circuit-saver", "launchGame")]
    [InlineData("do yoga", "@be/exercise", "exerciseDoYoga")]
    [InlineData("open tutorial", "@be/tutorial", "tutorialOpen")]
    [InlineData("turn the lights red", "@be/hue-control", "lightsColor")]
    public void LaunchesCarrySkillAndIntent(string text, string skill, string intent)
    {
        var result = NativeGrammar.Instance.Parse(text);
        Assert.NotNull(result);
        Assert.Equal(skill, result.Skill);
        Assert.Equal(intent, result.Intent);
    }
    [Theory]
    [InlineData("set up hue lights", "lightsSetup", null, null)]
    [InlineData("forget hue lights", "lightsDeleteData", null, null)]
    [InlineData("change default lights group", "lightsSetupDefaultGroup", null, null)]
    [InlineData("turn lights on", "lightsOn", null, null)]
    [InlineData("turn lights off", "lightsOff", null, null)]
    [InlineData("turn kitchen lights on", "lightsGroupOn", "group", "kitchen")]
    [InlineData("turn bedroom lights off", "lightsGroupOff", "group", "bedroom")]
    [InlineData("turn lights brighter", "lightsUp", null, null)]
    [InlineData("dim lights", "lightsDown", null, null)]
    [InlineData("turn lights all the way up", "lightsUpCompletely", null, null)]
    [InlineData("make kitchen lights brighter", "lightsGroupUp", "group", "kitchen")]
    [InlineData("dim bedroom lights", "lightsGroupDown", "group", "bedroom")]
    [InlineData("make office lights as bright as possible", "lightsGroupUpCompletely", "group", "office")]
    [InlineData("make lights warmer", "lightsWarm", null, null)]
    [InlineData("make lights cooler", "lightsCool", null, null)]
    [InlineData("make kitchen lights warmer", "lightsGroupWarm", "group", "kitchen")]
    [InlineData("make bedroom lights cooler", "lightsGroupCool", "group", "bedroom")]
    [InlineData("make living room lights red", "lightsColorGroup", "group", "living")]
    [InlineData("make lights cornflower blue", "lightsColor", "color", "cornflowerblue")]
    [InlineData("how do I use hue lights", "lightsHowTo", null, null)]
    public void HueCommandsCarryRequiredParameters(string text, string intent, string? entity, string? expected)
    {
        var parsed = NativeGrammar.Instance.Parse(text);
        Assert.NotNull(parsed);
        Assert.Equal("@be/hue-control", parsed.Skill);
        Assert.Equal(intent, parsed.Intent);
        if (entity is not null) Assert.Equal(expected, parsed.Entities[entity]);
    }

    [Fact]
    public void AmbiguousUnsupportedColorDoesNotBecomeHueColorCommand()
    {
        var parsed = NativeGrammar.Instance.Parse("make the lights banana");
        Assert.NotEqual("lightsColor", parsed?.Intent);
    }

    public static IEnumerable<object[]> OracleCases()
    {
        using var json = JsonDocument.Parse(NativeConversationResources.Instance.Files["packages/nlu/test/fixtures/launch-oracle-89.json"]);
        foreach (var row in json.RootElement.GetProperty("cases").EnumerateArray())
            yield return new object[] { row.GetProperty("text").GetString()!, row.GetProperty("intent").GetString()! };
    }

    [Theory]
    [MemberData(nameof(OracleCases))]
    public void PinnedLaunchExamples_PreserveRecognizedIntents(string text, string intent)
    {
        var result = NativeGrammar.Instance.Parse(text);
        Assert.NotNull(result);
        Assert.Equal(intent, result.Intent);
    }

}
