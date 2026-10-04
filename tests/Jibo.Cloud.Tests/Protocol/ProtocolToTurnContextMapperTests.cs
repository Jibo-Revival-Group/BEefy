using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Protocol;

public sealed class ProtocolToTurnContextMapperTests
{
    [Theory]
    [InlineData("AUTO_FINALIZE", true, false, TurnInputMode.WakeWord)]
    [InlineData("CLIENT_ASR", false, false, TurnInputMode.WakeWord)]
    [InlineData("CLIENT_NLU", false, true, TurnInputMode.WakeWord)]
    [InlineData("LISTEN", false, false, TurnInputMode.DirectText)]
    public void MapListenMessage_ClassifiesSpeechSeparatelyFromTypedText(
        string messageType, bool bufferedAudio, bool nestedAsr, TurnInputMode expected)
    {
        var session = new CloudSession();
        if (bufferedAudio)
        {
            session.TurnState.BufferedAudioBytes = 3;
            session.TurnState.BufferedAudioFrames.Add([1, 2, 3]);
        }
        var envelope = new WebSocketMessageEnvelope
        {
            Text = nestedAsr ? """{"data":{"asr":{"text":"make a peter sir"}}}""" :
                """{"data":{"text":"make a peter sir"}}"""
        };
        Assert.Equal(expected, ProtocolToTurnContextMapper.MapListenMessage(envelope, session, messageType).InputMode);
        session.FollowUpExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.Equal(TurnInputMode.FollowUp,
            ProtocolToTurnContextMapper.MapListenMessage(envelope, session, messageType).InputMode);
    }

    [Fact]
    public void MapListenMessage_PreservesHouseholdListMetadata()
    {
        var session = new CloudSession
        {
            AccountId = "acct-123",
            DeviceId = "device-123",
            Metadata = new Dictionary<string, object?>
            {
                ["householdListState"] = "awaiting_item",
                ["householdListType"] = "shopping",
                ["householdListDisplayType"] = "grocery"
            }
        };

        var envelope = new WebSocketMessageEnvelope
        {
            HostName = "api.jibo.com",
            Text = """{"data":{"text":"add milk"}}"""
        };

        var turn = ProtocolToTurnContextMapper.MapListenMessage(envelope, session, "LISTEN");

        Assert.Equal("add milk", turn.NormalizedTranscript);
        Assert.Equal("awaiting_item", turn.Attributes["householdListState"]);
        Assert.Equal("shopping", turn.Attributes["householdListType"]);
        Assert.Equal("grocery", turn.Attributes["householdListDisplayType"]);
    }
}