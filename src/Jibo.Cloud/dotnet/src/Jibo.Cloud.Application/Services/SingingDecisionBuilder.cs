using System.Text;
using System.Xml.Linq;
using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

internal sealed record SingingMimPrompt(string Esml, string MimId, string? PromptId);

internal static class SingingDecisionBuilder
{
    internal static bool IsSingingIntent(string? intent) =>
        string.Equals(intent, "robot_can_sing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(intent, "robot_sing_christmas_song", StringComparison.OrdinalIgnoreCase);

    internal static JiboInteractionDecision Build(
        JiboExperienceCatalog catalog, IJiboRandomizer randomizer, bool holiday,
        DateTimeOffset? referenceLocalTime = null)
    {
        var intent = holiday ? "robot_sing_christmas_song" : "robot_can_sing";
        var context = LegacyMimScriptedReplyBuilder.BuildScriptedContext(referenceLocalTime);
        var prompts = new List<SingingMimPrompt>();
        var spokenText = new StringBuilder();

        void AppendMim(string mimId, bool songOnly = false)
        {
            if (!LegacyMimScriptedReplyBuilder.TrySelectMimReply(catalog, randomizer,
                    intent, context, null, mimId, [], out var selection)) return;
            var selected = selection!;
            var original = catalog.MimReplies[mimId].First(reply => reply.PromptId == selected.PromptId);
            var root = XElement.Parse($"<speak>{original.OriginalEsml ?? selected.ReplyText}</speak>");
            var nodes = root.Nodes().ToArray();
            if (songOnly)
            {
                // The original favorite-singer MIM starts with a conversational
                // reply. Its Twinkle performance starts at the first duration tag.
                nodes = nodes.SkipWhile(node => node is not XElement { Name.LocalName: "duration" }).ToArray();
            }
            var chunk = new StringBuilder();
            void Flush()
            {
                if (chunk.Length == 0) return;
                prompts.Add(new SingingMimPrompt($"<speak>{chunk}</speak>", mimId, selected.PromptId));
                chunk.Clear();
            }
            foreach (var node in nodes)
            {
                var markup = node.ToString(SaveOptions.DisableFormatting);
                if (chunk.Length + markup.Length > 380) Flush();
                // Split only between complete top-level nodes: all original
                // pitch, duration, phoneme and style markup stays intact.
                chunk.Append(markup);
                spokenText.Append(node is XElement element ? element.Value : ((XText)node).Value);
            }
            Flush();
            spokenText.Append(' ');
        }

        if (holiday)
            AppendMim("RI_JBO_KnowsJingleBellsSong");
        else
        {
            AppendMim("RA_JBO_Sing");
            AppendMim("RI_JBO_HasFavoriteSinger", songOnly: true);
        }

        // Missing imported content must not result in an empty playback action.
        if (prompts.Count == 0)
            return new JiboInteractionDecision(intent, "I don't have a song ready right now.",
                ContextUpdates: ScriptedResponseDecisionBuilder.BuildScriptedResponseContextUpdates());

        return new JiboInteractionDecision(intent, spokenText.ToString().Trim(), "chitchat-skill",
            new Dictionary<string, object?>
            {
                ["esml"] = prompts[0].Esml,
                ["singing_mim_sequence"] = prompts.ToArray(),
                ["mim_id"] = prompts[0].MimId,
                ["prompt_id"] = prompts[0].PromptId,
                ["mim_type"] = "announcement"
            }, ScriptedResponseDecisionBuilder.BuildScriptedResponseContextUpdates());
    }
}
