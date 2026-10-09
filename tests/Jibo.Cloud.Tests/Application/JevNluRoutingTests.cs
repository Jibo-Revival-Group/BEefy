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
    public async Task UnknownTurn_UsesJevAfterPhoenixMiss(TurnInputMode mode)
    {
        var phoenixCalled = false;
        var classifier = Classifier("time");
        classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => Assert.True(phoenixCalled))
            .ReturnsAsync(new NluClassification("time", 0.95, "jev", "test"));
        var phoenix = new Mock<IPhoenixConversationClient>();
        phoenix.Setup(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => phoenixCalled = true).ReturnsAsync((JiboInteractionDecision?)null);
        var turn = Turn("flurble zorp", mode);
        Assert.Equal("time", (await Service(classifier.Object, phoenix.Object).BuildDecisionAsync(turn)).IntentName);
        Assert.Equal("jev", turn.Attributes["nlu:provider"]);
        Assert.Equal("accepted", turn.Attributes["nlu:outcome"]);
        classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        phoenix.Verify(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("what time is it", "time")]
    [InlineData("hello", "hello")]
    [InlineData("tell me a joke", "joke")]
    public async Task KnownLocalIntent_SkipsJev(string transcript, string intent)
    {
        var classifier = new Mock<INluClassifier>(MockBehavior.Strict);
        Assert.Equal(intent, (await Service(classifier.Object).BuildDecisionAsync(Turn(transcript))).IntentName);
    }

    [Theory]
    [InlineData("requestTellAboutThing")]
    [InlineData("chat")]
    public async Task RecognizedPhoenixDecision_SkipsJev(string intent)
    {
        var classifier = new Mock<INluClassifier>(MockBehavior.Strict);
        var phoenix = new Mock<IPhoenixConversationClient>();
        var response = new JiboInteractionDecision(intent, "hello");
        phoenix.Setup(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        Assert.Same(response, await Service(classifier.Object, phoenix.Object).BuildDecisionAsync(Turn("flurble zorp")));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("not_understood")]
    [InlineData("unrecognized")]
    [InlineData("no_match")]
    public async Task UnknownPhoenixDecision_AllowsJev(string intent)
    {
        var phoenix = new Mock<IPhoenixConversationClient>();
        phoenix.Setup(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiboInteractionDecision(intent, "I did not understand."));
        Assert.Equal("time", (await Service(Classifier("time").Object, phoenix.Object)
            .BuildDecisionAsync(Turn("flurble zorp"))).IntentName);
    }

    [Fact]
    public async Task JevNoMatch_PreservesOriginalUnknownPhoenixReply()
    {
        var classifier = new Mock<INluClassifier>();
        var phoenix = new Mock<IPhoenixConversationClient>();
        var response = new JiboInteractionDecision("not_understood", "I did not understand.");
        phoenix.Setup(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(response);
        Assert.Same(response, await Service(classifier.Object, phoenix.Object).BuildDecisionAsync(Turn("flurble zorp")));
        classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownWithoutPhoenix_CallsJevOnce_AndRetainsLocalFallbackOnMiss()
    {
        var classifier = new Mock<INluClassifier>();
        var result = await Service(classifier.Object).BuildDecisionAsync(Turn("flurble zorp"));
        Assert.Equal("not_understood", result.IntentName);
        classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RecoveredAsrCommand_SkipsJev()
    {
        var correction = new Mock<IAsrCorrectionModel>();
        correction.Setup(c => c.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsrCorrection("twerk", 0.95, "test"));
        var classifier = new Mock<INluClassifier>(MockBehavior.Strict);
        Assert.Equal("twerk", (await Service(classifier.Object, correction: correction.Object)
            .BuildDecisionAsync(Turn("twke", TurnInputMode.WakeWord))).IntentName);
    }

    [Fact]
    public async Task KnownLocalIntent_WithUnknownPhoenixReply_SkipsJev()
    {
        var phoenix = new Mock<IPhoenixConversationClient>();
        phoenix.Setup(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JiboInteractionDecision("unknown", "I did not understand."));
        Assert.Equal("time", (await Service(new Mock<INluClassifier>(MockBehavior.Strict).Object, phoenix.Object)
            .BuildDecisionAsync(Turn("what time is it"))).IntentName);
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
        var turn = Turn("flurble zorp");
        Assert.NotEqual(intent, (await Service(Classifier(intent).Object).BuildDecisionAsync(turn)).IntentName);
        Assert.Equal("missing_values", turn.Attributes["nlu:outcome"]);
    }

    [Theory]
    [InlineData("volume_to_value", "set volume to 4")]
    [InlineData("timer_value", "set a timer for five minutes")]
    [InlineData("alarm_value", "set an alarm for 7:30 am")]
    public async Task ExtractableValue_ReusesLocalParser(string intent, string transcript)
    {
        Assert.Equal(intent, (await Service(new Mock<INluClassifier>(MockBehavior.Strict).Object)
            .BuildDecisionAsync(Turn(transcript))).IntentName);
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
        Assert.NotEqual(intent, (await Service(Classifier(intent).Object).BuildDecisionAsync(Turn("flurble zorp"))).IntentName);
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
