namespace Jibo.Cloud.Application.Services;

/// <summary>
/// Asks the local Phoenix parser and skills for a cloud conversation turn.
/// A null result means the current decision dispatch should run.
/// </summary>
public interface IPhoenixConversationClient
{
    Task<JiboInteractionDecision?> TryDecideAsync(string transcript, CancellationToken cancellationToken);
}
