using System.Text.Json;
using Jibo.Runtime.Abstractions;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Server changes that need robot code from the beta (e.g. the sports card needs BEam's data: image support). A robot
/// gets them when its CONTEXT carries <c>general.beta: true</c> (BEnch SSM sends it only on 192.168.7.165) or its
/// release is newer than <see cref="CurrentOtaRelease"/>, so after the next OTA every robot does, tag or not.
/// </summary>
public static class BetaFeatures
{
    public const string CurrentOtaRelease = "3.1.8";

    public static bool IsEnabled(TurnContext turn) =>
        IsNewerThanCurrentOta(turn.FirmwareVersion) ||
        turn.Attributes.TryGetValue("context", out var context) && SendsBetaTag(context?.ToString());

    public static bool IsNewerThanCurrentOta(string? release) =>
        Version.TryParse(release?.Trim(), out var version) && version > Version.Parse(CurrentOtaRelease);

    private static bool SendsBetaTag(string? context)
    {
        if (string.IsNullOrWhiteSpace(context)) return false;
        try
        {
            using var document = JsonDocument.Parse(context);
            var root = document.RootElement;
            var data = root.TryGetProperty("data", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
            return data.TryGetProperty("general", out var general) && general.ValueKind == JsonValueKind.Object &&
                   general.TryGetProperty("beta", out var beta) && beta.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
