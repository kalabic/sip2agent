using AudioFormatLib.IO;
using Microsoft.Extensions.Logging;
using SIPSorceryMedia.Abstractions;

namespace SIP2Agent.UserAgentService.Service;

internal sealed partial class RealtimeAgentBridge : IDisposable, IAsyncDisposable
{
    internal const int REALTIME_SAMPLE_RATE = RealtimeAssistantAudioSource.RealtimeSampleRate;
    internal const int INPUT_CHANNEL_CAPACITY = RealtimeCallerAudioSink.InputChannelCapacity;
    internal const int OUTPUT_PREBUFFER_PACKETS = RealtimeAssistantAudioSource.OutputPrebufferPackets;
    internal const int OUTPUT_MAX_REALTIME_SAMPLES = RealtimeAssistantAudioSource.OutputMaxRealtimeSamples;
    internal const int OUTPUT_COMMAND_CAPACITY = RealtimeAssistantAudioSource.OutputCommandCapacity;

    private readonly object _gate = new();
    private readonly RealtimeCallerAudioSink _caller;
    private readonly RealtimeAssistantAudioSource _assistant;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _stopTask;

    private int _faulted;

    internal RealtimeAgentBridge(ILogger logger, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _caller = new RealtimeCallerAudioSink(logger, Fail);
        _assistant = new RealtimeAssistantAudioSource(
            logger,
            timeProvider ?? TimeProvider.System,
            Fail);
    }

    public Task Completion => _completion.Task;

    public long DroppedInputFrameCount => _caller.DroppedFrameCount;

    public long OutputOverflowCount => _assistant.OutputOverflowCount;

    internal long UnplayedRealtimeSampleCount => _assistant.UnplayedRealtimeSampleCount;

    internal IPcm16FrameOutput CallerAudioOutput => _caller.CallerAudioOutput;

    internal RealtimeCallerAudioSink Caller => _caller;

    internal RealtimeAssistantAudioSource Assistant => _assistant;

    public MediaEndPoints ToMediaEndPoints()
        => new()
        {
            AudioSource = _assistant,
            AudioSink = _caller,
        };

    public void AttachSession(IRealtimeAgentSession session)
    {
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                throw new InvalidOperationException("The Realtime bridge is stopping.");
            }

            _assistant.AttachSession(session);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync();
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    public void Dispose()
        => _ = StopAsync(CancellationToken.None);

    public async ValueTask DisposeAsync()
        => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task StopCoreAsync()
    {
        _assistant.RequestStop();
        _caller.RequestStop();

        try
        {
            await Task.WhenAll(
                _assistant.CloseAudio(),
                _caller.CloseAudioSink()).ConfigureAwait(false);
        }
        finally
        {
            _assistant.DisposeOwnedResources();
            _caller.DisposeOwnedResources();
        }

        _completion.TrySetResult();
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        _assistant.CancelSessionSafely();
        _assistant.RequestStop();
        _caller.RequestStop();
        _completion.TrySetException(exception);
    }
}
