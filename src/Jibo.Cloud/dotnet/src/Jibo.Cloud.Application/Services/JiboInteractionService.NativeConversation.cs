using Jibo.Cloud.Application.Abstractions;
using System.Text.Json;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    internal const string NativeParseAttribute = "nativeParseResult";

    private async Task<JiboInteractionDecision?> TryBuildNativeConversationDecisionAsync(
        TurnContext turn, string transcript, string semanticIntent, JiboExperienceCatalog catalog,
        IReadOnlyDictionary<string, string> clientEntities, string? clientIntent,
        IReadOnlyList<string> clientRules, IReadOnlyList<string> listenRules,
        bool isSkillOwnedListen, bool isYesNoTurn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Existing .NET commands and enrolled HA routes remain authoritative.
        var explicitLocal = clientEntities.TryGetValue("skill", out var clientSkill)
            && NativeCommandRegistry.Instance.LocalSkills.Contains(clientSkill);
        if (semanticIntent is not ("chat" or "native_command") && !explicitLocal && clientSkill is not ("example-skill" or "template-skill")
            && !(isSkillOwnedListen && semanticIntent == "skill_listen") || isYesNoTurn) return null;
        if (SkillListenOwnership.IsCloudOwnedFollowUp(turn) &&
            NativeConversationValue.Read(turn.Attributes, "chitchatNativeState")?.ToString() != "color") return null;

        if (isSkillOwnedListen && (TranscriptHeuristics.IsLikelyPromptEchoTranscript(transcript.ToLowerInvariant())
            || TranscriptHeuristics.IsLikelySkillOfferPromptEcho(transcript.ToLowerInvariant()))) return null;
        if (!isSkillOwnedListen && semanticIntent == "chat")
        {
            var emotion = ChitchatStateMachine.TryBuildChatEmotionDecision(transcript.ToLowerInvariant(), catalog, randomizer,
                NativeConversationValue.Read(turn.Attributes, ChitchatStateMachine.EmotionMetadataKey)?.ToString(),
                ResolvePreferredGreetingName(turn, ResolveGreetingPresenceProfile(turn)));
            if (emotion is not null) return emotion;
        }
        NativeParseResult? parsed;
        if (!isSkillOwnedListen && System.Text.RegularExpressions.Regex.IsMatch(transcript.ToLowerInvariant(),
            @"^(?:let'?s talk|talk about) colou?rs[.!?]?$")) return BuildNativeColorQuestion();
        if (clientIntent == "favoriteColorChat") return BuildNativeColorQuestion();
        if (!string.IsNullOrWhiteSpace(clientIntent) && (explicitLocal || NativeCommandRegistry.Instance.Commands.Any(c => c.Intent == clientIntent)))
            parsed = new(clientIntent, clientSkill, clientEntities.GetValueOrDefault("domain"), clientRules,
                null, clientEntities.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));
        else
        {
            var names = ResolveGreetingPresenceProfile(turn).LoopUserFirstNames.Values.ToArray();
            var requested = isSkillOwnedListen ? listenRules.Concat(clientRules).Distinct().ToArray() : ["launch"];
            parsed = NativeGrammar.Instance.Parse(transcript, requested, names);
        }
        if (parsed is null) return null;
        if (isSkillOwnedListen)
        {
            // A contextual rule returns NLU to the owning robot skill, without a second launch.
            turn.Attributes[NativeParseAttribute] = parsed;
            return new("skill_listen", string.Empty);
        }
        if (parsed.Intent == "whoIsPerson" && transcript.Trim().Equals("who is jibo", StringComparison.OrdinalIgnoreCase)) return null;
        var entry = NativeCommandRegistry.Instance.Resolve(parsed);
        if (entry is null)
        {
            var global = await TryBuildNativeGlobalDecisionAsync(parsed, turn, catalog, cancellationToken);
            if (global is not null) turn.Attributes[NativeParseAttribute] = parsed;
            return global;
        }
        turn.Attributes[NativeParseAttribute] = parsed with { Skill = entry.Skill };
        if (entry.OnRobot)
            return new(parsed.Intent, string.Empty, entry.Skill, new Dictionary<string, object?>
            {
                ["skillId"] = entry.Skill, ["localIntent"] = parsed.Intent,
                ["nativeLaunch"] = true, ["nluEntities"] = parsed.Entities,
                ["nluRules"] = parsed.Rules, ["nluDomain"] = parsed.Domain
            });
        switch (entry.Skill)
        {
            case "report-skill":
                var mapped = parsed.Intent switch
                {
                    "requestWeatherPR" => "weather", "requestCalendar" => "calendar",
                    "requestCommute" => "commute", "requestNews" => "news", _ => "personal_report"
                };
                // Existing providers read the same client-entity interface as robot NLU.
                turn.Attributes["clientEntities"] = parsed.Entities.ToDictionary(p => p.Key, p => p.Value?.ToString() ?? "");
                return await PersonalReportOrchestrator.TryBuildDecisionAsync(turn, mapped, transcript.ToLowerInvariant(), catalog,
                    BuildWeatherReportDecisionAsync, BuildCalendarReportDecisionAsync, BuildCommuteReportDecisionAsync,
                    (context, ct) => BuildNewsDecisionAsync(context, transcript, catalog, ct, includeOutro: false), cancellationToken)
                    ?? (mapped == "weather" ? await BuildWeatherReportDecisionAsync(turn, transcript, cancellationToken)
                        : mapped == "news" ? await BuildNewsDecisionAsync(turn, transcript, catalog, cancellationToken)
                        : mapped == "calendar" ? await BuildCalendarReportDecisionAsync(turn, cancellationToken)
                        : mapped == "commute" ? await BuildCommuteReportDecisionAsync(turn, cancellationToken)
                        : new JiboInteractionDecision(mapped, "I couldn't get your personal report right now."));
            case "answer-skill":
                return await BuildChatFallbackDecisionAsync(catalog, transcript, transcript.ToLowerInvariant(),
                    NativeConversationValue.Read(turn.Attributes, ChitchatStateMachine.EmotionMetadataKey)?.ToString(),
                    ResolvePreferredGreetingName(turn, ResolveGreetingPresenceProfile(turn)), cancellationToken);
            case "color-skill": return BuildNativeColorQuestion();
            case "template-skill":
                return new("template_skill", "This is a template skill", "template-skill");
            case "example-skill":
                return new(parsed.Intent, "SLIM: 'Node1' MEMO: 'SomeThing' SLIM: 'Node2' MEMO: 'SomeThing' SLIM: 'Node3' MEMO: 'SomeThing'", "example-skill");
            case "chitchat-skill":
                return NativeScriptedReplies.Build(entry, parsed, turn, catalog, randomizer,
                    ResolvePreferredGreetingName(turn, ResolveGreetingPresenceProfile(turn)), TryResolveReferenceLocalTime(turn));
            default: throw new InvalidOperationException($"No native handler for registered cloud skill {entry.Skill}.");
        }
    }

    private Task<JiboInteractionDecision?> TryBuildNativeGlobalDecisionAsync(NativeParseResult parsed,
        TurnContext turn, JiboExperienceCatalog catalog, CancellationToken cancellationToken)
    {
        JiboInteractionDecision? result = parsed.Intent switch
        {
            "stop" => BuildStopDecision(), "sleep" => BuildSleepDecision(), "requestWakeUp" => BuildWakeUpDecision(),
            "turnAround" => BuildIdleGlobalCommandDecision("turn_around", "turnAround", "Don't mind if I do."),
            "spinAround" => BuildIdleGlobalCommandDecision("spin_around", "spinAround", "Don't mind if I do."),
            "volumeUp" => BuildVolumeControlDecision("volume_up", "volumeUp", "null"),
            "volumeDown" => BuildVolumeControlDecision("volume_down", "volumeDown", "null"),
            "volumeToValue" => BuildVolumeControlDecision("volume_to_value", "volumeToValue", parsed.Entities.GetValueOrDefault("volumeLevel")?.ToString() ?? "null"),
            "repeat" => null,
            "mainMenu" => BuildNativeMenu("main-menu"),
            "overHere" or "turnAway" => new(parsed.Intent, string.Empty, "@be/idle", new Dictionary<string, object?>
            { ["globalIntent"] = parsed.Intent, ["nluDomain"] = "global_commands", ["nativeGlobal"] = true }),
            "left" or "right" or "beginning" or "end" or "close" or "selectItem" or "holdOn" or "thanks" => new("skill_listen", string.Empty),
            "help" => BuildNativeMenu("friendly-tips"),
            "loadMenu" when parsed.Entities.TryGetValue("destination", out var destination) => BuildNativeMenu(destination?.ToString()),
            _ => null
        };
        if (parsed.Intent == "repeat") return Repeat();
        return Task.FromResult(result);
        async Task<JiboInteractionDecision?> Repeat() => await BuildRepeatLastCommandDecisionAsync(turn, cancellationToken);
    }

    private static JiboInteractionDecision? BuildNativeClassifierDecision(string intent)
    {
        if (!NativeCommandRegistry.Instance.ClassifierCommands.TryGetValue(intent, out var command)) return null;
        return new(intent, string.Empty, command.Skill, new Dictionary<string, object?>
        {
            ["skillId"] = command.Skill, ["localIntent"] = command.Intent, ["nativeLaunch"] = true,
            ["nluDomain"] = command.Skill[4..], ["nluEntities"] = new Dictionary<string, object?> { ["skill"] = command.Skill }
        });
    }

    private static JiboInteractionDecision? BuildNativeMenu(string? destination)
    {
        var skill = destination switch
        {
            "snapshot" or "photobooth" => "@be/create", "photos" or "photo-gallery" => "@be/gallery",
            "main" or "main-menu" => "@be/main-menu", _ when destination is not null => "@be/" + destination,
            _ => null
        };
        if (skill is null || !NativeCommandRegistry.Instance.LocalSkills.Contains(skill)) return null;
        var menuIntent = skill switch
        {
            "@be/main-menu" => "launchMainMenu", "@be/friendly-tips" => "whatCanIDo",
            "@be/settings" or "@be/clock" => "menu", "@be/create" => "createOnePhoto",
            "@be/gallery" => "galleryOpen", "@be/radio" => "showStations", "@be/word-of-the-day" => "play",
            "@be/tutorial" => "tutorialOpen", "@be/exercise" => "exerciseDoYoga",
            "@be/circuit-saver" => "launchGame", "@be/hue-control" => "lightsHowTo",
            "@be/surprises-ota" => "releaseNotes", "@be/who-am-i" => "launchWhoAmI", _ => null
        };
        var entry = NativeCommandRegistry.Instance.Commands.FirstOrDefault(c => c.Skill == skill && c.Intent == menuIntent);
        if (entry is null) return null;
        return new(entry.Intent, string.Empty, skill, new Dictionary<string, object?>
        { ["skillId"] = skill, ["localIntent"] = entry.Intent, ["nativeLaunch"] = true });
    }

    private static JiboInteractionDecision BuildNativeColorQuestion() => new("favoriteColorChat",
        "What's your favorite color?", "color-skill", new Dictionary<string, object?>
        { ["nativeCloudDialog"] = true, ["listen_contexts"] = new[] { "global" }, ["listen_asr_hints"] = new[] { "$ANYTHING" } },
        new Dictionary<string, object?> { ["chitchatNativeState"] = "color" });

    private static JiboInteractionDecision? TryBuildNativeColorReply(TurnContext turn, string transcript)
    {
        if (NativeConversationValue.Read(turn.Attributes, "chitchatNativeState")?.ToString() != "color"
            || SkillListenOwnership.ReadListenHotphrase(turn)) return null;
        var state = new Dictionary<string, object?> { ["chitchatNativeState"] = null };
        if (string.IsNullOrWhiteSpace(transcript)) return BuildNativeColorQuestion();
        if (transcript.Trim().ToLowerInvariant() is "stop" or "cancel" or "never mind")
            return new("stop", string.Empty, ContextUpdates: state);
        return new("favoriteColorChat", $"Oh, {transcript.Trim()} is a great color! Mine is teal.", "color-skill", ContextUpdates: state);
    }
}
