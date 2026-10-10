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
    [InlineData("what time is it", "time")]
    [InlineData("hello", "hello")]
    [InlineData("tell me a joke", "joke")]
    public async Task KnownLocalIntent_SkipsJev(string transcript, string intent)
    {
        var classifier = new Mock<INluClassifier>(MockBehavior.Strict);
        Assert.Equal(intent, (await Service(classifier.Object).BuildDecisionAsync(Turn(transcript))).IntentName);
    }

    [Theory]
    [InlineData("mary christmas")]
    [InlineData("Mary Christmas!")]
    [InlineData("mary christmas jibo")]
    public async Task MaryChristmasGreetingRoutesLocallyWithoutWaitingForJev(string transcript)
    {
        var classifier = new Mock<INluClassifier>(MockBehavior.Strict);
        var decision = await Service(classifier.Object).BuildDecisionAsync(Turn(transcript));
        Assert.Equal("seasonal_holiday_greeting", decision.IntentName);
        classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task JevCanSelectChristmasGreetingWithoutExactTranscriptMatch()
    {
        var turn = Turn("flurble zorp");
        var decision = await Service(Classifier("holiday_greeting/christmas").Object).BuildDecisionAsync(turn);
        Assert.Equal("seasonal_holiday_greeting", decision.IntentName);
        Assert.Equal("accepted", turn.Attributes["nlu:outcome"]);
        Assert.NotEqual("not_understood", decision.IntentName);
    }

    [Fact]
    public async Task JevCanSelectRegisteredScriptedResponseWithManifestEntities()
    {
        var intent = NativeScriptedResponseCatalog.Commands.First(p =>
            p.Value.Memo.GetProperty("mim").GetString() == "RI_JBO_LikesCats").Key;
        var decision = await Service(Classifier(intent).Object).BuildDecisionAsync(Turn("flurble zorp"));
        Assert.Equal("RI_JBO_LikesCats", decision.SkillPayload!["mim_id"]);
        Assert.Contains("<speak>", decision.SkillPayload["esml"]!.ToString());
    }

    [Fact]
    public async Task UnknownWithoutNativeMatch_CallsJevOnce_AndRetainsLocalFallbackOnMiss()
    {
        var classifier = new Mock<INluClassifier>();
        var result = await Service(classifier.Object).BuildDecisionAsync(Turn("flurble zorp"));
        Assert.Equal("not_understood", result.IntentName);
        classifier.Verify(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnknownPhraseCanLaunchNewNativeCommandThroughOptionalJev()
    {
        var decision = await Service(Classifier("native/exercise/exerciseDoYoga").Object).BuildDecisionAsync(Turn("flurble zorp"));
        Assert.Equal("@be/exercise", decision.SkillName);
        Assert.Equal("exerciseDoYoga", decision.SkillPayload!["localIntent"]);
        Assert.True((bool)decision.SkillPayload["nativeLaunch"]!);
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
    private static JiboInteractionService Service(INluClassifier classifier,
        IAsrCorrectionModel? correction = null) => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()), new FirstRandomizer(),
        new InMemoryPersonalMemoryStore(), asrCorrectionModel: correction, nluClassifier: classifier);
    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
