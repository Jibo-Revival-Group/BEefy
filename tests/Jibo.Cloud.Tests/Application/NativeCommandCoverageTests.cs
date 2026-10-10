using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class NativeCommandCoverageTests
{
    [Fact]
    public void EveryRegisteredCommandHasExecutableNativeHandlerAndContent()
    {
        var cloudHandlers = new HashSet<string> { "answer-skill", "report-skill", "chitchat-skill", "color-skill", "example-skill", "template-skill" };
        var resources = NativeConversationResources.Instance.Files;
        foreach (var command in NativeCommandRegistry.Instance.Commands)
        {
            if (command.OnRobot) Assert.Contains(command.Skill, NativeCommandRegistry.Instance.LocalSkills);
            else Assert.Contains(command.Skill, cloudHandlers);
            if (command.Skill != "chitchat-skill") continue;
            var mim = command.Memo.GetProperty("mim").GetString()!;
            Assert.True(resources.Keys.Any(p => p.EndsWith("/" + mim + ".mim", StringComparison.Ordinal)
                || p.Contains("/" + mim + "_", StringComparison.Ordinal)), $"Missing content: {command.Intent} -> {mim}");
        }
        using var catalog = JsonDocument.Parse(resources["packages/nlu/src/generatedIntentCatalog.json"]);
        foreach (var entry in catalog.RootElement.GetProperty("tools").EnumerateArray().Where(t => !t.GetProperty("launch").GetBoolean()))
        {
            Assert.NotEmpty(entry.GetProperty("domains").EnumerateArray());
            Assert.NotEmpty(entry.GetProperty("source").GetProperty("fst").EnumerateArray());
            foreach (var rule in entry.GetProperty("source").GetProperty("fst").EnumerateArray())
                Assert.Contains(rule.GetString()![..^5], NativeGrammar.Instance.RuleNames);
        }
    }

    [Fact]
    public async Task EveryRegisteredRobotCommandExecutesWithItsSkillAndParameters()
    {
        var service = new JiboInteractionService(new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstRandomizer(), new InMemoryPersonalMemoryStore());
        foreach (var command in NativeCommandRegistry.Instance.Commands.Where(c => c.OnRobot))
        {
            var entities = new Dictionary<string, string> { ["skill"] = command.Skill, ["domain"] = command.Skill[4..],
                ["group"] = "living", ["color"] = "red", ["routine"] = "BasicWarrior", ["date"] = "today",
                ["seconds"] = "30", ["minutes"] = "2", ["hours"] = "0", ["time"] = "7", ["ampm"] = "am" };
            if (command.Constraints.ValueKind == JsonValueKind.Array)
                foreach (var constraint in command.Constraints.EnumerateArray())
                    entities[constraint.GetProperty("name").GetString()!] = constraint.GetProperty("value").ToString() is "*" ? "Alice" : constraint.GetProperty("value").ToString();
            var turn = new TurnContext { RawTranscript = "native command fixture", NormalizedTranscript = "native command fixture",
                InputMode = TurnInputMode.DirectText, Attributes = new Dictionary<string, object?>
                { ["clientIntent"] = command.Intent, ["clientEntities"] = entities } };
            var plan = await new DemoConversationBroker(service).HandleTurnAsync(turn);
            var invocation = Assert.Single(plan.Actions.OfType<InvokeNativeSkillAction>());
            Assert.True(invocation.SkillName == command.Skill, $"{command.Skill}/{command.Intent}: {plan.IntentName} -> {invocation.SkillName}");
            var messages = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new Jibo.Cloud.Domain.Models.CloudSession(), true);
            Assert.DoesNotContain(messages, m => m.Text.Contains("SKILL_REDIRECT") || m.Text.Contains("SKILL_ACTION"));
            using var listen = JsonDocument.Parse(messages.Single(m => m.Text.Contains("\"type\":\"LISTEN\"")).Text);
            Assert.Equal(command.Skill, listen.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
            Assert.Equal(command.Intent, listen.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        }
    }

    [Fact]
    public async Task EveryRegisteredScriptedResponseRendersNativeContentWithoutUnexpandedParameters()
    {
        var catalog = await new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()).GetCatalogAsync();
        foreach (var command in NativeCommandRegistry.Instance.Commands.Where(c => c.Skill == "chitchat-skill"))
        {
            var entities = new Dictionary<string, object?>();
            if (command.Constraints.ValueKind == JsonValueKind.Array)
                foreach (var constraint in command.Constraints.EnumerateArray())
                    entities[constraint.GetProperty("name").GetString()!] = constraint.GetProperty("value").ToString() is "*" ? "Amber" : constraint.GetProperty("value").ToString();
            var parsed = new NativeParseResult(command.Intent, command.Skill, "chitchat", ["launch"], null, entities);
            var decision = NativeScriptedReplies.Build(command, parsed, new TurnContext(), catalog, new FirstRandomizer(), "Amber", DateTimeOffset.UtcNow);
            Assert.False(string.IsNullOrEmpty(decision.ReplyText), $"{command.Intent}: {command.Memo}");
            Assert.DoesNotContain("${", decision.ReplyText);
            Assert.NotNull(decision.SkillPayload);
            Assert.True(decision.SkillPayload.ContainsKey("esml"), $"Missing performance for {command.Intent}: {command.Memo}");
        }
    }

    [Fact]
    public void OptionalClassifierContainsExecutableCommandsAndExcludesRequiredParameters()
    {
        Assert.True(NluIntentCatalog.IsSupported("native/exercise/exerciseDoYoga"));
        Assert.True(NluIntentCatalog.IsSupported("native/settings/menu"));
        Assert.False(NluIntentCatalog.IsSupported("native/hue-control/lightsColor"));
        Assert.False(NluIntentCatalog.IsSupported("native/hue-control/lightsGroupOn"));
        Assert.False(NluIntentCatalog.IsSupported("native/ifttt/ifttt"));
        Assert.False(NluIntentCatalog.IsSupported("native/clock/whenIsBirthday"));
    }

    [Fact]
    public async Task NativeColorQuestionKeepsSocketTurnOpen()
    {
        var turn = new TurnContext { RawTranscript = "let's talk colors", InputMode = TurnInputMode.DirectText };
        var service = new JiboInteractionService(new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstRandomizer(), new InMemoryPersonalMemoryStore());
        var plan = await new DemoConversationBroker(service).HandleTurnAsync(turn);
        Assert.True(plan.FollowUp.KeepMicOpen);
        var messages = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new Jibo.Cloud.Domain.Models.CloudSession(), true);
        using var action = JsonDocument.Parse(messages.Single(m => m.Text.Contains("SKILL_ACTION")).Text);
        Assert.False(action.RootElement.GetProperty("final").GetBoolean());
        Assert.False(action.RootElement.GetProperty("data").GetProperty("final").GetBoolean());
    }
    private sealed class FirstRandomizer : IJiboRandomizer { public T Choose<T>(IReadOnlyList<T> items) => items[0]; }
}
