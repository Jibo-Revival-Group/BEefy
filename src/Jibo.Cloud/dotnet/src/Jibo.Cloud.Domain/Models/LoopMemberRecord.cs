namespace Jibo.Cloud.Domain.Models;

public sealed class LoopMemberRecord
{
    public string Id { get; init; } = $"mbr-{Guid.NewGuid():N}";
    public string LoopId { get; init; } = string.Empty;
    public string? AccountId { get; init; }
    public string? Email { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Gender { get; init; }
    public long? Birthday { get; init; }
    public bool IsChild { get; init; }
    public string? PhoneNumber { get; init; }
    public string Status { get; init; } = "active";
    public string Type { get; init; } = "owner";
    public string? Nickname { get; init; }
    public string? PhoneticName { get; init; }
    public bool FaceEnrolled { get; init; }
    public bool VoiceEnrolled { get; init; }
    public string? LegalGuardianId { get; init; }
    public string? AgreementId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    /// <summary>
    /// When set, a Portal edit owns name/gender until the robot's roster catches up.
    /// </summary>
    public DateTimeOffset? PortalEditedUtc { get; init; }

    /// <summary>SHA-256 hex of the stored (resized) JPEG bytes — also used as the photo URL path key.</summary>
    public string? PhotoContentHash { get; init; }

    /// <summary>Stored photo content type, typically <c>image/jpeg</c>.</summary>
    public string? PhotoContentType { get; init; }

    public DateTimeOffset? PhotoUpdatedUtc { get; init; }

    /// <summary>
    /// Copies this member, optionally overriding selected fields. Photo metadata is preserved
    /// unless <paramref name="clearPhoto"/> is true or new photo fields are supplied.
    /// </summary>
    public LoopMemberRecord Clone(
        string? firstName = null,
        bool setFirstName = false,
        string? lastName = null,
        bool setLastName = false,
        string? gender = null,
        bool setGender = false,
        long? birthday = null,
        bool setBirthday = false,
        bool? isChild = null,
        string? phoneNumber = null,
        bool setPhoneNumber = false,
        string? status = null,
        string? type = null,
        string? nickname = null,
        bool setNickname = false,
        string? phoneticName = null,
        bool setPhoneticName = false,
        bool? faceEnrolled = null,
        bool? voiceEnrolled = null,
        string? email = null,
        bool setEmail = false,
        string? accountId = null,
        bool setAccountId = false,
        DateTimeOffset? portalEditedUtc = null,
        bool setPortalEditedUtc = false,
        string? photoContentHash = null,
        string? photoContentType = null,
        DateTimeOffset? photoUpdatedUtc = null,
        bool setPhoto = false,
        bool clearPhoto = false)
    {
        var nextPhotoHash = clearPhoto
            ? null
            : setPhoto ? photoContentHash : PhotoContentHash;
        var nextPhotoType = clearPhoto
            ? null
            : setPhoto ? photoContentType : PhotoContentType;
        var nextPhotoUpdated = clearPhoto
            ? null
            : setPhoto ? photoUpdatedUtc : PhotoUpdatedUtc;

        return new LoopMemberRecord
        {
            Id = Id,
            LoopId = LoopId,
            AccountId = setAccountId ? accountId : AccountId,
            Email = setEmail ? email : Email,
            FirstName = setFirstName ? firstName : FirstName,
            LastName = setLastName ? lastName : LastName,
            Gender = setGender ? gender : Gender,
            Birthday = setBirthday ? birthday : Birthday,
            IsChild = isChild ?? IsChild,
            PhoneNumber = setPhoneNumber ? phoneNumber : PhoneNumber,
            Status = status ?? Status,
            Type = type ?? Type,
            Nickname = setNickname ? nickname : Nickname,
            PhoneticName = setPhoneticName ? phoneticName : PhoneticName,
            FaceEnrolled = faceEnrolled ?? FaceEnrolled,
            VoiceEnrolled = voiceEnrolled ?? VoiceEnrolled,
            LegalGuardianId = LegalGuardianId,
            AgreementId = AgreementId,
            CreatedUtc = CreatedUtc,
            PortalEditedUtc = setPortalEditedUtc ? portalEditedUtc : PortalEditedUtc,
            PhotoContentHash = nextPhotoHash,
            PhotoContentType = nextPhotoType,
            PhotoUpdatedUtc = nextPhotoUpdated
        };
    }
}
