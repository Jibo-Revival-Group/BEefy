using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Infrastructure.Content;
using Jibo.Cloud.Infrastructure.Persistence;
using Jibo.Runtime.Abstractions;
using Moq;

namespace Jibo.Cloud.Tests.Application;

public sealed class AsrModelFallbackTests
{
    [Theory]
    [InlineData("what's your paper color", "what is your favorite color", "robot_favorite_color")]
    [InlineData("my time is it", "what time is it", "time")]
    [InlineData("make a peter sir", "make a pizza", "pizza")]
    [InlineData("make a peter", "make a pizza", "pizza")]
    [InlineData("make a pit sir", "make a pizza", "pizza")]
    [InlineData("make peter", "make pizza", "pizza")]
    [InlineData("twick", "twerk", "twerk")]
    [InlineData("twelc", "twerk", "twerk")]
    [InlineData("do a dense", "do a dance", "dance")]
    [InlineData("tell me a storey", "tell me a story", "robot_story")]
    public async Task ContextualCommandRecovery(string heard, string corrected, string intent)
    {
        var model = Model(corrected);
        Assert.Equal(intent, (await Service(model.Object).BuildDecisionAsync(Turn(heard))).IntentName);
        model.Verify(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(0.749, false)]
    [InlineData(0.75, true)]
    [InlineData(0.79, true)]
    public async Task DefaultThreshold_AcceptsAt75Percent(double confidence, bool accepted)
    {
        var turn = Turn("make a peter sir");
        var decision = await Service(Model("make a pizza", confidence).Object).BuildDecisionAsync(turn);
        Assert.Equal(accepted, decision.IntentName == "pizza");
        Assert.Equal(accepted, turn.Attributes.ContainsKey(JiboInteractionService.ModelCorrectedTranscriptKey));
        Assert.Equal("make a peter sir", turn.RawTranscript);
    }

    [Theory]
    [InlineData(0.749, false)]
    [InlineData(0.75, true)]
    public async Task ConfidentRecovery_PrecedesPhoenixConversation(double confidence, bool recovered)
    {
        var phoenix = new Mock<IPhoenixConversationClient>();
        var fallback = new JiboInteractionDecision("chat", "I don't understand.");
        phoenix.Setup(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(fallback);
        var service = Service(Model("make a pizza", confidence).Object, phoenix: phoenix.Object);
        var decision = await service.BuildDecisionAsync(Turn("make a peter sir"));
        Assert.Equal(recovered ? "pizza" : "chat", decision.IntentName);
        phoenix.Verify(p => p.TryDecideAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            recovered ? Times.Never() : Times.Once());
        if (!recovered) Assert.Same(fallback, decision);
    }

    [Fact]
    public async Task RecognizedCommand_PreservesPhoenixPathAndSkipsInference()
    {
        var model = Model("make a pizza");
        var phoenix = new Mock<IPhoenixConversationClient>();
        var response = new JiboInteractionDecision("requestMakePizza", "One pizza, coming right up.");
        phoenix.Setup(p => p.TryDecideAsync("make a pizza", It.IsAny<CancellationToken>())).ReturnsAsync(response);
        Assert.Same(response, await Service(model.Object, phoenix: phoenix.Object).BuildDecisionAsync(Turn("make a pizza")));
        model.Verify(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task UnrecognizedCommand_CorrectsOnce_AndPreservesOriginalTranscript()
    {
        var model = Model("Tell me a joke.");
        var turn = Turn("tell me a jok");
        var result = await Service(model.Object).BuildDecisionAsync(turn);
        Assert.Equal("joke", result.IntentName);
        Assert.Equal("tell me a jok", turn.RawTranscript);
        Assert.Equal("tell me a joke", turn.Attributes[JiboInteractionService.ModelCorrectedTranscriptKey]);
        Assert.Equal("test-neural-model", turn.Attributes["stt:correctionModel"]);
        model.Verify(m => m.TryCorrectAsync("tell me a jok", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("make a pizza")]
    [InlineData("do a dance")]
    [InlineData("what time is it")]
    [InlineData("tell me a joke")]
    [InlineData("what's your favorite color")]
    public async Task RecognizedCommand_NeverCallsModel(string transcript)
    {
        var model = Model("Tell me a joke.");
        await Service(model.Object).BuildDecisionAsync(Turn(transcript));
        model.Verify(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("tell me a jok", "Tell me a joke.", 0.1)]
    [InlineData("tell me a jok", "Tell me a joke.", 1.5)]
    [InlineData("tell me a jok", "Turn off the lights.", 0.99)]
    [InlineData("three purple clouds", "Three purple cloud.", 0.99)]
    [InlineData("tell me a jok", "Tell me a jok.", 0.99)]
    public async Task RejectedCorrection_RetainsExistingFallback(string heard, string corrected, double confidence)
    {
        var expected = await Service().BuildDecisionAsync(Turn(heard));
        var model = Model(corrected, confidence);
        var turn = Turn(heard);
        var result = await Service(model.Object).BuildDecisionAsync(turn);
        Assert.Equal(expected.IntentName, result.IntentName);
        Assert.Equal(expected.ReplyText, result.ReplyText);
        Assert.False(turn.Attributes.ContainsKey(JiboInteractionService.ModelCorrectedTranscriptKey));
        model.Verify(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MissingOrFailedModel_RetainsExistingFallback()
    {
        var expected = await Service().BuildDecisionAsync(Turn("tell me a jok"));
        var model = new Mock<IAsrCorrectionModel>();
        model.Setup(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("worker exited"));
        var failed = await Service(model.Object).BuildDecisionAsync(Turn("tell me a jok"));
        Assert.Equal(expected.IntentName, failed.IntentName);
        Assert.Equal(expected.ReplyText, failed.ReplyText);
        model.Setup(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AsrCorrection?)null);
        var missing = await Service(model.Object).BuildDecisionAsync(Turn("tell me a jok"));
        Assert.Equal(expected.IntentName, missing.IntentName);
        Assert.Equal(expected.ReplyText, missing.ReplyText);
    }

    [Fact]
    public async Task SlowModel_IsBoundedAndRetainsFallback()
    {
        var expected = await Service().BuildDecisionAsync(Turn("tell me a jok"));
        var model = new Mock<IAsrCorrectionModel>();
        model.Setup(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { await Task.Delay(1000); return new AsrCorrection("Tell me a joke.", 0.99, "slow"); });
        var metrics = new Mock<ITransportMetrics>();
        var result = await Service(model.Object, new AsrCorrectionOptions { TimeoutMilliseconds = 10 }, metrics: metrics.Object)
            .BuildDecisionAsync(Turn("tell me a jok"));
        Assert.Equal(expected.IntentName, result.IntentName);
        Assert.Equal(expected.ReplyText, result.ReplyText);
        metrics.Verify(m => m.TurnPhaseCompleted("asr_correction", "timeout", It.IsAny<double>()), Times.Once);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cancel = new CancellationTokenSource();
        var model = new Mock<IAsrCorrectionModel>();
        model.Setup(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, CancellationToken token) =>
            {
                cancel.Cancel();
                await Task.Delay(1000, token);
                return null;
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(model.Object).BuildDecisionAsync(Turn("tell me a jok"), cancel.Token));
    }

    [Fact]
    public async Task DirectText_DisabledModel_AndNonEnglish_DoNotInvokeModel()
    {
        var model = Model("Tell me a joke.");
        await Service(model.Object).BuildDecisionAsync(new TurnContext
            { RawTranscript = "tell me a jok", InputMode = TurnInputMode.DirectText });
        await Service(model.Object, new AsrCorrectionOptions { Enabled = false }).BuildDecisionAsync(Turn("tell me a jok"));
        await Service(model.Object).BuildDecisionAsync(new TurnContext
            { RawTranscript = "tell me a jok", Locale = "fr-FR" });
        await Service(model.Object).BuildDecisionAsync(new TurnContext
            { RawTranscript = "tell me a jok", Attributes = new Dictionary<string, object?> { ["listenRules"] = new[] { "shared/yes_no" } } });
        model.Verify(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Repeat_ReusesCorrectedCommandWithoutAnotherInference()
    {
        var model = Model("Tell me a joke.");
        var store = new RepeatLastCommandStore();
        var service = Service(model.Object, store: store);
        Assert.Equal("joke", (await service.BuildDecisionAsync(Turn("tell me a jok"))).IntentName);
        Assert.Equal("joke", (await service.BuildDecisionAsync(Turn("do that again"))).IntentName);
        model.Verify(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("twick", "twerk", true)]
    [InlineData("twelc", "twerk", true)]
    [InlineData("work", "twerk", false)]
    [InlineData("Tim", "time", false)]
    [InlineData("not", "twerk", false)]
    [InlineData("twick tomorrow", "twerk", false)]
    [InlineData("twick", "can you twerk", false)]
    [InlineData("twick", "weather", false)]
    [InlineData("make a pit sir", "make a pizza", true)]
    [InlineData("make a peter", "make a pizza", true)]
    [InlineData("make peter", "make pizza", true)]
    [InlineData("do a dense", "do a dance", true)]
    [InlineData("make a pit sir tomorrow", "make a pizza", false)]
    [InlineData("do not make a pit sir", "make a pizza", false)]
    [InlineData("make my pit sir", "make a pizza", false)]
    [InlineData("make two pit sir", "make a pizza", false)]
    [InlineData("bake a peter", "make a pizza", false)]
    [InlineData("what your favorite colur", "What is your favorite color?", true)]
    [InlineData("tell me a jok", "Tell me a joke.", true)]
    [InlineData("what is your paper color", "what is your favorite color", true)]
    [InlineData("what's your paper color", "what is your favorite color", true)]
    [InlineData("my time is it", "what time is it", true)]
    [InlineData("five time is it", "what time is it", false)]
    [InlineData("not time is it", "what time is it", false)]
    [InlineData("tell me a choke", "tell me a joke", true)]
    [InlineData("tell tell me a jok", "Tell me a joke.", true)]
    [InlineData("set timer for five minutes", "Set timer for nine minutes.", false)]
    [InlineData("do not tell me a jok", "Tell me a joke.", false)]
    [InlineData("what is my favorite colur", "What is your favorite color?", false)]
    [InlineData("my name is Sam", "My name is Tom.", false)]
    public void Acceptance_PreservesNumbersPronounsNegationAndMeaning(string before, string after, bool expected)
    {
        Assert.Equal(expected, AsrCorrectionAcceptance.IsConservativeEdit(before, after));
    }

    private static Mock<IAsrCorrectionModel> Model(string text, double confidence = 0.99)
    {
        var model = new Mock<IAsrCorrectionModel>();
        model.Setup(m => m.TryCorrectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AsrCorrection(text, confidence, "test-neural-model"));
        return model;
    }

    private static TurnContext Turn(string text) => new()
    {
        RawTranscript = text, NormalizedTranscript = text, DeviceId = "robot-model-test"
    };

    private static JiboInteractionService Service(IAsrCorrectionModel? model = null,
        AsrCorrectionOptions? options = null, RepeatLastCommandStore? store = null, ITransportMetrics? metrics = null, IPhoenixConversationClient? phoenix = null) => new(
        new JiboExperienceContentCache(new InMemoryJiboExperienceContentRepository()),
        new FirstRandomizer(), new InMemoryPersonalMemoryStore(), repeatLastCommandStore: store,
        asrCorrectionModel: model, asrCorrectionOptions: options, transportMetrics: metrics, phoenixConversation: phoenix);

    private sealed class FirstRandomizer : IJiboRandomizer
    {
        public T Choose<T>(IReadOnlyList<T> items) => items[0];
    }
}
