using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService.Service;

internal enum CallTerminationReason
{
    RemoteHangup,
    RemoteCancellation,
    MediaTimeout,
    RingTimeout,
    LocalHangup,
    ApplicationShutdown,
}

internal enum AgentPreparationFailureKind
{
    Configuration,
    ProviderUnavailable,
}

internal sealed class AgentPreparationException : Exception
{
    public AgentPreparationFailureKind FailureKind { get; }

    public AgentPreparationException(
        AgentPreparationFailureKind failureKind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }
}

internal interface IInboundCall
{
    string CallId { get; }

    bool Answered { get; }

    CancellationToken CancellationToken { get; }

    Task<CallTerminationReason> Termination { get; }

    Task AgentCompletion { get; }

    bool IsCallActive { get; }

    Task PrepareAgentAsync();

    Task<bool> AnswerAsync();

    Task StartAgentAsync();

    void Reject(SIPResponseStatusCodesEnum status, string reason);

    void Hangup();

    Task StopAsync(string reason, CancellationToken cancellationToken);
}
