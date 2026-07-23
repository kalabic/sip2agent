using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;

namespace SIP2Agent.UserAgentService.Service;

internal sealed partial class CallSession : IInboundCall, IDisposable, IAsyncDisposable
{
    private readonly object _stopGate = new();
    private readonly ILogger _logger;
    private readonly ICallAgent _agent;
    private readonly CallLifecycleSignals _lifecycle;
    private readonly IPAddress? _publicAnswerAddress;
    private readonly Action<CallSession>? _onAnswered;
    private readonly Action<CallSession>? _onStopped;

    private Task? _stopTask;
    private int _hangupRequested;
    private int _answerNotified;
    private int _stopping;

    private CallSession(
        string callId,
        int initialInviteCSeq,
        string remoteDescription,
        SIPUserAgent userAgent,
        SIPServerUserAgent serverUserAgent,
        ICallAgent agent,
        CallLifecycleSignals lifecycle,
        IPAddress? publicAnswerAddress,
        Action<CallSession>? onAnswered,
        Action<CallSession>? onStopped,
        string? mediaAuditorRecordingDirectory,
        ILogger logger)
    {
        CallId = callId;
        InitialInviteCSeq = initialInviteCSeq;
        RemoteDescription = remoteDescription;
        UserAgent = userAgent;
        ServerUserAgent = serverUserAgent;
        _agent = agent;
        _lifecycle = lifecycle;
        _publicAnswerAddress = publicAnswerAddress;
        _onAnswered = onAnswered;
        _onStopped = onStopped;
        _logger = logger;

        SubscribeEvents();
        InitializeMediaAuditor(mediaAuditorRecordingDirectory);
        if (ServerUserAgent.IsCancelled)
        {
            _lifecycle.RequestStop(CallTerminationReason.RemoteCancellation);
        }
    }

    public string CallId { get; }

    internal int InitialInviteCSeq { get; }

    public DateTimeOffset Inserted { get; } = DateTimeOffset.Now;

    public string RemoteDescription { get; }

    public SIPUserAgent UserAgent { get; }

    public SIPServerUserAgent ServerUserAgent { get; }

    public VoIPMediaSession MediaSession => _agent.MediaSession;

    public bool Answered => ServerUserAgent.IsUASAnswered;

    public CancellationToken CancellationToken => _lifecycle.CancellationToken;

    public Task<CallTerminationReason> Termination => _lifecycle.Termination;

    public Task AgentCompletion => _agent.Completion;

    public bool IsCallActive => UserAgent.IsCallActive;

