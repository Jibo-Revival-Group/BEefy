namespace Jibo.Cloud.Domain;

/// <summary>
/// Canonical and legacy hostnames used across protocol, sockets, and trust roots.
/// </summary>
public static class OpenJiboHostNames
{
    public const string CanonicalApi = "api.5x1.com";
    public const string LegacyOpenJiboApi = "api.openjibo.com";
    public const string LegacyJiboApi = "api.jibo.com";
    public const string LegacyJiboApiSocket = "api-socket.jibo.com";
    public const string LegacyOpenJiboSocket = "open-jibo-socket.openjibo.com";
    public const string LegacyNeoHub = "neo-hub.jibo.com";
    public const string LegacyOpenJiboNeoHub = "neohub.openjibo.com";
    public const string NativeCompatibilitySocket = "open-jibo-socket.jibo.pro";

    public static IReadOnlyList<string> RobotHostMappingKeys { get; } =
    [
        LegacyJiboApi,
        LegacyJiboApiSocket,
        LegacyOpenJiboApi,
        LegacyOpenJiboSocket,
        LegacyNeoHub,
        LegacyOpenJiboNeoHub,
        CanonicalApi
    ];
}
