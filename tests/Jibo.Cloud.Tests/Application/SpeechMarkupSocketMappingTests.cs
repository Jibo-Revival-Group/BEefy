using System.Text.Json;
using System.Xml.Linq;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class SpeechMarkupSocketMappingTests
{
    [Theory]
    [InlineData("Merry Christmas. <break size='.3'/> You too.")]
    [InlineData("Merry Christmas. &lt;break size='.3'/&gt; You too.")]
    [InlineData("<tts>Merry Christmas.</tts> <break size='.3'/> You too.")]
    public void LegacySpeechTagsAreNotEscapedIntoSpokenWords(string text)
    {
        var plan = new ResponsePlan
        {
            IntentName = "seasonal_holiday_greeting",
            Actions = { new SpeakAction { Text = text } }
        };
        var replies = ResponsePlanToSocketMessagesMapper.Map(plan,
            new TurnContext { Attributes = new Dictionary<string, object?> { ["transID"] = "holiday" } },
            new CloudSession(), emitSkillActions: true);
        using var json = JsonDocument.Parse(replies.Single(p => JsonDocument.Parse(p.Text).RootElement
            .GetProperty("type").GetString() == "SKILL_ACTION").Text);
        var esml = FindEsml(json.RootElement);
        var xml = XDocument.Parse(esml!);
        Assert.Contains("Merry Christmas.", xml.Root!.Value);
        Assert.DoesNotContain("<break", xml.Root.Value);
        Assert.DoesNotContain("tts", xml.Root.Value);
        Assert.Equal(".3", xml.Descendants("break").Single().Attribute("size")!.Value);
    }

    [Fact]
    public void PlainSpeechEscapesAmpersandsAndRemovesUnsupportedTags()
    {
        var body = LegacyMimPromptNormalizer.ToEsmlBody("<pitch mult='1.2'>Happy</pitch> & merry Christmas.");
        Assert.Equal("Happy & merry Christmas.", XDocument.Parse("<speak>" + body + "</speak>").Root!.Value);
    }

    private static string? FindEsml(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name == "esml") return property.Value.GetString();
                if (FindEsml(property.Value) is { } value) return value;
            }
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                if (FindEsml(item) is { } value) return value;
        return null;
    }
}
