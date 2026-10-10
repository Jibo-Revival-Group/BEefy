namespace Jibo.Cloud.Application.Abstractions;

public sealed record ConversationHistoryRecord(string Id, DateTimeOffset Timestamp, string RobotId,
    string SessionId, string SkillId, string Intent, string? Transcript, string? Reply, string? Payload);

public interface IConversationHistory
{
    void Record(ConversationHistoryRecord record);
    ConversationHistoryRecord? Latest(string robotId, string? skillId = null);
    int Count(string robotId, string? skillId = null, DateTimeOffset? since = null);
}
