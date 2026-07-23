using AudioFormatLib.IO;
using DotBase.Log;
using LibRTIC.Config;
using LibRTIC.Conversation;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Sys;
using SIP2Agent.UserAgentService.Service;

namespace SIP2Agent.UserAgentService.Integration.LibRTIC;

internal sealed class LibRTICCallAgentOptions
{
    public TimeSpan PreparationTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public string GreetingInstructions { get; init; }
        = "Greet the caller briefly and ask how you can help.";
}

/// <summary>Creates isolated SIP media and LibRTIC conversation state for one call.</summary>
internal sealed partial class LibRTICCallAgent : ICallAgent, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly InfoLog _info;
    private readonly RTICConfig _configuration;
    private readonly LibRTICCallAgentOptions _options;
    private readonly Func<InfoLog, RTICConfig, IPcm16FrameOutput, CancellationToken, IRealtimeAgentSession>
        _sessionFactory;
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

    private LibRTICCallAgent(
        RTICConfig configuration,
        LibRTICCallAgentOptions options,
        ILogger logger,
        Func<InfoLog, RTICConfig, IPcm16FrameOutput, CancellationToken, IRealtimeAgentSession> sessionFactory,
        RealtimeAgentBridge audioBridge,
        VoIPMediaSession mediaSession,
        Func<Task> startMediaAsync)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _audioBridge = audioBridge ?? throw new ArgumentNullException(nameof(audioBridge));
        MediaSession = mediaSession ?? throw new ArgumentNullException(nameof(mediaSession));
        _startMediaAsync = startMediaAsync ?? throw new ArgumentNullException(nameof(startMediaAsync));
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

        _info = new MicrosoftInfoLogAdapter(logger);
    }

    public VoIPMediaSession MediaSession { get; }

    public Task Completion => _completion.Task;

    internal static LibRTICCallAgent Create(
        RTICConfig configuration,
        LibRTICCallAgentOptions options,
        ILogger logger,
        bool acceptRtpFromAny,
        PortRange? rtpPortRange)
    {
        RealtimeAgentBridge bridge = new(logger);
        try
        {
            VoIPMediaSession mediaSession = CreateMediaSession(
                bridge,
                acceptRtpFromAny,
                rtpPortRange);
            return new LibRTICCallAgent(
                configuration,
                options,
                logger,
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

    internal static LibRTICCallAgent CreateForTesting(
        RTICConfig configuration,
        LibRTICCallAgentOptions options,
        ILogger logger,
        Func<InfoLog, RTICConfig, IPcm16FrameOutput, CancellationToken, IRealtimeAgentSession> sessionFactory,
        Func<Task> startMediaAsync,
        bool acceptRtpFromAny = true,
        PortRange? rtpPortRange = null)
    {
        RealtimeAgentBridge bridge = new(logger);
        try
        {
            VoIPMediaSession mediaSession = CreateMediaSession(
                bridge,
                acceptRtpFromAny,
                rtpPortRange);
            return new LibRTICCallAgent(
                configuration,
                options,
                logger,
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
                throw new InvalidOperationException("The LibRTIC call agent is stopping.");
            }

            return _prepareTask ??= PrepareCoreAsync(cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_prepareTask?.IsCompletedSuccessfully != true)
            {
                throw new InvalidOperationException("The LibRTIC call agent is not ready.");
            }
            if (_stopTask is not null)
            {
                throw new InvalidOperationException("The LibRTIC call agent is stopping.");
            }

            return _startTask ??= StartCoreAsync(cancellationToken);
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
        => await StopAsync("LibRTIC call agent disposed.", CancellationToken.None).ConfigureAwait(false);

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
            return new LibRTICConversationSessionAdapter(conversation);
        }
        catch
        {
            conversation.Dispose();
            throw;
        }
    }

    private static VoIPMediaSession CreateMediaSession(
        RealtimeAgentBridge bridge,
        bool acceptRtpFromAny,
        PortRange? rtpPortRange)
        => new(new VoIPMediaSessionConfig
        {
            MediaEndPoint = bridge.ToMediaEndPoints(),
            RtpPortRange = rtpPortRange,
        })
        {
            AcceptRtpFromAny = acceptRtpFromAny,
        };

    private async Task PrepareCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource preparationCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
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

            await session.StartResponseAsync(_options.GreetingInstructions, cancellationToken)
                .ConfigureAwait(false);
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
            Task completed = await Task.WhenAny(sessionRunTask, bridgeCompletion).ConfigureAwait(false);
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

        // LibRTIC keeps a borrowed read view over this buffer; dispose it before
        // the bridge tears down its caller-audio buffer.
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
