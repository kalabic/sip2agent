using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;
using SIPSorcery.Sys;

namespace SIP2Agent.UserAgentService.Service;


public sealed class SIPEndpointService : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(15);

    private bool IsDisposed
    {
        get
        {
            lock (_stateLock)
            {
                return _disposed;
            }
        }
    }

    private readonly SIPEndpointConfig _config;
    private readonly ILogger _logger;
    private readonly SIPTransport _sipTransport = new();
    private readonly RegistrationManager _registrationManager;
    private readonly InboundCallRegistry _calls;
    private readonly PortRange? _rtpPortRange;
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _shutdownTask;
    private int _started;
    private bool _disposed;

    public SIPEndpointService(SIPEndpointConfig config, ILoggerFactory loggerFactory)
    {
        SIPEndpointConfig.Validate(config);
        _config = config;
        _logger = loggerFactory.CreateLogger<SIPEndpointService>();
        _registrationManager = new RegistrationManager(config, _sipTransport, loggerFactory);
        _calls = new InboundCallRegistry(_logger);
        _rtpPortRange = config.RtpPortRange is { } range
            ? new PortRange(range.StartPort, range.EndPort)
            : null;
    }

    internal int ActiveCallCount => _calls.Count;

    internal int TrackedCallTaskCount => _calls.Count;

    internal bool IsRegistrationConnected =>
        _registrationManager.IsConnected;

    public void Start()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                return;
            }

            SIPUtil.StartTransport(_config, _sipTransport, _logger);
            _sipTransport.SIPTransportRequestReceived += OnRequest;
            _registrationManager.Start();
        }
    }

    public void ListCallsToLog()
        => _calls.ListCalls();

    public void LogStatus()
        => _registrationManager.LogStatus();

    public void Connect()
        => _registrationManager.Start();

    public void Disconnect()
        => _registrationManager.Disconnect();

    public void HangupOldest()
    {
        CallSession? oldestCall = _calls.GetOldest();

        if (oldestCall == null)
        {
            _logger.LogWarning("There are no active calls.");
            return;
        }

        _logger.LogInformation("Hanging up call {CallId}.", oldestCall.CallId);
        SignalAndHangup(oldestCall, CallTerminationReason.LocalHangup);
    }

    public void HangupAll()
        => RequestCallsStop(CallTerminationReason.LocalHangup);

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task shutdownTask;
        lock (_stateLock)
        {
            _shutdownTask ??= ShutdownCoreAsync();
            shutdownTask = _shutdownTask;
        }

        return shutdownTask.WaitAsync(cancellationToken);
    }

    public void Dispose()
        => StopAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
        => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task OnRequest(
        SIPEndPoint localSIPEndPoint,
        SIPEndPoint remoteEndPoint,
        SIPRequest sipRequest)
    {
        try
        {
            if (SIPUtil.IsInDialogRequest(sipRequest))
            {
                // Each call owns a SIPUserAgent on the shared transport. Its Call-ID filter handles
                // the request, while all other session user agents ignore it.
                return;
            }

            bool providerRequest = IsProviderRequest(remoteEndPoint, sipRequest);
            switch (sipRequest.Method)
            {
                case SIPMethodsEnum.INVITE:
                    if (!providerRequest)
                    {
                        await SIPUtil.SendResponseAsync(
                                _sipTransport,
                                sipRequest,
                                SIPResponseStatusCodesEnum.Forbidden)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Incoming INVITE {Local}<-{Remote} {Uri}.",
                            localSIPEndPoint,
                            remoteEndPoint,
                            sipRequest.URI);
                        SIPResponseStatusCodesEnum responseStatus = HandleInviteAdmission(sipRequest);
                        if (responseStatus != SIPResponseStatusCodesEnum.None)
                        {
                            await SIPUtil.SendResponseAsync(
                                    _sipTransport,
                                    sipRequest,
                                    responseStatus)
                                .ConfigureAwait(false);
                        }
                    }
                    break;

                case SIPMethodsEnum.OPTIONS:
                    await SIPUtil.SendResponseAsync(
                            _sipTransport,
                            sipRequest,
                            providerRequest
                                ? SIPResponseStatusCodesEnum.Ok
                                : SIPResponseStatusCodesEnum.Forbidden)
                        .ConfigureAwait(false);
                    break;

                case SIPMethodsEnum.BYE:
                    await SIPUtil.SendResponseAsync(
                            _sipTransport,
                            sipRequest,
                            providerRequest
                                ? SIPResponseStatusCodesEnum.CallLegTransactionDoesNotExist
                                : SIPResponseStatusCodesEnum.Forbidden)
                        .ConfigureAwait(false);
                    break;

                case SIPMethodsEnum.SUBSCRIBE:
                case SIPMethodsEnum.REGISTER:
                default:
                    await SIPUtil.SendResponseAsync(
                            _sipTransport,
                            sipRequest,
                            providerRequest
                                ? SIPResponseStatusCodesEnum.MethodNotAllowed
                                : SIPResponseStatusCodesEnum.Forbidden)
                        .ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (IsDisposed)
        {
            _logger.LogDebug(
                "SIP request handling for {Method} was cancelled during shutdown.",
                sipRequest.Method);
        }
        catch (ObjectDisposedException) when (IsDisposed)
        {
            _logger.LogDebug(
                "SIP request handling for {Method} stopped during disposal.",
                sipRequest.Method);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Exception handling {Method}.", sipRequest.Method);
        }
    }

    /// <summary>
    /// Evaluates endpoint-level admission for a new INVITE.
    /// Any non-<see cref="SIPResponseStatusCodesEnum.None"/> result is sent by the caller after
    /// <c>_stateLock</c> is released.
    /// </summary>
    private SIPResponseStatusCodesEnum HandleInviteAdmission(SIPRequest sipRequest)
    {
        lock (_stateLock)
        {
            if (_disposed || _shutdownCts.IsCancellationRequested)
            {
                return SIPResponseStatusCodesEnum.ServiceUnavailable;
            }

            if (!_registrationManager.IsConnected)
            {
                return SIPResponseStatusCodesEnum.TemporarilyUnavailable;
            }

            return AdmitInviteLocked(sipRequest);
        }
    }

    /// <summary>
    /// Tracks a distinct inbound call and queues its independent full-call workflow.
    /// The caller must hold <c>_stateLock</c>; <see cref="SIPResponseStatusCodesEnum.None"/> means
    /// the session and workflow task are tracked.
    /// </summary>
    private SIPResponseStatusCodesEnum AdmitInviteLocked(SIPRequest sipRequest)
    {
        if (_calls.TryGet(sipRequest.Header.CallId, out _))
        {
            _logger.LogInformation(
                "Rejecting incoming INVITE {CallId}; call workflow is already tracked.",
                sipRequest.Header.CallId);
            return SIPResponseStatusCodesEnum.BusyHere;
        }

        string? resolvedAudioFile = string.IsNullOrWhiteSpace(_config.AnswerAudioFile)
            ? null
            : SIPEndpointConfig.ResolveAnswerAudioFilePath(_config.AnswerAudioFile);
        CallSession session = CallSession.CreateInbound(
            _sipTransport,
            _logger,
            sipRequest,
            () => new FilePlaybackCallAgent(
                _logger,
                resolvedAudioFile,
                _config.AcceptRtpFromAny,
                _rtpPortRange),
            _config.ContactHost,
            _shutdownCts.Token);

        if (!_calls.TryStart(session, _logger, out _))
        {
            _logger.LogWarning(
                "Could not track incoming INVITE {CallId}; a session with that Call-ID already exists.",
                session.CallId);
            try
            {
                session.Reject(SIPResponseStatusCodesEnum.BusyHere, "Busy Here");
            }
            finally
            {
                session.Dispose();
            }
            return SIPResponseStatusCodesEnum.None;
        }

        return SIPResponseStatusCodesEnum.None;
    }

    private bool IsProviderRequest(SIPEndPoint remoteEndPoint, SIPRequest sipRequest)
    {
        bool isProvider = _registrationManager.IsRequestFromProvider(remoteEndPoint);
        if (!isProvider)
        {
            _logger.LogWarning(
                "Rejecting out-of-dialog {Method} from untrusted SIP endpoint {RemoteEndPoint}.",
                sipRequest.Method,
                remoteEndPoint);
        }

        return isProvider;
    }

    private void RequestCallsStop(CallTerminationReason reason)
    {
        CallSession[] calls = _calls.Snapshot().Select(call => call.Session).ToArray();

        foreach (CallSession call in calls)
        {
            _logger.LogInformation("Hanging up call {CallId}.", call.CallId);
            SignalAndHangup(call, reason);
        }
    }

    private void SignalAndHangup(CallSession call, CallTerminationReason reason)
    {
        call.RequestStop(reason);

        try
        {
            call.Hangup();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to hang up call {CallId}.", call.CallId);
        }
    }

    private async Task ShutdownCoreAsync()
    {
        TrackedInboundCall[] trackedCalls;

        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sipTransport.SIPTransportRequestReceived -= OnRequest;
            trackedCalls = _calls.StopAcceptingAndSnapshot();
        }

        foreach (TrackedInboundCall tracked in trackedCalls)
        {
            SignalAndHangup(tracked.Session, CallTerminationReason.ApplicationShutdown);
        }

        _shutdownCts.Cancel();

        if (trackedCalls.Length > 0)
        {
            try
            {
                await Task.WhenAll(trackedCalls.Select(call => call.RunTask))
                    .WaitAsync(ShutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError(
                    "Timed out waiting for {CallCount} call workflows to stop.",
                    trackedCalls.Length);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "One or more call workflows failed during shutdown.");
            }
        }

        _registrationManager.Dispose();
        SIPUtil.ShutdownTransport(_sipTransport, _logger);
        _shutdownCts.Dispose();
    }
}
