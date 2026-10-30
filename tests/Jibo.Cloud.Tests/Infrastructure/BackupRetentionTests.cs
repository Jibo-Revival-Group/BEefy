using Jibo.Cloud.Infrastructure.Persistence;

namespace Jibo.Cloud.Tests.Infrastructure;

public sealed class BackupRetentionTests
{
    [Fact]
    public void CreateBackup_KeepsOnlyTheNewestFive()
    {
        var store = new InMemoryCloudStateStore();
        var created = Enumerable.Range(0, 8)
            .Select(i => store.CreateBackup("openjibo-default-loop", $"b{i}").BackupId)
            .ToArray();

        Assert.Equal(created[3..], store.GetBackups().Select(b => b.BackupId));
    }
}
