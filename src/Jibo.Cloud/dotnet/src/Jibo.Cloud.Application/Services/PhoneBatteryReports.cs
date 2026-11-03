using System.Collections.Concurrent;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Phone batteries BEep reports to a robot, which forwards them here (SetPhoneBattery) so the
/// personal report can mention the listener's phone.
/// </summary>
// shortcut: in memory only; a restart forgets them until each phone next reports (BEep does every few minutes to hours).
public static class PhoneBatteryReports
{
    public sealed record Reading(string MemberId, string? MemberName, int Level, bool Charging, DateTimeOffset AtUtc);

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Reading>> ByRobot = new(StringComparer.Ordinal);

    // The report skips a phone it hasn't heard from in this long.
    private static readonly TimeSpan ReportWindow = TimeSpan.FromHours(2);
    private const int Low = 20;

    public static void Save(string robotScope, Reading reading) =>
        ByRobot.GetOrAdd(robotScope, _ => new ConcurrentDictionary<string, Reading>(StringComparer.Ordinal))[reading.MemberId] = reading;

    /// <summary>The report's phone line for <paramref name="userName"/>, or null when there's nothing recent to say.</summary>
    public static string? ReportLine(string? robotScope, string? userName, DateTimeOffset now)
    {
        if (robotScope is null || !ByRobot.TryGetValue(robotScope, out var phones)) return null;
        var recent = phones.Values.Where(reading => now - reading.AtUtc <= ReportWindow).ToArray();
        var reading = Pick(recent, userName);
        if (reading is null) return null;

        var age = now - reading.AtUtc;
        var minutes = (int)Math.Round(age.TotalMinutes);
        var line = minutes < 2
            ? $"Your phone is at {reading.Level} percent{(reading.Charging ? " and charging" : string.Empty)}."
            : $"As of {Ago(minutes)}, your phone was at {reading.Level} percent{(reading.Charging ? " and charging" : string.Empty)}.";
        return reading.Level < Low && !reading.Charging ? line + " You better charge it before you go!" : line;
    }

    private static Reading? Pick(Reading[] recent, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return recent.Length == 1 ? recent[0] : null;
        var spoken = FirstName(userName);
        return recent.FirstOrDefault(reading => reading.MemberName is { } name && FirstName(name) == spoken);
    }

    private static string FirstName(string name) =>
        name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? string.Empty;

    private static string Ago(int minutes) =>
        minutes < 60 ? $"{minutes} minutes ago" : minutes < 90 ? "an hour ago" : $"{(int)Math.Round(minutes / 60.0)} hours ago";

    public static string? RobotScopeFor(Jibo.Runtime.Abstractions.TurnContext turn)
    {
        var robot = string.IsNullOrWhiteSpace(turn.DeviceId) ? null : turn.DeviceId.Trim();
        robot ??= turn.Attributes.TryGetValue("deviceId", out var device) && !string.IsNullOrWhiteSpace(device?.ToString()) ? device!.ToString()!.Trim() : null;
        robot ??= turn.Attributes.TryGetValue("robotId", out var id) && !string.IsNullOrWhiteSpace(id?.ToString()) ? id!.ToString()!.Trim() : null;
        return robot is null ? null : MemberCalendarFeedRecord.RobotScope(robot);
    }
}
