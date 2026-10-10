using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Jibo.Cloud.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace Jibo.Cloud.Tests.Application;

public sealed class NativeConversationHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "beefy-native-history-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_directory, "history.sqlite");
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void RecordsSurviveRestart_AreIsolatedByRobotAndRespectRetention()
    {
        var store = new SqliteConversationHistory(FilePath);
        store.Record(Row("old", "robot-a", DateTimeOffset.UtcNow.AddDays(-15)));
        store.Record(Row("a", "robot-a", DateTimeOffset.UtcNow));
        store.Record(Row("b", "robot-b", DateTimeOffset.UtcNow));
        var restarted = new SqliteConversationHistory(FilePath);
        Assert.Equal(1, restarted.Count("robot-a"));
        Assert.Equal("a", restarted.Latest("robot-a")!.Id);
        Assert.Equal("b", restarted.Latest("robot-b")!.Id);
        Assert.Null(restarted.Latest("robot-a")!.Transcript);
    }

    [Fact]
    public void LegacySnapshotImportsOnce_WithoutChangingOriginalBytes()
    {
        Directory.CreateDirectory(_directory);
        var legacy = Path.Combine(_directory, "legacy.json");
        var bytes = JsonSerializer.Serialize(new { skillLaunches = new[] { new
        { id = "legacy", timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), robotID = "robot", sessionID = "session", skillID = "@be/exercise", intent = "exerciseDoYoga" } }, speech = Array.Empty<object>() });
        File.WriteAllText(legacy, bytes);
        var first = new SqliteConversationHistory(FilePath, legacyPath: legacy);
        var second = new SqliteConversationHistory(FilePath, legacyPath: legacy);
        Assert.Equal(1, second.Count("robot"));
        Assert.Equal("legacy", second.Latest("robot")!.Id);
        Assert.Equal(bytes, File.ReadAllText(legacy));
    }

    [Fact]
    public void SpeechRecordingIsExplicitlyEnabled()
    {
        var store = new SqliteConversationHistory(FilePath, recordSpeech: true);
        store.Record(Row("speech", "robot", DateTimeOffset.UtcNow));
        Assert.Equal("do yoga", store.Latest("robot")!.Transcript);
    }
    [Fact]
    public void InvalidLegacySnapshotDoesNotMarkImportSuccessful()
    {
        Directory.CreateDirectory(_directory);
        var legacy = Path.Combine(_directory, "legacy.json");
        File.WriteAllText(legacy, "{}");
        Assert.Throws<InvalidDataException>(() => new SqliteConversationHistory(FilePath, legacyPath: legacy));
        Assert.Equal("{}", File.ReadAllText(legacy));
        File.WriteAllText(legacy, "{\"skillLaunches\":[],\"speech\":[]}");
        Assert.Equal(0, new SqliteConversationHistory(FilePath, legacyPath: legacy).Count("robot"));
    }

    private static ConversationHistoryRecord Row(string id, string robot, DateTimeOffset time) =>
        new(id, time, robot, "session", "@be/exercise", "exerciseDoYoga", "do yoga", "", null);
}
