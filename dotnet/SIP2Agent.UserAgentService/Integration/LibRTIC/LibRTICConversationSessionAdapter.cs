using LibRTIC.Conversation;
using LibRTIC.MiniTaskLib.Events;
using SIP2Agent.UserAgentService.Service;

namespace SIP2Agent.UserAgentService.Integration.LibRTIC;

/// <summary>Adapts one LibRTIC conversation to SIP2Agent's private media contract.</summary>
internal sealed class LibRTICConversationSessionAdapter : IRealtimeAgentSession
{
    private readonly object _gate = new();
    private readonly RTIConversation _conversation;
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _runTask;
    private Exception? _providerFailure;
    private bool _cancelRequested;
    private int _disposed;

    public LibRTICConversationSessionAdapter(RTIConversation conversation)
    {
        _conversation = conversation ?? throw new ArgumentNullException(nameof(conversation));

        var conversationEvents = conversation.ConversationEvents;
        conversationEvents.Connect<FailedToConnectMsg>(false, HandleEvent);
        conversationEvents.Connect<TaskExceptionOccured>(false, HandleEvent);

        var updates = conversation.UpdatesReceiverEvents;
        // Preserve the LibRTIC mailbox order by forwarding updates inline.
        updates.Connect<RTICSessionConfigured>(false, HandleEvent);
        updates.Connect<ConversationSessionFinished>(false, HandleEvent);
        updates.Connect<RTICOutputAudioDelta>(false, HandleEvent);
        updates.Connect<RTICOutputAudioCompleted>(false, HandleEvent);
        updates.Connect<RTICInputSpeechStarted>(false, HandleEvent);
    }

    public event Action<RealtimeAgentMediaUpdate>? MediaUpdate;

    public Task Ready => _ready.Task;

    public Task RunAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return _runTask ??= RunCoreAsync();
        }
    }

    public Task StartResponseAsync(string? instructions, CancellationToken cancellationToken)
        => _conversation.StartResponseAsync(instructions, cancellationToken);

    public Task InterruptResponseAsync(CancellationToken cancellationToken)
        => _conversation.InterruptResponseAsync(cancellationToken);

    public Task TruncateOutputItemAsync(
        string itemId,
        int contentIndex,
        TimeSpan audioEndTime,
        CancellationToken cancellationToken)
        => _conversation.TruncateOutputItemAsync(
            itemId,
            contentIndex,
            audioEndTime,
            cancellationToken);

    public void Cancel()
    {
        lock (_gate)
        {
            _cancelRequested = true;
        }

        _conversation.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _conversation.Dispose();
        }
    }

    private async Task RunCoreAsync()
    {
        try
        {
            await _conversation.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (WasCancellationRequested())
        {
            return;
        }
        catch (Exception exception)
        {
            Exception failure = _ready.Task.IsCompletedSuccessfully
                ? exception
                : new AgentPreparationException(
                    AgentPreparationFailureKind.ProviderUnavailable,
                    "Realtime conversation startup failed.",
                    exception);
            throw RecordFailure(failure);
        }

        Exception? preservedFailure;
        bool cancelled;
        lock (_gate)
        {
            preservedFailure = _providerFailure;
            cancelled = _cancelRequested;
        }

        if (preservedFailure is not null)
        {
            throw preservedFailure;
        }
        if (!cancelled)
        {
            throw new InvalidOperationException("The Realtime provider session ended unexpectedly.");
        }
    }

    private bool WasCancellationRequested()
    {
        lock (_gate)
        {
            return _cancelRequested;
        }
    }

    private void HandleEvent(object? sender, RTICSessionConfigured update)
        => _ready.TrySetResult();

    private void HandleEvent(object? sender, FailedToConnectMsg update)
    {
        AgentPreparationFailureKind kind = update.Reason is
            FailedToConnectMsg.ErrorStatus.EndpointOptionsMissing or
            FailedToConnectMsg.ErrorStatus.FailedToConfigure
                ? AgentPreparationFailureKind.Configuration
                : AgentPreparationFailureKind.ProviderUnavailable;
        RecordFailure(new AgentPreparationException(kind, update.Message));
    }

    private void HandleEvent(object? sender, TaskExceptionOccured update)
        => RecordFailure(
            _ready.Task.IsCompletedSuccessfully
                ? update.Exception
                : new AgentPreparationException(
                    AgentPreparationFailureKind.ProviderUnavailable,
                    "Realtime conversation startup failed.",
                    update.Exception));

    private void HandleEvent(object? sender, ConversationSessionFinished update)
    {
        if (!WasCancellationRequested())
        {
            RecordFailure(
                _ready.Task.IsCompletedSuccessfully
                    ? new InvalidOperationException("The Realtime provider session ended unexpectedly.")
                    : new AgentPreparationException(
                        AgentPreparationFailureKind.ProviderUnavailable,
                        "Realtime conversation ended before the session was configured."));
        }
    }

    private Exception RecordFailure(Exception exception)
    {
        Exception failure;
        lock (_gate)
        {
            _providerFailure ??= exception;
            failure = _providerFailure;
        }

        _ready.TrySetException(failure);
        return failure;
    }

    private void HandleEvent(object? sender, RTICOutputAudioDelta update)
        => MediaUpdate?.Invoke(new RealtimeOutputAudioDelta(
            new RealtimeOutputIdentity(update.ResponseId, update.ItemId, update.ContentIndex),
            update.Audio));

    private void HandleEvent(object? sender, RTICOutputAudioCompleted update)
        => MediaUpdate?.Invoke(new RealtimeOutputAudioFinished(
            new RealtimeOutputIdentity(update.ResponseId, update.ItemId, update.ContentIndex)));

    private void HandleEvent(object? sender, RTICInputSpeechStarted update)
        => MediaUpdate?.Invoke(new RealtimeInputSpeechStarted());
}
