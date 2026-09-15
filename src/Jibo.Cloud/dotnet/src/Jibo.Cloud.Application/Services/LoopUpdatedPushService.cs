using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Domain;
using Jibo.Cloud.Domain.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Shared LoopUpdated push path for portal and protocol loop mutations.
/// </summary>
public sealed class LoopUpdatedPushService(
    ICloudStateStore cloudStateStore,
    RobotNotificationRegistry robotNotificationRegistry,
    LoopMemberPhotoUrlSigner photoUrlSigner,
    IConfiguration configuration,
    ILogger<LoopUpdatedPushService> logger)
{
    public async Task<int> PushForLoopIdAsync(
        string loopId,
        IReadOnlyCollection<string>? additionalRobotKeys = null,
        CancellationToken cancellationToken = default)
    {
        var loop = cloudStateStore.GetLoops()
            .FirstOrDefault(item => item.LoopId.Equals(loopId, StringComparison.OrdinalIgnoreCase));
        if (loop is null)
        {
            logger.LogWarning("LoopUpdated push skipped: loop {LoopId} not found", loopId);
            return 0;
        }

        var robotKeys = BuildRobotKeys(loop, additionalRobotKeys);
        if (robotKeys.Count == 0)
        {
            logger.LogWarning("LoopUpdated push skipped: no robot keys loopId={LoopId}", loopId);
            return 0;
        }

        var payload = BuildLoopNotificationPayload(loop, cloudStateStore.GetLoopMembers(loopId));
        var pushed = await robotNotificationRegistry.PushLoopUpdatedAsync(robotKeys, payload, cancellationToken);
        if (pushed == 0)
        {
            logger.LogWarning(
                "LoopUpdated push matched no live api-socket loopId={LoopId} keyCount={KeyCount} keys={Keys}. " +
                "Robot must keep wss notification socket open (Host api-socket.jibo.com or /token-... path).",
                loopId,
                robotKeys.Count,
                string.Join(',', robotKeys.Take(8)));
        }
        else
        {
            logger.LogInformation(
                "LoopUpdated push loopId={LoopId} pushCount={PushCount} keyCount={KeyCount} keys={Keys}",
                loopId,
                pushed,
                robotKeys.Count,
                string.Join(',', robotKeys.Take(8)));
        }

        return pushed;
    }

    private object BuildLoopNotificationPayload(LoopRecord loop, IEnumerable<LoopMemberRecord> members)
    {
        var origin = ResolveApiOrigin();
        return new
        {
            id = loop.LoopId,
            name = loop.Name,
            owner = loop.OwnerAccountId,
            robot = loop.RobotId,
            robotFriendlyId = loop.RobotFriendlyId,
            members = members.Select(member => MapLoopMember(member, origin)).ToArray(),
            isSuspended = loop.IsSuspended,
            created = loop.CreatedUtc.ToUnixTimeMilliseconds(),
            updated = loop.UpdatedUtc.ToUnixTimeMilliseconds(),
            eventKey = "LoopUpdated"
        };
    }

    private object MapLoopMember(LoopMemberRecord member, string origin)
    {
        string? photoUrl = null;
        if (!string.IsNullOrWhiteSpace(member.PhotoContentHash))
            photoUrl = photoUrlSigner.BuildSignedUrl(origin, member.Id, member.PhotoContentHash);

        return new
        {
            id = member.Id,
            loopId = member.LoopId,
            accountId = member.AccountId,
            account = new
            {
                email = member.Email,
                firstName = member.FirstName,
                lastName = member.LastName,
                gender = member.Gender,
                birthday = member.Birthday,
                isChild = member.IsChild,
                phoneNumber = member.PhoneNumber,
                photoUrl
            },
            enrolled = new { face = member.FaceEnrolled, voice = member.VoiceEnrolled },
            status = member.Status,
            type = member.Type,
            nickname = member.Nickname,
            phoneticName = member.PhoneticName,
            legalGuardianId = member.LegalGuardianId,
            agreementId = member.AgreementId,
            created = member.CreatedUtc.ToUnixTimeMilliseconds()
        };
    }

    private string ResolveApiOrigin()
    {
        var configured = configuration["OpenJibo:CanonicalApiBaseUrl"];
        if (Uri.TryCreate(configured, UriKind.Absolute, out var uri) &&
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return uri.GetLeftPart(UriPartial.Authority);

        return $"https://{OpenJiboHostNames.CanonicalApi}";
    }

    private HashSet<string> BuildRobotKeys(LoopRecord loop, IReadOnlyCollection<string>? additionalRobotKeys)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                keys.Add(value.Trim());
        }

        if (additionalRobotKeys is not null)
        {
            foreach (var key in additionalRobotKeys)
                Add(key);
        }

        Add(loop.RobotId);
        Add(loop.RobotFriendlyId);

        foreach (var seed in keys.ToArray())
        {
            var device = cloudStateStore.FindDeviceByFriendlyId(seed);
            if (device is null) continue;
            Add(device.DeviceId);
            Add(device.RobotId);
            Add(device.FriendlyName);
        }

        return keys;
    }
}
