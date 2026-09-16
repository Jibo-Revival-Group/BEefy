namespace Jibo.Cloud.Domain.Models;

/// <summary>
/// Stable fingerprint of robot-reported loop users so roster sync can skip no-op upserts.
/// </summary>
public static class LoopUserRosterFingerprint
{
    public static string BuildKey(string loopId, string robotId) =>
        $"{loopId.Trim()}|{robotId.Trim()}";

    public static string Compute(IReadOnlyList<LoopUserSnapshot> loopUsers)
    {
        if (loopUsers is null || loopUsers.Count == 0)
            return string.Empty;

        return string.Join('\n',
            loopUsers
                .Where(static user =>
                    !string.IsNullOrWhiteSpace(user.Id) &&
                    !string.Equals(user.Type, "robot", StringComparison.OrdinalIgnoreCase))
                .Select(static user =>
                    string.Join('|',
                        user.Id.Trim().ToLowerInvariant(),
                        Normalize(user.FirstName),
                        Normalize(user.LastName),
                        Normalize(user.Type),
                        Normalize(user.Nickname)))
                .OrderBy(static part => part, StringComparer.Ordinal));
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
}
