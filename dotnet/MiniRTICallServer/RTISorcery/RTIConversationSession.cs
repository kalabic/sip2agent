using LibRTIC.Conversation;
using SIP2Agent.UserAgentService.Integration.LibRTIC;
using SIP2Agent.UserAgentService.Service;

namespace MiniRTICallServer.RTISorcery;

internal sealed class RTIConversationSession : IRealtimeAgentSession
{
    private readonly LibRTICConversationSessionAdapter _inner;

    public RTIConversationSession(RTIConversation conversation)
        : this(new LibRTICConversationSessionAdapter(conversation))
    {
    }

    internal RTIConversationSession(ILibRTICConversation conversation)
        : this(new LibRTICConversationSessionAdapter(conversation))
    {
    }

    private RTIConversationSession(LibRTICConversationSessionAdapter inner)
    {
        _inner = inner;
    }

    public event Action<RealtimeAgentMediaUpdate>? MediaUpdate
    {
        add => _inner.MediaUpdate += value;
        remove => _inner.MediaUpdate -= value;
    }

    public Task Ready => _inner.Ready;

    public Task RunAsync() => _inner.RunAsync();

    public Task StartResponseAsync(
        string? instructions,
        CancellationToken cancellationToken)
        => _inner.StartResponseAsync(instructions, cancellationToken);

    public Task InterruptOutputAsync(
        RealtimeOutputIdentity identity,
        TimeSpan playedThrough,
        bool cancelResponseIfActive,
        CancellationToken cancellationToken)
        => _inner.InterruptOutputAsync(
            identity,
            playedThrough,
            cancelResponseIfActive,
            cancellationToken);

    public void Cancel() => _inner.Cancel();

    public void Dispose() => _inner.Dispose();
}
