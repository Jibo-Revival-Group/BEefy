using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Moq;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class NativeConversationTests
{
    [Theory]
    [InlineData("open settings", "@be/settings", "menu")]
    [InlineData("show battery status", "@be/settings", "battery")]
    [InlineData("show storage status", "@be/settings", "storageStatus")]
    [InlineData("show wifi status", "@be/settings", "wifiStatus")]
    [InlineData("open main menu", "@be/main-menu", "launchMainMenu")]
    [InlineData("play circuit saver", "@be/circuit-saver", "launchGame")]
    [InlineData("do yoga", "@be/exercise", "exerciseDoYoga")]
    [InlineData("open tutorial", "@be/tutorial", "tutorialOpen")]
    [InlineData("turn the lights red", "@be/hue-control", "lightsColor")]
    [InlineData("trigger movie time", "@be/ifttt", "ifttt")]
    [InlineData("what's new in your update", "@be/surprises-ota", "releaseNotes")]
    [InlineData("what can I do", "@be/friendly-tips", "whatCanIDo")]
    public async Task NativeLaunches_AreExecutableAndDoNotSpeakOrLaunchTwice(string text, string skill, string intent)
    {
        var classifier = new Mock<INluClassifier>(MockBehavior.Strict);
        var service = Service(classifier.Object);
        var turn = Turn(text);
        var plan = await new DemoConversationBroker(service).HandleTurnAsync(turn);
        var invocation = Assert.Single(plan.Actions.OfType<InvokeNativeSkillAction>());
        Assert.Equal(skill, invocation.SkillName);
        Assert.Equal(intent, invocation.Payload!["localIntent"]);
        Assert.False(plan.FollowUp.KeepMicOpen);
        var messages = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), true)
            .Select(m => JsonDocument.Parse(m.Text).RootElement.Clone()).ToArray();
        var listen = Assert.Single(messages, m => m.GetProperty("type").GetString() == "LISTEN");
        Assert.Equal("native-turn", listen.GetProperty("transID").GetString());
        Assert.Equal(skill, listen.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal(intent, listen.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.DoesNotContain(messages, m => m.GetProperty("type").GetString() is "SKILL_ACTION" or "SKILL_REDIRECT");
    }

    [Theory]
    [InlineData("do sun salutation", "routine", "SunSalutation")]
    [InlineData("turn the lights red", "color", "red")]
    [InlineData("make living room lights red", "group", "living")]
    [InlineData("trigger movie time", "slotAction", "movie time")]
    public async Task NativeLaunches_PreserveCommandEntities(string text, string entity, string value)
    {
        var turn = Turn(text);
        var plan = await new DemoConversationBroker(Service()).HandleTurnAsync(turn);
        var messages = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), true);
        var listen = messages.Select(m => JsonDocument.Parse(m.Text).RootElement.Clone())
            .Single(m => m.GetProperty("type").GetString() == "LISTEN");
        Assert.Equal(value, listen.GetProperty("data").GetProperty("nlu").GetProperty("entities").GetProperty(entity).GetString());
    }

    [Fact]
    public async Task ContextualYogaReply_GoesToOwningSkillWithoutSpeechOrRelaunch()
    {
        var turn = Turn("basic warrior");
        turn.Attributes["listenRules"] = new[] { "exercise/routine_selector" };
        turn.Attributes["listenHotphrase"] = false;
        var plan = await new DemoConversationBroker(Service()).HandleTurnAsync(turn);
        var messages = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), true)
            .Select(m => JsonDocument.Parse(m.Text).RootElement.Clone()).ToArray();
        var listen = messages.Single(m => m.GetProperty("type").GetString() == "LISTEN");
        Assert.Equal("BasicWarrior", listen.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.DoesNotContain(messages, m => m.GetProperty("type").GetString() is "SKILL_ACTION" or "SKILL_REDIRECT");
    }

    [Fact]
    public async Task ColorConversation_AsksAndUsesTheFollowUpAnswer()
    {
        var service = Service();
        var question = await service.BuildDecisionAsync(Turn("let's talk colors"));
        Assert.Equal("favoriteColorChat", question.IntentName);
        var turn = Turn("purple", TurnInputMode.FollowUp);
        foreach (var pair in question.ContextUpdates!) turn.Attributes[pair.Key] = pair.Value;
        var answer = await service.BuildDecisionAsync(turn);
        Assert.Contains("purple", answer.ReplyText);
        Assert.Null(answer.ContextUpdates!["chitchatNativeState"]);
    }

    [Theory]
    [InlineData("over here", "overHere")]
    [InlineData("turn away", "turnAway")]
    public async Task NativeGlobalGesturesGoToIdleWithoutCloudSpeech(string text, string intent)
    {
        var turn = Turn(text);
        var plan = await new DemoConversationBroker(Service()).HandleTurnAsync(turn);
        var messages = ResponsePlanToSocketMessagesMapper.Map(plan, turn, new CloudSession(), true);
        using var listen = JsonDocument.Parse(messages.Single(m => m.Text.Contains("\"type\":\"LISTEN\"")).Text);
        Assert.Equal("@be/idle", listen.RootElement.GetProperty("data").GetProperty("match").GetProperty("skillID").GetString());
        Assert.Equal(intent, listen.RootElement.GetProperty("data").GetProperty("nlu").GetProperty("intent").GetString());
        Assert.DoesNotContain(messages, m => m.Text.Contains("SKILL_ACTION") || m.Text.Contains("SKILL_REDIRECT"));
    }

    [Fact]
    public async Task ExplicitExampleHandlerRunsItsThreeScriptedNodes()
    {
        var turn = Turn("native example");
        turn.Attributes["clientIntent"] = "doesJiboLikeThing";
        turn.Attributes["clientEntities"] = new Dictionary<string, string> { ["skill"] = "example-skill" };
        var decision = await Service().BuildDecisionAsync(turn);
        Assert.Equal("example-skill", decision.SkillName);
        Assert.Contains("Node1", decision.ReplyText);
        Assert.Contains("Node2", decision.ReplyText);
        Assert.Contains("Node3", decision.ReplyText);
    }

    [Fact]
    public async Task TemplateLaunchProducesItsScriptedResponse()
    {
        var decision = await Service().BuildDecisionAsync(Turn("template skill"));
        Assert.Equal("template-skill", decision.SkillName);
        Assert.Equal("This is a template skill", decision.ReplyText);
    }

    [Theory]
    [InlineData("tell me a joke", "joke")]
    [InlineData("play bad apple", "bad_apple")]
    public async Task ExistingNativeFeaturesKeepPrecedence(string text, string intent)
    {
        var decision = await Service().BuildDecisionAsync(Turn(text));
        Assert.Equal(intent, decision.IntentName);
    }

    [Theory]
    [InlineData("my favorite color is blue")]
    [InlineData("what is my favorite color")]
    [InlineData("my birthday is June 5")]
    [InlineData("when is my birthday")]
    [InlineData("my anniversary is June 5")]
    [InlineData("I love pizza")]
    [InlineData("add milk to my shopping list")]
    [InlineData("add bread to my grocery list")]
    [InlineData("add call the vet to my to do list")]
    [InlineData("shopping list")]
    [InlineData("what's on my shopping list")]
    public async Task RemovedPersonalCommands_DoNotStoreFactsOrStartListDialogs(string text)
    {
        var store = new InMemoryPersonalMemoryStore();
        var service = new JiboInteractionService(
            new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
            new FirstRandomizer(), store);
        var turn = Turn(text);
        turn.Attributes["accountId"] = "removed-account";
        turn.Attributes["loopId"] = "removed-loop";
        var decision = await service.BuildDecisionAsync(turn);
        Assert.False(decision.IntentName.StartsWith("memory_"));
        Assert.False(decision.IntentName.StartsWith("shopping_list"));
        Assert.False(decision.IntentName.StartsWith("todo_list"));
        Assert.DoesNotContain(decision.ContextUpdates?.Keys ?? [], k => k.StartsWith("householdList"));
        var scope = new PersonalMemoryTenantScope("removed-account", "removed-loop", "native-test");
        Assert.Null(store.GetPreference(scope, "color"));
        Assert.Null(store.GetBirthday(scope));
        Assert.Null(store.GetImportantDate(scope, "anniversary"));
        Assert.Empty(store.GetAffinities(scope));
        Assert.Empty(store.GetListItems(scope, "shopping"));
        Assert.Empty(store.GetListItems(scope, "todo"));
    }

    [Theory]
    [InlineData("memory_set_preference")]
    [InlineData("memory_get_preference")]
    [InlineData("memory_set_birthday")]
    [InlineData("memory_get_birthday")]
    [InlineData("memory_set_important_date")]
    [InlineData("memory_get_important_date")]
    [InlineData("memory_set_affinity")]
    [InlineData("memory_get_affinity")]
    [InlineData("shopping_list")]
    [InlineData("todo_list")]
    public async Task RemovedCommands_AreUnavailableToClassifierAndClientNlu(string intent)
    {
        Assert.False(NluIntentCatalog.IsSupported(intent));
        var classifier = new Mock<INluClassifier>();
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NluClassification(intent, 1, "test", "test"));
        var turn = Turn("flurble zorp");
        turn.Attributes["clientIntent"] = intent;
        var decision = await Service(classifier.Object).BuildDecisionAsync(turn);
        Assert.NotEqual(intent, decision.IntentName);
    }

    [Fact]
    public async Task OldListFollowUpState_DoesNotCaptureItems()
    {
        var turn = Turn("milk", TurnInputMode.FollowUp);
        turn.Attributes["householdListState"] = "awaiting_item";
        turn.Attributes["householdListType"] = "shopping";
        var decision = await Service().BuildDecisionAsync(turn);
        Assert.False(decision.IntentName.StartsWith("shopping_list"));
        Assert.DoesNotContain(decision.ContextUpdates?.Keys ?? [], k => k.StartsWith("householdList"));
    }

    private static TurnContext Turn(string text, TurnInputMode mode = TurnInputMode.DirectText) => new()
    {
        RawTranscript = text, NormalizedTranscript = text, InputMode = mode,
        DeviceId = "native-test", Attributes = new Dictionary<string, object?> { ["transID"] = "native-turn" }
    };
    private static JiboInteractionService Service(INluClassifier? classifier = null) => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()), new FirstRandomizer(),
        new InMemoryPersonalMemoryStore(), nluClassifier: classifier);
    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
