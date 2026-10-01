using System.Text.Json;
using Jibo.Cloud.Domain.Models;

namespace Jibo.Cloud.Infrastructure.Persistence;

public sealed partial class PostgreSqlCloudStateStore
{
    public UserRecord? FindUserByAccessKeyId(string accessKeyId) =>
        string.IsNullOrWhiteSpace(accessKeyId)
            ? null
            : Sync(RequireUsers().GetByAccessKeyIdAsync(accessKeyId));

    public UserRecord ChangeUserPassword(string userId, string newPassword) =>
        Sync(RequireUsers().ChangePasswordAsync(userId, newPassword));

    public UserRecord RotateUserKeys(string userId) =>
        Sync(RequireUsers().RotateKeysAsync(userId));

    public void SetPasswordResetCode(string userId, string code, DateTimeOffset expiresUtc)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("A user id is required.", nameof(userId));
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("A reset code is required.", nameof(code));

        var user = GetUserById(userId) ?? throw new InvalidOperationException($"User '{userId}' not found.");
        Sync(_authTokens.IssueAsync(code.Trim(), "access", user.Id, null, expiresUtc,
            new Dictionary<string, object?>
            {
                ["purpose"] = "password-reset",
                ["email"] = user.Email
            }));
    }

    public UserRecord? RedeemPasswordReset(string code, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(newPassword))
            return null;

        var stored = Sync(_authTokens.FindValidAsync(code.Trim()));
        if (stored is null || !string.Equals(MetadataText(stored.Metadata, "purpose"), "password-reset",
                StringComparison.Ordinal))
            return null;
        if (string.IsNullOrWhiteSpace(stored.AccountId))
            return null;

        var updated = ChangeUserPassword(stored.AccountId, newPassword);
        Sync(_authTokens.RevokeAsync(code.Trim()));
        return updated;
    }

    public string IssueAccountAccessToken(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("An account id is required.", nameof(accountId));

        var token = $"access-{accountId.Trim()}-{Guid.NewGuid():N}";
        Sync(_authTokens.IssueAsync(token, "access", accountId.Trim(), null, DateTimeOffset.UtcNow.AddHours(1),
            new Dictionary<string, object?> { ["purpose"] = "account-access" }));
        return token;
    }

    public string? FindAccountAccessTokenOwnerId(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var stored = Sync(_authTokens.FindValidAsync(token.Trim()));
        if (stored is null || !string.Equals(stored.TokenKind, "access", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!string.Equals(MetadataText(stored.Metadata, "purpose"), "account-access", StringComparison.Ordinal))
            return null;
        return stored.AccountId;
    }

    public void SaveOobeSetup(OobeSetupRecord setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        if (string.IsNullOrWhiteSpace(setup.Token))
            throw new ArgumentException("An OOBE token is required.", nameof(setup));
        if (setup.ExpiresUtc <= DateTimeOffset.UtcNow)
            return;

        Sync(_authTokens.RevokeAsync(setup.Token));
        Sync(_authTokens.IssueAsync(setup.Token, "oobe", setup.UserId, setup.DeviceId, setup.ExpiresUtc,
            new Dictionary<string, object?>
            {
                ["loopId"] = setup.LoopId,
                ["targetMode"] = setup.TargetMode,
                ["targetHost"] = setup.TargetHost,
                ["rollbackSnapshotId"] = setup.RollbackSnapshotId,
                ["complete"] = setup.Complete ? "true" : "false"
            }));
    }

    public OobeSetupRecord? FindOobeSetup(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var stored = Sync(_authTokens.FindValidAsync(token.Trim()));
        if (stored is null || !string.Equals(stored.TokenKind, "oobe", StringComparison.OrdinalIgnoreCase))
            return null;

        return new OobeSetupRecord
        {
            Token = token.Trim(),
            UserId = stored.AccountId,
            DeviceId = stored.DeviceId,
            LoopId = MetadataText(stored.Metadata, "loopId"),
            TargetMode = MetadataText(stored.Metadata, "targetMode") ?? "open-jibo",
            TargetHost = MetadataText(stored.Metadata, "targetHost"),
            RollbackSnapshotId = MetadataText(stored.Metadata, "rollbackSnapshotId"),
            Complete = string.Equals(MetadataText(stored.Metadata, "complete"), "true", StringComparison.OrdinalIgnoreCase),
            ExpiresUtc = stored.ExpiresUtc
        };
    }

    private static string? MetadataText(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || value is null)
            return null;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
