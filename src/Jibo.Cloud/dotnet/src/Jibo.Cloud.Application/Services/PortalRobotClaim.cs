using Jibo.Cloud.Application.Abstractions;

namespace Jibo.Cloud.Application.Services;

public static class PortalRobotClaim
{
    public static string? RejectIfUnproven(JiboVerificationService verification, ICloudStateStore store, string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return "code is required.";

        var pending = verification.InspectCode(code);
        if (pending is null)
            return "That verification code is invalid or has expired.";

        var presented = store.GetRobotCredentialBindings().Any(binding =>
            binding.DeviceId.Equals(pending.DeviceId, StringComparison.OrdinalIgnoreCase));
        if (!presented)
            return "That robot has not presented its credentials.";

        return null;
    }
}
