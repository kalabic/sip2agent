namespace SIP2Agent.UserAgentService.Service;

internal sealed class CallLifecycleSignals : IDisposable
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource<CallTerminationReason> _termination =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _applicationShutdownRegistration;

    internal CallLifecycleSignals(CancellationToken applicationCancellationToken)
    {
        _applicationShutdownRegistration = applicationCancellationToken.Register(
            static state => ((CallLifecycleSignals)state!).RequestStop(
                CallTerminationReason.ApplicationShutdown),
            this);
    }

    internal CancellationToken CancellationToken => _cancellation.Token;

    internal Task<CallTerminationReason> Termination => _termination.Task;

    internal void RequestStop(CallTerminationReason reason)
    {
        if (!_termination.TrySetResult(reason))
        {
            return;
        }

        _cancellation.Cancel();
    }

    public void Dispose()
    {
        _applicationShutdownRegistration.Dispose();
        _cancellation.Dispose();
    }
}
