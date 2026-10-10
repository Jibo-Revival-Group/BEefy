using Jibo.Cloud.Application.Services;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Tests.Application;

public sealed class NativePromptContextTests
{
    [Fact]
    public void ResolvesLoopOwnerSpeakerAndKnownBirthdateFromRobotRuntime()
    {
        var turn = new TurnContext { Attributes = new Dictionary<string, object?> { ["context"] = """
            {"data":{"runtime":{"location":{"iso":"2026-01-01T12:00:00Z","city":"Boston"},
            "perception":{"speaker":"person"},"dialog":{"referent":"person"},"character":{"emotion":{"name":"JOYFUL"}},
            "loop":{"owner":"person","users":[{"id":"person","firstName":"Amber","phoneticName":"Amber","gender":"female","birthdate":"2000-01-01"}]}}}}
            """ } };
        var values = new Dictionary<string, string>();
        NativePromptContext.Populate(turn, values);
        Assert.Equal("Amber", values["speaker"]);
        Assert.Equal("Amber", values["loop.owner"]);
        Assert.Equal("Amber", values["referent"]);
        Assert.Equal("1", values["loop.count"]);
        Assert.Equal("26 years old", values["speaker.age"]);
        Assert.Equal("Boston", values["location.city"]);
        var context = new LegacyMimConditionEvaluator.Context(null, null, new DateOnly(2026, 1, 1));
        Assert.True(NativePromptContext.Matches("!!loop.owner && !!speaker && (loop.owner.id === speaker.id)", values, context));
        Assert.True(NativePromptContext.Matches("!!referent && referent.gender == 'female'", values, context));
        Assert.False(NativePromptContext.Matches("referent.gender == 'male'", values, context));
    }
}
