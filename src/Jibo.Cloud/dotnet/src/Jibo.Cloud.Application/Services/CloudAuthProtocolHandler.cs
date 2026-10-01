using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jibo.Cloud.Application.Services;

public sealed class CloudAuthProtocolHandler(
    ICloudStateStore stateStore,
    ILogger<CloudAuthProtocolHandler>? logger = null,
    RobotIdentitySuggestionStore? identitySuggestionStore = null,
    ReleaseSmokeAuthorizationOptions? releaseSmokeAuthorization = null) : ICloudAuthProtocolHandler
{
    private readonly ILogger _logger = logger ?? NullLogger<CloudAuthProtocolHandler>.Instance;
    private readonly ReleaseSmokeAuthorizationOptions _releaseSmokeAuthorization =
        releaseSmokeAuthorization ?? new ReleaseSmokeAuthorizationOptions();
    public ProtocolDispatchResult HandleAccount(string operation, ProtocolEnvelope envelope)
    {
        var account = stateStore.GetAccount();
        var body = envelope.TryParseBody();

        if (operation.Equals("CreateHubToken", StringComparison.OrdinalIgnoreCase))
        {
            var deviceId = !string.IsNullOrWhiteSpace(envelope.DeviceId)
                ? envelope.DeviceId!
                : ReadString(body, "deviceId")
                  ?? ReadString(body, "serial_number")
                  ?? ReadString(body, "serialNumber")
                  ?? ReadString(body, "cpuid")
                  ?? ReadString(body, "cpuId")
                  ?? ReadString(body, "robotId");

            var defaultRobotIsSynthetic = RobotRegistrationSources.IsSynthetic(
                RobotRegistrationSources.Normalize(stateStore.GetRobot().RegistrationSource,
                    stateStore.GetRobot().DeviceId));

            // Real hardware often reaches the cloud through the hub-token flow before it has
            // completed a separate registration exchange. Preserve the observed identity on the
            // durable token/session, but do not promote it into visible inventory. A trusted
            // RobotIdentityLink can attach the session to an existing canonical robot; genuinely
            // new hardware remains an unlinked observed session until it is explicitly registered.
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var registrationSource = envelope.Headers.TryGetValue("X-OpenJibo-Registration-Source",
                    out var sourceHeader)
                    ? sourceHeader
                    : null;
                var smokeHubToken = TryIssueDeploymentSmokeHubToken(deviceId, registrationSource, envelope);
                if (smokeHubToken is not null) return smokeHubToken;
            }

            return ProtocolDispatchResult.Ok(new
            {
                // An empty request must not inherit a deployment-smoke robot as its identity.
                // Leave it unassigned until the physical client provides a real identity signal.
                token = stateStore.IssueHubToken(deviceId, useDefaultRobot: !defaultRobotIsSynthetic),
                expires = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            });
        }

        if (operation.Equals("CreateAccessToken", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveAccountCaller(envelope, out var accessUser, out var accessOwner, out var accessDenied))
                return accessDenied!;

            var accountId = accessUser?.Id ?? accessOwner.AccountId;
            var expires = DateTimeOffset.UtcNow.AddHours(1);
            return ProtocolDispatchResult.Ok(new
            {
                token = stateStore.IssueAccountAccessToken(accountId),
                expires = expires.ToUnixTimeMilliseconds()
            });
        }

        if (operation.Equals("CheckEmail", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            var emailExists = stateStore.GetUserByEmail(email) is not null ||
                              email.Equals(account.Email, StringComparison.OrdinalIgnoreCase);
            return ProtocolDispatchResult.Ok(new { exists = emailExists });
        }

        if (operation.Equals("Create", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            var password = ReadString(body, "password") ?? string.Empty;
            var firstName = ReadString(body, "firstName") ?? string.Empty;
            var lastName = ReadString(body, "lastName") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                return ProtocolDispatchResult.Raw(400, "{\"message\":\"Email and password are required\"}",
                    "application/json");

            var created = stateStore.CreateUser(email, password, firstName, lastName);
            if (created is null)
                return ProtocolDispatchResult.Raw(409,
                    "{\"message\":\"An account with this email already exists\"}",
                    "application/json");

            return ProtocolDispatchResult.Ok(BuildAccountResponse(created));
        }

        if (operation.Equals("Login", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            var password = ReadString(body, "password") ?? string.Empty;

            var authenticated = stateStore.AuthenticateUser(email, password);
            if (authenticated is null)
                return ProtocolDispatchResult.Raw(401,
                    "{\"message\":\"Invalid email or password\"}",
                    "application/json");

            return ProtocolDispatchResult.Ok(BuildAccountResponse(authenticated));
        }

        if (operation.Equals("Get", StringComparison.OrdinalIgnoreCase))
        {
            var ids = ReadStringArray(body, "ids");
            if (ids.Count == 0)
            {
                if (!TryResolveAccountCaller(envelope, out var getUser, out var getOwner, out var getDenied))
                    return getDenied!;
                return ProtocolDispatchResult.Ok(new[]
                {
                    getUser is null ? BuildAccountResponse(getOwner) : BuildAccountResponse(getUser)
                });
            }

            var results = ids
                .Select(id =>
                {
                    var user = stateStore.GetUserById(id);
                    if (user is not null) return BuildAccountResponse(user);
                    return id.Equals(account.AccountId, StringComparison.OrdinalIgnoreCase)
                        ? BuildAccountResponse(account)
                        : null;
                })
                .Where(result => result is not null)
                .ToArray();

            return ProtocolDispatchResult.Ok(results);
        }

        if (operation.Equals("Update", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveAccountCaller(envelope, out var updateUser, out var updateOwner, out var updateDenied))
                return updateDenied!;
            if (updateUser is null)
                return ProtocolDispatchResult.Ok(BuildAccountResponse(updateOwner));

            var updated = stateStore.UpdateUser(updateUser.Id, ReadString(body, "firstName"),
                ReadString(body, "lastName"), ReadString(body, "gender"), ReadInt64(body, "birthday"));
            return ProtocolDispatchResult.Ok(BuildAccountResponse(updated));
        }

        if (operation.Equals("ChangePassword", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveAccountCaller(envelope, out var passwordUser, out _, out var passwordDenied))
                return passwordDenied!;
            if (passwordUser is null)
                return ProtocolDispatchResult.Raw(400,
                    "{\"message\":\"The owner account has no password to change.\"}", "application/json");

            var oldPassword = ReadString(body, "oldPassword") ?? string.Empty;
            var newPassword = ReadString(body, "newPassword") ?? string.Empty;
            if (stateStore.AuthenticateUser(passwordUser.Email, oldPassword) is null)
                return ProtocolDispatchResult.Raw(401, "{\"message\":\"Invalid email or password\"}",
                    "application/json");
            if (string.IsNullOrWhiteSpace(newPassword))
                return ProtocolDispatchResult.Raw(400, "{\"message\":\"Email and password are required\"}",
                    "application/json");

            var changed = stateStore.ChangeUserPassword(passwordUser.Id, newPassword);
            return ProtocolDispatchResult.Ok(BuildAccountResponse(changed));
        }

        if (operation.Equals("ResetKeys", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryResolveAccountCaller(envelope, out var keyUser, out var keyOwner, out var keyDenied))
                return keyDenied!;
            if (keyUser is null)
                return ProtocolDispatchResult.Ok(BuildAccountResponse(keyOwner));

            var rotated = stateStore.RotateUserKeys(keyUser.Id);
            return ProtocolDispatchResult.Ok(BuildAccountResponse(rotated));
        }

        if (operation.Equals("SendPasswordReset", StringComparison.OrdinalIgnoreCase))
        {
            var email = ReadString(body, "email") ?? string.Empty;
            var user = stateStore.GetUserByEmail(email);
            if (user is null)
                return ProtocolDispatchResult.Ok(new { email });

            stateStore.SetPasswordResetCode(user.Id, CreatePasswordResetCode(),
                DateTimeOffset.UtcNow.AddHours(1));
            return ProtocolDispatchResult.Ok(BuildAccountResponse(user));
        }

        if (operation.Equals("PasswordResetByCode", StringComparison.OrdinalIgnoreCase))
        {
            var code = ReadString(body, "code") ?? string.Empty;
            var password = ReadString(body, "password") ?? string.Empty;
            var reset = stateStore.RedeemPasswordReset(code, password);
            if (reset is null)
                return ProtocolDispatchResult.Raw(400, "{\"message\":\"That reset code is invalid or has expired.\"}",
                    "application/json");
            return ProtocolDispatchResult.Ok(BuildAccountResponse(reset));
        }

        if (operation is "Remove" or "ActivateByCode" or "ResendActivationCode" or "ChangeEmail" or
            "ConfirmEmailReset" or "UpdatePhoto" or "RemovePhoto" or "VerifyPhoneByCode" or
            "SendPhoneVerificationCode" or "AcceptTerms" or "FacebookConnect" or "FacebookMobileConnect" or
            "FacebookPrepareLogin")
            return UnsupportedAccountOperation();

        if (operation.Equals("GetAccountByAccessToken", StringComparison.OrdinalIgnoreCase))
        {
            var accessToken = ReadString(body, "token") ?? string.Empty;
            var ownerId = stateStore.FindAccountAccessTokenOwnerId(accessToken);
            if (string.IsNullOrWhiteSpace(ownerId))
                return ProtocolDispatchResult.Raw(401, "{\"message\":\"Invalid access key\"}", "application/json");

            var tokenUser = stateStore.GetUserById(ownerId);
            var namedRobot = ReadString(body, "friendlyId") ?? ReadString(body, "robotId") ??
                             ReadString(body, "deviceId");
            if (tokenUser is not null)
            {
                string? friendlyId = null;
                if (!string.IsNullOrWhiteSpace(namedRobot))
                {
                    var match = stateStore.GetDevicesForUser(tokenUser.Id).FirstOrDefault(device =>
                        device.DeviceId.Equals(namedRobot, StringComparison.OrdinalIgnoreCase) ||
                        device.RobotId.Equals(namedRobot, StringComparison.OrdinalIgnoreCase) ||
                        device.FriendlyName.Equals(namedRobot, StringComparison.OrdinalIgnoreCase));
                    friendlyId = match?.RobotId;
                }

                return ProtocolDispatchResult.Ok(new
                {
                    id = tokenUser.Id,
                    accessKeyId = tokenUser.AccessKeyId,
                    secretAccessKey = tokenUser.SecretAccessKey,
                    email = tokenUser.Email,
                    friendlyId,
                    payload = ReadObject(body, "payload")
                });
            }

            if (!ownerId.Equals(account.AccountId, StringComparison.OrdinalIgnoreCase))
                return ProtocolDispatchResult.Raw(401, "{\"message\":\"Invalid access key\"}", "application/json");

            return ProtocolDispatchResult.Ok(new
            {
                id = account.AccountId,
                accessKeyId = account.AccessKeyId,
                secretAccessKey = account.SecretAccessKey,
                email = account.Email,
                friendlyId = string.IsNullOrWhiteSpace(namedRobot) ? stateStore.GetRobot().RobotId : namedRobot,
                payload = ReadObject(body, "payload")
            });
        }

        if (operation.Equals("Search", StringComparison.OrdinalIgnoreCase))
        {
            var query = (ReadString(body, "query") ?? string.Empty).ToLowerInvariant();
            var haystack = $"{account.Email} {account.FirstName} {account.LastName} {account.AccountId}"
                .ToLowerInvariant();

            return ProtocolDispatchResult.Ok(query.Length > 0 && haystack.Contains(query)
                ?
                [
                    BuildAccountResponse(account)
                ]
                : Array.Empty<object>());
        }

        return ProtocolDispatchResult.Ok(new
        {
            id = account.AccountId,
            email = account.Email,
            firstName = account.FirstName,
            lastName = account.LastName
        });
    }

    public ProtocolDispatchResult HandleNotification(string operation, ProtocolEnvelope envelope)
    {
        if (!operation.Equals("NewRobotToken", StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Ok(new { ok = true, operation });

        var body = envelope.TryParseBody();
        var presentedDeviceId = ReadString(body, "deviceId")
                                ?? ReadString(body, "serial_number")
                                ?? ReadString(body, "serialNumber")
                                ?? ReadString(body, "cpuid")
                                ?? ReadString(body, "cpuId");
        var presentedRobotId = ReadString(body, "robotId")
                               ?? ReadString(body, "friendlyId")
                               ?? envelope.DeviceId;
        var deviceId = !string.IsNullOrWhiteSpace(presentedDeviceId)
            ? presentedDeviceId!
            : !string.IsNullOrWhiteSpace(presentedRobotId)
                ? presentedRobotId!
                : "unknown-device";

        var registrationSource = envelope.Headers.TryGetValue("X-OpenJibo-Registration-Source", out var sourceHeader)
            ? sourceHeader
            : null;
        var isDeploymentSmoke = string.Equals(registrationSource, RobotRegistrationSources.DeploymentSmoke,
            StringComparison.OrdinalIgnoreCase);
        DeploymentSmokeRegistrationAuthorization? smokeAuthorization = null;
        var usesReservedSmokeNamespace = deviceId.StartsWith(
            $"{ReleaseSmokeAuthorizationOptions.FixedPrefix}-", StringComparison.OrdinalIgnoreCase);
        if (isDeploymentSmoke || usesReservedSmokeNamespace)
        {
            var presentedSecret = envelope.Headers.TryGetValue("X-OpenJibo-Release-Smoke-Secret", out var secretHeader)
                ? secretHeader
                : null;
            if (!isDeploymentSmoke ||
                !_releaseSmokeAuthorization.TryAuthorize(deviceId, presentedSecret, out smokeAuthorization))
                return ProtocolDispatchResult.Raw(403, "{\"message\":\"Deployment smoke is not authorized.\"}",
                    "application/x-amz-json-1.1");
        }
        var existing = isDeploymentSmoke
            ? stateStore.GetOrCreateDeploymentSmokeDevice(smokeAuthorization!, envelope.FirmwareVersion,
                envelope.ApplicationVersion)
            : FindVisibleDeviceByIdentity(deviceId) ??
              stateStore.GetOrCreateDevice(deviceId, envelope.FirmwareVersion, envelope.ApplicationVersion,
                  registrationSource);
        if (!string.IsNullOrWhiteSpace(presentedRobotId))
            identitySuggestionStore?.Observe(existing.DeviceId, presentedRobotId,
                "auth:Notification.NewRobotToken", "robotId");

        var token = isDeploymentSmoke
            ? stateStore.IssueDeploymentSmokeRobotToken(deviceId)
            : stateStore.IssueRobotToken(existing.DeviceId);
        _logger.LogInformation(
            "Notification NewRobotToken issued deviceId={DeviceId} robotId={RobotId}",
            deviceId,
            presentedRobotId);

        return ProtocolDispatchResult.Ok(new
        {
            token
        });
    }

    private DeviceRegistration? FindVisibleDeviceByIdentity(string identity)
    {
        var normalized = identity.Trim();
        var candidates = stateStore.FindVisibleIdentityCandidates(normalized);
        if (candidates.Count == 1) return candidates[0];
        if (candidates.Count > 1)
        {
            // Choosing among a duplicate would silently issue the reconnecting robot
            // another robot's token, so ambiguity must remain a non-match.
            _logger.LogWarning("NewRobotToken identity {Identity} matched multiple visible robot records; refusing automatic reuse.",
                normalized);
        }

        return null;
    }

    private ProtocolDispatchResult? TryIssueDeploymentSmokeHubToken(string deviceId, string? registrationSource,
        ProtocolEnvelope envelope)
    {
        var isDeploymentSmoke = string.Equals(registrationSource, RobotRegistrationSources.DeploymentSmoke,
            StringComparison.OrdinalIgnoreCase);
        var usesReservedNamespace = deviceId.StartsWith($"{ReleaseSmokeAuthorizationOptions.FixedPrefix}-",
            StringComparison.OrdinalIgnoreCase);
        if (!isDeploymentSmoke && !usesReservedNamespace) return null;

        var presentedSecret = envelope.Headers.TryGetValue("X-OpenJibo-Release-Smoke-Secret", out var secretHeader)
            ? secretHeader
            : null;
        if (!isDeploymentSmoke ||
            !_releaseSmokeAuthorization.TryAuthorize(deviceId, presentedSecret, out _))
            return ProtocolDispatchResult.Raw(403, "{\"message\":\"Deployment smoke is not authorized.\"}",
                "application/x-amz-json-1.1");

        var existing = stateStore.GetDevices().FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        if (existing is null ||
            !string.Equals(RobotRegistrationSources.Normalize(existing.RegistrationSource, existing.DeviceId),
                RobotRegistrationSources.DeploymentSmoke, StringComparison.OrdinalIgnoreCase))
            return ProtocolDispatchResult.Raw(403,
                "{\"message\":\"Deployment smoke registration requires NewRobotToken.\"}",
                "application/x-amz-json-1.1");

        var token = stateStore.IssueDeploymentSmokeHubToken(existing.DeviceId);
        return ProtocolDispatchResult.Ok(new
        {
            token,
            expires = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds()
        });
    }
    private bool TryResolveAccountCaller(ProtocolEnvelope envelope, out UserRecord? user, out AccountProfile owner,
        out ProtocolDispatchResult? denied)
    {
        owner = stateStore.GetAccount();
        user = null;
        denied = null;
        var accessKeyId = AwsRequestAccessKey.Read(envelope);
        if (string.IsNullOrWhiteSpace(accessKeyId))
            return true;

        user = stateStore.FindUserByAccessKeyId(accessKeyId);
        if (user is not null)
            return true;
        if (accessKeyId.Equals(owner.AccessKeyId, StringComparison.Ordinal))
            return true;

        denied = ProtocolDispatchResult.Raw(401, "{\"message\":\"Invalid access key\"}", "application/json");
        return false;
    }

    private static ProtocolDispatchResult UnsupportedAccountOperation() =>
        ProtocolDispatchResult.Raw(400, "{\"message\":\"This account operation is not supported.\"}",
            "application/json");

    private static string CreatePasswordResetCode()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return $"reset-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }

    private static long? ReadInt64(JsonElement? body, string propertyName)
    {
        if (body is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(propertyName, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var number))
            return number;
        return property.ValueKind == JsonValueKind.String && long.TryParse(property.GetString(), out var parsed)
            ? parsed
            : null;
    }

    private static string? ReadString(JsonElement? body, string propertyName)
    {
        return body is { ValueKind: JsonValueKind.Object } element &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement? body, string propertyName)
    {
        if (body is not { ValueKind: JsonValueKind.Object } element ||
            !element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.Array)
            return [];

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static JsonElement? ReadObject(JsonElement? body, string propertyName)
    {
        return body is { ValueKind: JsonValueKind.Object } element &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : null;
    }

    private static object BuildAccountResponse(AccountProfile account)
    {
        return new
        {
            id = account.AccountId,
            email = account.Email,
            firstName = account.FirstName,
            lastName = account.LastName,
            gender = "unknown",
            birthday = 631152000000L,
            phoneNumber = "+10000000000",
            photoUrl = string.Empty,
            isActive = true,
            messagingAllowed = true,
            accessKeyId = account.AccessKeyId,
            secretAccessKey = account.SecretAccessKey,
            roles = Array.Empty<object>(),
            facebookConnected = false,
            termsAccepted = true
        };
    }

    private static object BuildAccountResponse(UserRecord user)
    {
        return new
        {
            id = user.Id,
            email = user.Email,
            firstName = user.FirstName,
            lastName = user.LastName,
            gender = user.Gender ?? "unknown",
            birthday = user.Birthday ?? 631152000000L,
            phoneNumber = "+10000000000",
            photoUrl = string.Empty,
            isActive = user.IsActive,
            messagingAllowed = true,
            accessKeyId = user.AccessKeyId,
            secretAccessKey = user.SecretAccessKey,
            roles = Array.Empty<object>(),
            facebookConnected = false,
            termsAccepted = true
        };
    }
}