    public static CallSession CreateInbound(
        SIPTransport sipTransport,
        ILogger logger,
        SIPRequest inviteRequest,
        Func<ICallAgent> agentFactory,
        string? publicAnswerAddress,
        CancellationToken applicationCancellationToken,
        Action<CallSession>? onAnswered = null,
        Action<CallSession>? onStopped = null,
        string? mediaAuditorRecordingDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(sipTransport);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(inviteRequest);
        ArgumentNullException.ThrowIfNull(agentFactory);

        SIPUserAgent? userAgent = null;
        SIPServerUserAgent? serverUserAgent = null;
        ICallAgent? agent = null;
        CallLifecycleSignals? lifecycle = null;
        string callId = inviteRequest.Header.CallId;

        try
        {
            lifecycle = new CallLifecycleSignals(applicationCancellationToken);
            userAgent = new SIPUserAgent(sipTransport, null);
            serverUserAgent = userAgent.AcceptCall(inviteRequest);
            agent = agentFactory();

            IPAddress? publicAddress = null;
            if (!string.IsNullOrWhiteSpace(publicAnswerAddress) &&
                !IPAddress.TryParse(publicAnswerAddress, out publicAddress))
            {
                throw new ArgumentException(
                    "The public SDP answer address is not a valid IP address.",
                    nameof(publicAnswerAddress));
            }

            return new CallSession(
                callId,
                inviteRequest.Header.CSeq,
                inviteRequest.Header.From?.ToString() ??
                    inviteRequest.RemoteSIPEndPoint?.ToString() ??
                    "unknown",
                userAgent,
                serverUserAgent,
                agent,
                lifecycle,
                publicAddress,
                onAnswered,
                onStopped,
                mediaAuditorRecordingDirectory,
                logger);
        }
        catch
        {
            try
            {
                if (serverUserAgent is { IsUASAnswered: false, IsCancelled: false })
                {
                    serverUserAgent.Reject(
                        SIPResponseStatusCodesEnum.InternalServerError,
                        "Internal Server Error");
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to reject a call after construction failed.");
            }

            if (agent is not null)
            {
                try
                {
                    agent.StopAsync("call session construction failed", CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Failed to stop the call agent after construction failed.");
                }
            }

            lifecycle?.Dispose();
            _ = CloseAndDisposeUserAgentAsync(userAgent, logger);
            throw;
        }
    }

    public Task PrepareAgentAsync()
        => _agent.PrepareAsync(CancellationToken);

    public async Task<bool> AnswerAsync()
    {
        if (Volatile.Read(ref _stopping) != 0 ||
            CancellationToken.IsCancellationRequested ||
            ServerUserAgent.IsCancelled)
        {
            return false;
        }

        try
        {
            if (_publicAnswerAddress is not null)
            {
                _logger.LogDebug(
                    "Answering call using configured public address {PublicIPAddress}.",
                    _publicAnswerAddress);
                bool answered = await UserAgent.Answer(
                        ServerUserAgent,
                        MediaSession,
                        _publicAnswerAddress)
                    .ConfigureAwait(false);
                NotifyAnswered(answered);
                return answered;
            }

            bool defaultAnswer = await UserAgent.Answer(ServerUserAgent, MediaSession)
                .ConfigureAwait(false);
            NotifyAnswered(defaultAnswer);
            return defaultAnswer;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Call {CallId} failed during media setup.", CallId);
            return false;
        }
    }

    public Task StartAgentAsync()
        => _agent.StartAsync(CancellationToken);

    internal void RequestStop(CallTerminationReason reason)
        => _lifecycle.RequestStop(reason);

    public void Reject(SIPResponseStatusCodesEnum status, string reason)
    {
        if (Volatile.Read(ref _stopping) != 0 ||
            Answered ||
            ServerUserAgent.IsCancelled)
        {
            return;
        }

        ServerUserAgent.Reject(status, reason);
    }

    public void Hangup()
    {
        if (Volatile.Read(ref _stopping) != 0 ||
            Interlocked.Exchange(ref _hangupRequested, 1) != 0)
        {
            return;
        }

        if (UserAgent.IsCallActive)
        {
            UserAgent.Hangup();
        }
        else if (!Answered && !ServerUserAgent.IsCancelled)
        {
            ServerUserAgent.Reject(
                SIPResponseStatusCodesEnum.TemporarilyUnavailable,
                "Temporarily Unavailable");
        }
    }

    public Task StopAsync(string reason, CancellationToken cancellationToken)
    {
        Task stopTask;
        lock (_stopGate)
        {
            _stopTask ??= StopCoreAsync(reason);
            stopTask = _stopTask;
        }

        return cancellationToken.CanBeCanceled
            ? stopTask.WaitAsync(cancellationToken)
            : stopTask;
    }

    public void Dispose()
        => StopAsync("call session disposed", CancellationToken.None).GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
        => await StopAsync("call session disposed", CancellationToken.None).ConfigureAwait(false);

    private void SubscribeEvents()
    {
        UserAgent.OnCallHungup += OnCallHungup;
        UserAgent.ServerCallCancelled += OnServerCallCancelled;
        UserAgent.ServerCallRingTimeout += OnServerCallRingTimeout;
        UserAgent.OnDtmfTone += OnDtmfTone;
        UserAgent.OnRtpEvent += OnRtpEvent;
        MediaSession.OnTimeout += OnMediaTimeout;
        MediaSession.OnRtpPacketReceived += OnRtpPacketReceived;
    }

    private void DetachEvents()
    {
        UserAgent.OnCallHungup -= OnCallHungup;
        UserAgent.ServerCallCancelled -= OnServerCallCancelled;
        UserAgent.ServerCallRingTimeout -= OnServerCallRingTimeout;
        UserAgent.OnDtmfTone -= OnDtmfTone;
        UserAgent.OnRtpEvent -= OnRtpEvent;
        MediaSession.OnTimeout -= OnMediaTimeout;
        MediaSession.OnRtpPacketReceived -= OnRtpPacketReceived;
    }

    private void OnCallHungup(SIPDialogue _) => RequestStop(CallTerminationReason.RemoteHangup);

    private void OnServerCallCancelled(ISIPServerUserAgent _, SIPRequest __)
        => RequestStop(CallTerminationReason.RemoteCancellation);

    private void OnServerCallRingTimeout(ISIPServerUserAgent _)
        => RequestStop(CallTerminationReason.RingTimeout);

    private void OnMediaTimeout(SDPMediaTypesEnum _)
        => RequestStop(CallTerminationReason.MediaTimeout);

    private void OnDtmfTone(byte key, int duration)
        => _logger.LogInformation(
            "Call {CallId} received DTMF {Key}, duration {Duration}ms.",
            CallId,
            key,
            duration);

    private void OnRtpEvent(RTPEvent evt, RTPHeader _)
        => _logger.LogDebug(
            "Call {CallId} RTP event {EventId}, duration {Duration}.",
            CallId,
            evt.EventID,
            evt.Duration);

    private void OnRtpPacketReceived(
        IPEndPoint remoteEndPoint,
        SDPMediaTypesEnum mediaType,
        RTPPacket rtpPacket)
        => _logger.LogTrace(
            "RTP packet from {RemoteEndPoint}, media {MediaType}, payload {PayloadLength} bytes.",
            remoteEndPoint,
            mediaType,
            rtpPacket.Payload?.Length ?? 0);

    private async Task StopCoreAsync(string reason)
    {
        Interlocked.Exchange(ref _stopping, 1);
        if (!Termination.IsCompleted)
        {
            RequestStop(CallTerminationReason.LocalHangup);
        }

        DetachEvents();
        DetachMediaAuditor();

        try
        {
            await _agent.StopAsync(reason, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to stop the agent for call {CallId}.", CallId);
        }

        CompleteMediaAuditor();

        try
        {
            UserAgent.Close();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to close SIP user agent for call {CallId}.", CallId);
        }

        _lifecycle.Dispose();
        try
        {
            _onStopped?.Invoke(this);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The stopped-call host callback failed for {CallId}.", CallId);
        }
        await DisposeUserAgentAsync(UserAgent, _logger).ConfigureAwait(false);
    }

    private void NotifyAnswered(bool answerResult)
    {
        if ((answerResult || Answered) &&
            Interlocked.Exchange(ref _answerNotified, 1) == 0)
        {
            try
            {
                _onAnswered?.Invoke(this);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "The answered-call host callback failed for {CallId}.", CallId);
            }
        }
    }

    private static async Task CloseAndDisposeUserAgentAsync(
        SIPUserAgent? userAgent,
        ILogger logger)
    {
        if (userAgent is null)
        {
            return;
        }

        try
        {
            userAgent.Close();
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to close SIP user agent during call cleanup.");
        }

        await DisposeUserAgentAsync(userAgent, logger).ConfigureAwait(false);
    }

    private static Task DisposeUserAgentAsync(SIPUserAgent userAgent, ILogger logger)
    {
        // SIPSorcery raises call-ended events while holding an internal semaphore. Final
        // disposal is deferred so cleanup never waits on that semaphore from its callback.
        return Task.Run(() =>
        {
            try
            {
                userAgent.Dispose();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Failed to dispose SIP user agent during call cleanup.");
            }
        });
    }

    partial void InitializeMediaAuditor(string? mediaAuditorRecordingDirectory);

    partial void DetachMediaAuditor();

    partial void CompleteMediaAuditor();
}
