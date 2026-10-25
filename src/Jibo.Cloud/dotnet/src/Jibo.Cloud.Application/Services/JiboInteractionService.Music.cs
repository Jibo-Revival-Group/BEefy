using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    private static JiboInteractionDecision BuildMusicLaunchDecision(TurnContext turn, string transcript)
    {
        if (!MusicCommandParser.TryParse(transcript, out var request) || request is null)
            return new("music_play", "Ask me to play a song by name.");
        if (!RobotFlavorClassifier.IsBeam(turn.FirmwareVersion))
            return new("music_play", "Song requests need an updated BEam music player.");
        return new("music_play", string.Empty, "@be/music", new Dictionary<string, object?>
        {
            ["skillId"] = "@be/music", ["nativeLaunch"] = true, ["localIntent"] = "playSong",
            ["nluEntities"] = new Dictionary<string, object?>
            {
                ["query"] = request.Query, ["artist"] = request.Artist ?? string.Empty
            }
        });
    }
}
