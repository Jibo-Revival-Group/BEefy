using System.Text.Json;
using Jibo.Cloud.Application.Abstractions;
using Microsoft.Data.Sqlite;

namespace Jibo.Cloud.Infrastructure.Persistence;

/// <summary>Additive history store; preserves existing cloud-state and personal-memory formats.</summary>
public sealed class SqliteConversationHistory : IConversationHistory
{
    private readonly string _connectionString;
    private readonly bool _recordSpeech;
    public SqliteConversationHistory(string path, bool recordSpeech = false, string? legacyPath = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path) }.ToString();
        _recordSpeech = recordSpeech;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS conversation_history (
                id TEXT PRIMARY KEY, timestamp INTEGER NOT NULL, robot_id TEXT NOT NULL,
                session_id TEXT NOT NULL, skill_id TEXT NOT NULL, intent TEXT NOT NULL,
                transcript TEXT, reply TEXT, payload TEXT);
            CREATE INDEX IF NOT EXISTS conversation_history_robot ON conversation_history(robot_id, timestamp);
            CREATE TABLE IF NOT EXISTS conversation_speech (id TEXT PRIMARY KEY, timestamp INTEGER NOT NULL, record TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS conversation_history_imports (path TEXT PRIMARY KEY);
            """;
        command.ExecuteNonQuery();
        if (legacyPath is not null && File.Exists(legacyPath)) ImportLegacy(legacyPath);
    }
    private SqliteConnection Open() { var connection = new SqliteConnection(_connectionString); connection.Open(); return connection; }
    public void Record(ConversationHistoryRecord record)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = "DELETE FROM conversation_history WHERE timestamp < $cutoff";
        prune.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeMilliseconds());
        prune.ExecuteNonQuery();
        Insert(connection, transaction, record);
        if (_recordSpeech)
        {
            using var speech = connection.CreateCommand(); speech.Transaction = transaction;
            speech.CommandText = "INSERT OR IGNORE INTO conversation_speech VALUES ($id, $timestamp, $record)";
            speech.Parameters.AddWithValue("$id", record.Id);
            speech.Parameters.AddWithValue("$timestamp", record.Timestamp.ToUnixTimeMilliseconds());
            speech.Parameters.AddWithValue("$record", JsonSerializer.Serialize(record));
            speech.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    private void Insert(SqliteConnection connection, SqliteTransaction transaction, ConversationHistoryRecord record)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO conversation_history VALUES ($id, $timestamp, $robot, $session, $skill, $intent, $transcript, $reply, $payload)";
        command.Parameters.AddWithValue("$id", record.Id); command.Parameters.AddWithValue("$timestamp", record.Timestamp.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$robot", record.RobotId); command.Parameters.AddWithValue("$session", record.SessionId);
        command.Parameters.AddWithValue("$skill", record.SkillId); command.Parameters.AddWithValue("$intent", record.Intent);
        command.Parameters.AddWithValue("$transcript", _recordSpeech ? (object?)record.Transcript ?? DBNull.Value : DBNull.Value);
        command.Parameters.AddWithValue("$reply", _recordSpeech ? (object?)record.Reply ?? DBNull.Value : DBNull.Value);
        command.Parameters.AddWithValue("$payload", (object?)record.Payload ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    public ConversationHistoryRecord? Latest(string robotId, string? skillId = null)
    {
        using var connection = Open(); using var command = Query(connection, robotId, skillId, null);
        command.CommandText = "SELECT * " + command.CommandText + " ORDER BY timestamp DESC, rowid DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8)) : null;
    }
    public int Count(string robotId, string? skillId = null, DateTimeOffset? since = null)
    {
        using var connection = Open(); using var command = Query(connection, robotId, skillId, since);
        command.CommandText = "SELECT COUNT(*) " + command.CommandText;
        return Convert.ToInt32(command.ExecuteScalar());
    }
    private static SqliteCommand Query(SqliteConnection connection, string robot, string? skill, DateTimeOffset? since)
    {
        var command = connection.CreateCommand();
        command.CommandText = "FROM conversation_history WHERE robot_id = $robot AND timestamp >= $cutoff";
        command.Parameters.AddWithValue("$robot", robot);
        command.Parameters.AddWithValue("$cutoff", (since is { } date && date > DateTimeOffset.UtcNow.AddDays(-14) ? date : DateTimeOffset.UtcNow.AddDays(-14)).ToUnixTimeMilliseconds());
        if (skill is not null) { command.CommandText += " AND skill_id = $skill"; command.Parameters.AddWithValue("$skill", skill); }
        return command;
    }
    private void ImportLegacy(string path)
    {
        path = Path.GetFullPath(path);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        using var check = connection.CreateCommand(); check.Transaction = transaction;
        check.CommandText = "SELECT COUNT(*) FROM conversation_history_imports WHERE path = $path";
        check.Parameters.AddWithValue("$path", path);
        if (Convert.ToInt32(check.ExecuteScalar()) != 0) return;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("skillLaunches", out _) || !json.RootElement.TryGetProperty("speech", out _))
            throw new InvalidDataException("Legacy history must contain skillLaunches and speech arrays; the original file was left untouched.");
        if (json.RootElement.TryGetProperty("skillLaunches", out var launches))
            foreach (var row in launches.EnumerateArray())
            {
                string Text(string key) => row.TryGetProperty(key, out var value) ? value.ToString() : "";
                Insert(connection, transaction, new(Text("id"), DateTimeOffset.FromUnixTimeMilliseconds(row.GetProperty("timestamp").GetInt64()),
                    Text("robotID"), Text("sessionID"), Text("skillID"), Text("intent"), null, null, row.GetRawText()));
            }
        if (json.RootElement.TryGetProperty("speech", out var speechRows))
            foreach (var row in speechRows.EnumerateArray())
            {
                using var speech = connection.CreateCommand(); speech.Transaction = transaction;
                speech.CommandText = "INSERT OR IGNORE INTO conversation_speech VALUES ($id, $timestamp, $record)";
                speech.Parameters.AddWithValue("$id", row.TryGetProperty("id", out var id) ? id.ToString() : Guid.NewGuid().ToString("N"));
                speech.Parameters.AddWithValue("$timestamp", row.TryGetProperty("timestamp", out var ts) ? ts.GetInt64() : 0);
                speech.Parameters.AddWithValue("$record", row.GetRawText()); speech.ExecuteNonQuery();
            }
        check.CommandText = "INSERT INTO conversation_history_imports VALUES ($path)";
        check.ExecuteNonQuery(); transaction.Commit();
    }
}
