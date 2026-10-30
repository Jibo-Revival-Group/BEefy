using System.Text.Json;
using Jibo.Cloud.Application.Services;
using Jibo.Cloud.Domain.Models;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Jibo.Cloud.Tests.Protocol;

// Backup_*.Create/Restore snapshot or roll back the whole server, so they need the server's own credentials.
public sealed class BackupAuthorizationTests
{
    [Fact]
    public async Task UnsignedBackupRestore_IsRejected_AndDoesNotRollBackOtherRobots()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        Assert.Equal(200, (await service.DispatchAsync(SignedBackup("Create", new { }))).StatusCode);
        store.GetOrCreateDevice("robot-registered-later", "1.0", "1.0");

        var restore = await service.DispatchAsync(Unsigned("Restore"));

        Assert.Equal(401, restore.StatusCode);
        Assert.Contains(store.GetDevices(), d => d.DeviceId == "robot-registered-later");
    }

    [Fact]
    public async Task UnsignedOrWrongSecretBackupCreate_IsRejected()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);

        Assert.Equal(401, (await service.DispatchAsync(Unsigned("Create"))).StatusCode);
        Assert.Equal(401, (await service.DispatchAsync(SignedBackup("Create", new { }, secret: "wrong"))).StatusCode);
        Assert.Empty(store.GetBackups());
    }

    [Fact]
    public async Task AccountSignedBackupRestore_IsRefusedInManagedMode()
    {
        var store = new InMemoryCloudStateStore();
        var managed = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OpenJibo:Deployment:Mode"] = "managed" })
            .Build();
        var service = new JiboCloudProtocolService(store, configuration: managed);
        Assert.Equal(200, (await service.DispatchAsync(SignedBackup("Create", new { }))).StatusCode);
        store.GetOrCreateDevice("robot-registered-later", "1.0", "1.0");

        var restore = await service.DispatchAsync(SignedBackup("Restore", new { }));

        Assert.Equal(403, restore.StatusCode);
        Assert.Contains(store.GetDevices(), d => d.DeviceId == "robot-registered-later");
    }

    [Fact]
    public async Task AccountSignedBackupRestore_StillWorksSelfHosted()
    {
        var store = new InMemoryCloudStateStore();
        var service = new JiboCloudProtocolService(store);
        Assert.Equal(200, (await service.DispatchAsync(SignedBackup("Create", new { }))).StatusCode);

        Assert.Equal(200, (await service.DispatchAsync(SignedBackup("Restore", new { }))).StatusCode);
    }

    internal static ProtocolEnvelope SignedBackup(string operation, object body, string? accessKeyId = null,
        string? secret = null)
    {
        var account = new AccountProfile();
        var bodyText = body as string ?? JsonSerializer.Serialize(body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = "api.jibo.com",
            ["X-Amz-Date"] = "Sat, 03 Oct 2026 13:00:00 GMT",
            ["X-Amz-Target"] = "Backup_20170222." + operation
        };
        headers["Authorization"] = Aws3Signature.BuildAuthorization(accessKeyId ?? account.AccessKeyId,
            secret ?? account.SecretAccessKey, "POST", headers, bodyText);
        return new ProtocolEnvelope
        {
            Method = "POST",
            HostName = "api.jibo.com",
            ServicePrefix = "Backup_20170222",
            Operation = operation,
            BodyText = bodyText,
            Headers = headers
        };
    }

    private static ProtocolEnvelope Unsigned(string operation) => new()
    {
        Method = "POST",
        HostName = "api.jibo.com",
        ServicePrefix = "Backup_20170222",
        Operation = operation,
        BodyText = "{}"
    };
}
