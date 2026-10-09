using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.Application;

public sealed class JevNluRoutingTests
{
    [Theory]
    [InlineData(TurnInputMode.DirectText)]
    [InlineData(TurnInputMode.WakeWord)]
    public async Task PrimaryDecision_BypassesPhoenixAndAsrCorrection(TurnInputMode mode)
    {
        var classifier = Classifier("time");
        var phoenix = new Mock<IPhoenixConversationClient>(MockBehavior.Strict);
        var correction = new Mock<IAsrCorrectionModel>(MockBehavior.Strict);
        var turn = Turn("Could I get the hour?", mode);
        Assert.Equal("time", (await Service(classifier.Object, phoenix.Object, correction.Object).BuildDecisionAsync(turn)).IntentName);
        Assert.Equal("jev", turn.Attributes["nlu:provider"]);
        Assert.Equal("accepted", turn.Attributes["nlu:outcome"]);
        classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PrimaryClassification_OverridesConfidentLocalMatch()
    {
        Assert.Equal("date", (await Service(Classifier("date").Object).BuildDecisionAsync(Turn("what time is it"))).IntentName);
    }

    [Fact]
    public async Task NoMatch_RetainsPhoenixFallback()
    {
        var classifier = new Mock<INluClassifier>();
        var phoenix = new Mock<IPhoenixConversationClient>();
        var response = new JiboInteractionDecision("phoenix", "hello");
        phoenix.Setup(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        Assert.Same(response, await Service(classifier.Object, phoenix.Object).BuildDecisionAsync(Turn("hello")));
        phoenix.Verify(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NoMatch_RetainsBoundedAsrRecovery()
    {
        var correction = new Mock<IAsrCorrectionModel>();
        correction.Setup(c => c.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsrCorrection("twerk", 0.95, "test"));
        var classifier = new Mock<INluClassifier>();
        Assert.Equal("twerk", (await Service(classifier.Object, correction: correction.Object)
            .BuildDecisionAsync(Turn("twke", TurnInputMode.WakeWord))).IntentName);
    }

    [Theory]
    [InlineData("volume_to_value")]
    [InlineData("timer_value")]
    [InlineData("alarm_value")]
    [InlineData("alarm_edit_value")]
    [InlineData("radio_genre")]
    [InlineData("memory_set_name")]
    [InlineData("ha_climate_set_temp")]
    public async Task MissingRequiredValue_RejectsClassification(string intent)
    {
        var turn = Turn("hello");
        Assert.NotEqual(intent, (await Service(Classifier(intent).Object).BuildDecisionAsync(turn)).IntentName);
        Assert.Equal("missing_values", turn.Attributes["nlu:outcome"]);
    }

    [Theory]
    [InlineData("volume_to_value", "set volume to 4")]
    [InlineData("timer_value", "set a timer for five minutes")]
    [InlineData("alarm_value", "set an alarm for 7:30 am")]
    public async Task ExtractableValue_ReusesLocalParser(string intent, string transcript)
    {
        Assert.Equal(intent, (await Service(Classifier(intent).Object).BuildDecisionAsync(Turn(transcript))).IntentName);
    }

    [Theory]
    [InlineData("shared/yes_no", "yes")]
    [InlineData("exercise/want_to", "yes")]
    [InlineData("clock/timer_value", "five minutes")]
    [InlineData("clock/alarm_value", "seven am")]
    public async Task ContextualListen_DoesNotCallJev(string rule, string text)
    {
        var classifier = new Mock<INluClassifier>(MockBehavior.Strict);
        var turn = Turn(text);
        turn.Attributes["listenRules"] = new[] { rule, "globals/gui_nav" };
        turn.Attributes["listenHotphrase"] = false;
        await Service(classifier.Object).BuildDecisionAsync(turn);
    }

    [Fact]
    public async Task Trigger_DoesNotCallJev()
    {
        var turn = Turn("hello");
        turn.Attributes["messageType"] = "TRIGGER";
        await Service(new Mock<INluClassifier>(MockBehavior.Strict).Object).BuildDecisionAsync(turn);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("invented")]
    public async Task NonExecutableIntent_IsRejected(string intent)
    {
        Assert.NotEqual(intent, (await Service(Classifier(intent).Object).BuildDecisionAsync(Turn("hello"))).IntentName);
    }

    private static Mock<INluClassifier> Classifier(string intent)
    {
        var mock = new Mock<INluClassifier>();
        mock.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new NluClassification(intent, 0.95, "jev", "test"));
        return mock;
    }
    private static TurnContext Turn(string text, TurnInputMode mode = TurnInputMode.DirectText) => new()
    {
        RawTranscript = text, NormalizedTranscript = text, InputMode = mode, DeviceId = "test"
    };
    private static JiboInteractionService Service(INluClassifier classifier, IPhoenixConversationClient? phoenix = null,
        IAsrCorrectionModel? correction = null) => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()), new FirstRandomizer(),
        new InMemoryPersonalMemoryStore(), phoenixConversation: phoenix, asrCorrectionModel: correction, nluClassifier: classifier);
    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
