using System.Text.RegularExpressions;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed partial class JiboInteractionService
{
    private JiboInteractionDecision BuildRememberNameDecision(TurnContext turn, string transcript)
    {
        var name = TryExtractNameFact(transcript);
        if (string.IsNullOrWhiteSpace(name))
            return new JiboInteractionDecision(
                "memory_set_name",
                "Household names are managed in BEacon People on the robot.");

        // Spoken names are not stored in cloud personal memory — BEacon owns the loop roster.
        return new JiboInteractionDecision(
            "memory_set_name",
            $"Nice to meet you, {ToDisplayName(name)}. Add or edit household names in BEacon People on the robot.");
    }

    private JiboInteractionDecision BuildRecallNameDecision(TurnContext turn, GreetingPresenceProfile? presence = null)
    {
        presence ??= ResolveGreetingPresenceProfile(turn);

        string? name = null;
        // Fall back to the robot's loop roster firstName — BEacon is the source of truth.
        if (CanUseLoopLevelNameMemoryFallback(presence) &&
            !string.IsNullOrWhiteSpace(presence.PrimaryPersonId) &&
            presence.LoopUserFirstNames.TryGetValue(presence.PrimaryPersonId, out var loopFirstName) &&
            !string.IsNullOrWhiteSpace(loopFirstName))
            name = loopFirstName;

        if (string.IsNullOrWhiteSpace(name) &&
            CanUseLoopLevelNameMemoryFallback(presence) &&
            presence.LoopUserFirstNames.Count == 1)
            name = presence.LoopUserFirstNames.Values.FirstOrDefault();

        name = ToDisplayName(name ?? string.Empty);

        return string.IsNullOrWhiteSpace(name)
            ? new JiboInteractionDecision(
                "memory_get_name",
                "I do not know your name yet. Add yourself in BEacon People on the robot.")
            : new JiboInteractionDecision(
                "memory_get_name",
                presence.HasKnownIdentity || !string.IsNullOrWhiteSpace(presence.PrimaryPersonId)
                    ? $"I think you are {name}."
                    : $"I think your name is {name}.");
    }

    private static bool CanUseLoopLevelNameMemoryFallback(GreetingPresenceProfile? presence)
    {
        if (presence is null) return true;
        if (string.IsNullOrWhiteSpace(presence.PrimaryPersonId)) return true;

        return presence.PeoplePresentIds.Count <= 1;
    }

    private static DateOnly? TryParseBirthdayDate(string birthdayText)
    {
        if (string.IsNullOrWhiteSpace(birthdayText)) return null;

        var normalized = birthdayText.Trim().ToLowerInvariant();
        var match = Regex.Match(
            normalized,
            @"\b(?<month>january|february|march|april|may|june|july|august|september|october|november|december)\s+(?<day>\d{1,2})(?:st|nd|rd|th)?\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        var month = match.Groups["month"].Value.ToLowerInvariant() switch
        {
            "january" => 1,
            "february" => 2,
            "march" => 3,
            "april" => 4,
            "may" => 5,
            "june" => 6,
            "july" => 7,
            "august" => 8,
            "september" => 9,
            "october" => 10,
            "november" => 11,
            "december" => 12,
            _ => 0
        };
        if (month == 0) return null;

        if (!int.TryParse(match.Groups["day"].Value, out var day) || day is < 1 or > 31) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var year = today.Year;
        if (day > DateTime.DaysInMonth(year, month)) return null;

        DateOnly birthday;
        try
        {
            birthday = new DateOnly(year, month, day);
        }
        catch
        {
            return null;
        }

        if (birthday < today) birthday = birthday.AddYears(1);
        return birthday;
    }
}
