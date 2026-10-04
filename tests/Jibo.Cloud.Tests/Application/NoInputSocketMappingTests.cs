using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class NoInputSocketMappingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyGlobalListen_IsNoSpeechInsteadOfEmptyQuoteNoMatch(bool redirectToIdle)
    {
        var rules = new[] { "launch", "globals/global_commands_launch" };
        var replies = redirectToIdle
            ? ResponsePlanToSocketMessagesMapper.MapNoInputAndRedirectToSkill("no-speech", rules, "@be/idle")
            : ResponsePlanToSocketMessagesMapper.MapNoInput("no-speech", rules);
        using var listen = JsonDocument.Parse(replies[0].Text);
        Assert.Equal(!redirectToIdle, listen.RootElement.GetProperty("final").GetBoolean());
        using var finalReply = JsonDocument.Parse(replies[redirectToIdle ? 2 : 0].Text);
        Assert.True(finalReply.RootElement.GetProperty("final").GetBoolean());
        var data = listen.RootElement.GetProperty("data");
        Assert.Equal(string.Empty, data.GetProperty("asr").GetProperty("text").GetString());
        Assert.Equal(string.Empty, data.GetProperty("nlu").GetProperty("intent").GetString());
        // SharedGlobalEvents in the robot SDK checks ASR annotations before its
        // no-match branch. Without one, launch + no match dispatches Idle's "" UI.
        Assert.False(RobotWouldShowUnknownCommand(data));
        Assert.Equal("SOS_TIMEOUT", data.GetProperty("asr").GetProperty("annotation").GetString());
        Assert.Equal(redirectToIdle ? 3 : 2, replies.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FallbackAndNativeCompletion_CloseHubEnvelope(bool nativeCompletion)
    {
        var replies = nativeCompletion
            ? ResponsePlanToSocketMessagesMapper.Map(new ResponsePlan
            {
                IntentName = "word_of_the_day",
                Actions = { new InvokeNativeSkillAction { SkillName = "@be/word-of-the-day" } }
            }, new TurnContext { Attributes = new Dictionary<string, object?> { ["transID"] = "complete" } },
                new CloudSession(), emitSkillActions: false)
            : ResponsePlanToSocketMessagesMapper.MapFallback("complete", ["launch"]);
        using var reply = JsonDocument.Parse(replies.Last().Text);
        Assert.Equal("SKILL_ACTION", reply.RootElement.GetProperty("type").GetString());
        // LhubClient ends its turn using the envelope flag. The existing nested
        // completion marker cannot replace it.
        Assert.True(reply.RootElement.GetProperty("final").GetBoolean());
        Assert.True(reply.RootElement.GetProperty("data").GetProperty("final").GetBoolean());
    }

    [Fact]
    public void RobotNoMatchContract_StillRejectsRealUnmatchedSpeech()
    {
        using var listen = JsonDocument.Parse("""
            {"asr":{"text":"unrelated words"},"nlu":{"intent":"","rules":["launch"],"entities":{}}}
            """);
        Assert.True(RobotWouldShowUnknownCommand(listen.RootElement));
    }

    private static bool RobotWouldShowUnknownCommand(JsonElement data)
    {
        var asr = data.GetProperty("asr");
        if (asr.TryGetProperty("annotation", out var annotation) &&
            annotation.GetString() is "GARBAGE" or "SOS_TIMEOUT" or "MAX_SPEECH_TIMEOUT")
            return false;
        var nlu = data.GetProperty("nlu");
        var hasNlu = !string.IsNullOrEmpty(nlu.GetProperty("intent").GetString()) ||
                     nlu.GetProperty("entities").EnumerateObject().Any();
        var noMatch = !hasNlu && !string.IsNullOrEmpty(asr.GetProperty("text").GetString());
        return noMatch || (!data.TryGetProperty("match", out var match) || match.ValueKind == JsonValueKind.Null) &&
            nlu.GetProperty("rules").EnumerateArray().Any(rule => rule.GetString() == "launch");
    }
}
