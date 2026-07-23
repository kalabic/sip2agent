using AudioFormatLib.IO;
using DotBase.Log;
using LibRTIC.Config;
using LibRTIC.Conversation;
using Microsoft.Extensions.Logging;
using SIPSorcery;
using SIPSorcery.Media;
using SIP2Agent.UserAgentService.Service;

namespace MiniRTICallServer.RTISorcery;

internal sealed class RTICCallAgentOptions
{
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string GreetingInstructions { get; init; }
        = "Greet the caller briefly and ask how you can help.";
}

internal sealed class RTICCallAgent : ICallAgent, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly InfoLog _info;
    private readonly RTICConfig _configuration;
    private readonly RTICCallAgentOptions _options;
    private readonly Func<
        InfoLog,
        RTICConfig,
        IPcm16FrameOutput,
        CancellationToken,
        IRealtimeAgentSession> _sessionFactory;
    private readonly RealtimeAgentBridge _audioBridge;
    private readonly Func<Task> _startMediaAsync;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IRealtimeAgentSession? _session;
    private Task? _sessionRunTask;
    private Task? _monitorTask;
    private Task? _prepareTask;
    private Task? _startTask;
    private Task? _stopTask;

    private RTICCallAgent(
        RTICConfig configuration,
        RTICCallAgentOptions options,
        Func<
            InfoLog,
            RTICConfig,
            IPcm16FrameOutput,
            CancellationToken,
            IRealtimeAgentSession> sessionFactory,
        RealtimeAgentBridge audioBridge,
        VoIPMediaSession mediaSession,
        Func<Task> startMediaAsync)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        ArgumentNullException.ThrowIfNull(audioBridge);
        ArgumentNullException.ThrowIfNull(mediaSession);
        ArgumentNullException.ThrowIfNull(startMediaAsync);
        if (options.PreparationTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.PreparationTimeout));
        }
        if (options.StopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options.StopTimeout));
        }
        if (string.IsNullOrWhiteSpace(options.GreetingInstructions))
        {
            throw new ArgumentException("Greeting instructions are required.", nameof(options));
        }

        _logger = LogFactory.CreateLogger<RTICCallAgent>();
        _info = new MicrosoftInfoLog(_logger);
        _configuration = configuration;
        _options = options;
        _sessionFactory = sessionFactory;
        _audioBridge = audioBridge;
        MediaSession = mediaSession;
        _startMediaAsync = startMediaAsync;
    }

    public VoIPMediaSession MediaSession { get; }

    public Task Completion => _completion.Task;

    public static RTICCallAgent Create(
        RTICConfig? configuration = null,
        RTICCallAgentOptions? options = null)
    {
        ILogger logger = LogFactory.CreateLogger<RTICCallAgent>();
        RealtimeAgentBridge bridge = new(logger);
        try
        {
            VoIPMediaSession mediaSession = new(bridge.ToMediaEndPoints());
            return new RTICCallAgent(
                configuration ?? LoadEnvironmentConfiguration(),
                options ?? new RTICCallAgentOptions(),
                CreateSession,
                bridge,
                mediaSession,
                mediaSession.Start);
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    internal static RTICCallAgent CreateForTesting(
        RTICConfig configuration,
        RTICCallAgentOptions options,
        Func<
            InfoLog,
            RTICConfig,
            IPcm16FrameOutput,
            CancellationToken,
            IRealtimeAgentSession> sessionFactory,
        Func<Task> startMediaAsync)
    {
        RealtimeAgentBridge bridge = new(LogFactory.CreateLogger<RTICCallAgent>());
        try
        {
            VoIPMediaSession mediaSession = new(bridge.ToMediaEndPoints());
            return new RTICCallAgent(
                configuration,
                options,
                sessionFactory,
                bridge,
                mediaSession,
                startMediaAsync);
        }
        catch
        {
            bridge.Dispose();
            throw;
        }
    }

    public Task PrepareAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_stopTask is not null)
            {
                throw new InvalidOperationException("The RTIC call agent is stopping.");
            }

            _prepareTask ??= PrepareCoreAsync(cancellationToken);
            return _prepareTask;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_prepareTask?.IsCompletedSuccessfully != true)
            {
                throw new InvalidOperationException("The RTIC call agent is not ready.");
            }
            if (_stopTask is not null)
            {
                throw new InvalidOperationException("The RTIC call agent is stopping.");
            }

            _startTask ??= StartCoreAsync(cancellationToken);
            return _startTask;
        }
    }

    public Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync(reason);
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    public async ValueTask DisposeAsync()
        => await StopAsync("RTIC call agent disposed.", CancellationToken.None)
            .ConfigureAwait(false);

    private static IRealtimeAgentSession CreateSession(
        InfoLog info,
        RTICConfig configuration,
        IPcm16FrameOutput callerAudioOutput,
        CancellationToken cancellationToken)
    {
        RTIConversation conversation = RTIConversationTask.Create(info, cancellationToken);
        try
        {
            conversation.ConfigureWith(configuration, callerAudioOutput);
            return new RTIConversationSession(conversation);
        }
        catch
        {
            conversation.Dispose();
            throw;
        }
    }

    private async Task PrepareCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource preparationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCancellation.Token);
        try
        {
            IRealtimeAgentSession session = _sessionFactory(
                _info,
                _configuration,
                _audioBridge.CallerAudioOutput,
                _lifetimeCancellation.Token);
            try
            {
                _audioBridge.AttachSession(session);
            }
            catch
            {
                session.Dispose();
                throw;
            }

            Task runTask = session.RunAsync();
            lock (_gate)
            {
                _session = session;
                _sessionRunTask = runTask;
            }

            await session.Ready
                .WaitAsync(_options.PreparationTimeout, preparationCancellation.Token)
                .ConfigureAwait(false);

            lock (_gate)
            {
                _monitorTask ??= MonitorAsync(runTask, _audioBridge.Completion);
            }
        }
        catch (TimeoutException exception)
        {
            CancelSessionSafely();
            throw new AgentPreparationException(
                AgentPreparationFailureKind.ProviderUnavailable,
                $"Realtime session did not become ready within {_options.PreparationTimeout.TotalSeconds:0} seconds.",
                exception);
        }
        catch
        {
            CancelSessionSafely();
            throw;
        }
    }

    private static RTICConfig LoadEnvironmentConfiguration()
    {
        RTICConfigLoadResult result = RTICConfigLoader.LoadEnvironment();
        if (result.Config is not null) return result.Config;
        throw new AgentPreparationException(AgentPreparationFailureKind.Configuration, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _startMediaAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            IRealtimeAgentSession session;
            lock (_gate)
            {
                session = _session ?? throw new InvalidOperationException(
                    "The Realtime provider session is not available.");
            }
            await session.StartResponseAsync(
                _options.GreetingInstructions,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            throw;
        }
    }

    private async Task MonitorAsync(Task sessionRunTask, Task bridgeCompletion)
    {
        try
        {
            Task completed = await Task.WhenAny(sessionRunTask, bridgeCompletion)
                .ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    completed == sessionRunTask
                        ? "The Realtime provider session ended unexpectedly."
                        : "The SIP media bridge stopped unexpectedly.");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
    }

    private async Task StopCoreAsync(string reason)
    {
        _lifetimeCancellation.Cancel();
        CancelSessionSafely();

        using CancellationTokenSource timeout = new(_options.StopTimeout);
        IRealtimeAgentSession? session;
        Task? sessionRunTask;
        Task? monitorTask;
        lock (_gate)
        {
            session = _session;
            sessionRunTask = _sessionRunTask;
            monitorTask = _monitorTask;
        }

        if (sessionRunTask is not null)
        {
            try
            {
                await sessionRunTask.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                _logger.LogError(
                    "Realtime provider shutdown exceeded the {StopTimeoutSeconds} second total stop budget.",
                    _options.StopTimeout.TotalSeconds);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Realtime provider stopped with an error.");
            }
        }

        // The provider owns a borrowed read view over the caller-audio buffer. End
        // that ownership before the bridge is allowed to dispose the buffer. In the
        // normal path its run task has already completed; disposal is also the final
        // forced-close boundary if provider shutdown exhausted the shared budget.
        session?.Dispose();

        try
        {
            MediaSession.Close(reason);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to close the SIP media session cleanly.");
        }

        try
        {
            await _audioBridge.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _logger.LogError(
                "Media bridge shutdown exceeded the {StopTimeoutSeconds} second total stop budget.",
                _options.StopTimeout.TotalSeconds);
            await _audioBridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (monitorTask is not null)
        {
            await monitorTask.ConfigureAwait(false);
        }

        await _audioBridge.DisposeAsync().ConfigureAwait(false);
        MediaSession.Dispose();
        _lifetimeCancellation.Dispose();
        _completion.TrySetResult();
    }

    private void CancelSessionSafely()
    {
        IRealtimeAgentSession? session;
        lock (_gate)
        {
            session = _session;
        }

        try
        {
            session?.Cancel();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to request Realtime provider cancellation.");
        }
    }
}
