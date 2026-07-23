#define MEDIA_AUDITOR_ENABLED

using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorceryMedia.Abstractions;

#if MEDIA_AUDITOR_ENABLED

namespace SIP2Agent.UserAgentService.Service
{
    /// <summary>Exposes the number of frames that reached the Realtime caller-audio sink.</summary>
    internal interface IMediaAuditorFrameMetrics
    {
        long MediaAuditorSinkFrameCount { get; }
    }

    /// <summary>Accepts per-call configuration for optional headerless encoded-audio capture.</summary>
    internal interface IMediaAuditorRawRecordingTarget
    {
        void EnableMediaAuditorRawRecording(string callId, string directoryPath);
    }

    public sealed partial class SIPEndpointService
    {
        private int _mediaAuditorStarted;
        private string? _mediaAuditorRecordingDirectory;

        partial void EnableMediaAuditorRawRecordingCore(string directoryPath, ref bool supported)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(directoryPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException("The recording directory path is invalid.", nameof(directoryPath), exception);
            }

            lock (_stateLock)
            {
                if (Volatile.Read(ref _started) != 0)
                {
                    throw new InvalidOperationException(
                        "Media auditor raw recording must be enabled before the SIP endpoint starts.");
                }

                Directory.CreateDirectory(normalizedPath);
                _mediaAuditorRecordingDirectory = normalizedPath;
                supported = true;
            }
        }

        partial void SnapshotMediaAuditorRecordingDirectory(ref string? directoryPath)
        {
            directoryPath = _mediaAuditorRecordingDirectory;
        }

        partial void StartMediaAuditor()
        {
            if (Interlocked.Exchange(ref _mediaAuditorStarted, 1) == 0)
            {
                _sipTransport.SIPResponseOutTraceEvent += OnMediaAuditorSipResponse;
            }
        }

        partial void StopMediaAuditor()
        {
            if (Interlocked.Exchange(ref _mediaAuditorStarted, 0) != 0)
            {
                _sipTransport.SIPResponseOutTraceEvent -= OnMediaAuditorSipResponse;
            }
        }

        private void OnMediaAuditorSipResponse(
            SIPEndPoint _,
            SIPEndPoint __,
            SIPResponse response)
        {
            try
            {
                if (response.Status != SIPResponseStatusCodesEnum.Ok ||
                    response.Header.CSeqMethod != SIPMethodsEnum.INVITE ||
                    string.IsNullOrWhiteSpace(response.Body) ||
                    string.IsNullOrWhiteSpace(response.Header.CallId) ||
                    !IsSdpContentType(response.Header.ContentType) ||
                    !_calls.TryGet(response.Header.CallId, out CallSession? session) ||
                    session is null ||
                    !string.Equals(session.CallId, response.Header.CallId, StringComparison.Ordinal) ||
                    session.InitialInviteCSeq != response.Header.CSeq)
                {
                    return;
                }

                session.CaptureMediaAuditorResponse(response);
            }
            catch (Exception exception)
            {
                _logger.LogTrace(exception, "Media auditor could not inspect an outbound SIP response.");
            }
        }

        private static bool IsSdpContentType(string? contentType)
            => !string.IsNullOrWhiteSpace(contentType) &&
                string.Equals(
                    contentType.Split(';', 2)[0].Trim(),
                    SDP.SDP_MIME_CONTENTTYPE,
                    StringComparison.OrdinalIgnoreCase);
    }

    internal sealed partial class CallSession
    {
        private MediaContractAuditor? _mediaAuditor;

        partial void InitializeMediaAuditor(string? mediaAuditorRecordingDirectory)
        {
            try
            {
                _mediaAuditor = new MediaContractAuditor(CallId, _logger, MediaSession);
                if (mediaAuditorRecordingDirectory is not null &&
                    _agent is IMediaAuditorRawRecordingTarget recordingTarget)
                {
                    recordingTarget.EnableMediaAuditorRawRecording(CallId, mediaAuditorRecordingDirectory);
                }
                _mediaAuditor.Attach();
            }
            catch (Exception exception)
            {
                _logger.LogTrace(exception, "Media auditor could not initialise for call {CallId}.", CallId);
            }
        }

        internal void CaptureMediaAuditorResponse(SIPResponse response)
            => _mediaAuditor?.CaptureOutboundSdp(response);

        partial void DetachMediaAuditor()
            => _mediaAuditor?.Detach();

        partial void CompleteMediaAuditor()
        {
            _mediaAuditor?.Complete(_agent is IMediaAuditorFrameMetrics metrics
                ? metrics.MediaAuditorSinkFrameCount
                : null);
        }
    }

    internal sealed partial class RealtimeCallerAudioSink
    {
        private long _mediaAuditorFrameCount;
        private readonly object _mediaAuditorRecordingGate = new();
        private FileStream? _mediaAuditorRecordingStream;
        private string? _mediaAuditorRecordingPath;
        private string? _mediaAuditorFirstFormat;
        private int _mediaAuditorRecordingFaulted;
        private int _mediaAuditorMixedFormatWarningLogged;

        internal long MediaAuditorFrameCount => Interlocked.Read(ref _mediaAuditorFrameCount);

        internal void EnableMediaAuditorRawRecording(string callId, string directoryPath)
        {
            try
            {
                string safeCallId = SanitizeCallId(callId);
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(callId)))[..12]
                    .ToLowerInvariant();
                string fileName = $"{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}_{safeCallId}_{hash}.raw";
                string path = Path.Combine(directoryPath, fileName);
                lock (_mediaAuditorRecordingGate)
                {
                    if (_mediaAuditorRecordingStream is not null || _mediaAuditorRecordingFaulted != 0)
                    {
                        return;
                    }

                    _mediaAuditorRecordingStream = new FileStream(
                        path,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.Read,
                        bufferSize: 64 * 1024,
                        FileOptions.SequentialScan);
                    _mediaAuditorRecordingPath = path;
                }
                _logger.LogInformation(
                    "Media auditor is recording sensitive headerless caller audio to {RecordingPath}.",
                    path);
            }
            catch (Exception exception)
            {
                DisableMediaAuditorRecording(exception);
            }
        }

        partial void RecordMediaAuditorFrame(EncodedAudioFrame encodedMediaFrame)
        {
            Interlocked.Increment(ref _mediaAuditorFrameCount);
            try
            {
                lock (_mediaAuditorRecordingGate)
                {
                    if (_mediaAuditorRecordingStream is null)
                    {
                        return;
                    }

                    string format = encodedMediaFrame.AudioFormat.ToString() ?? "unknown";
                    if (_mediaAuditorFirstFormat is null)
                    {
                        _mediaAuditorFirstFormat = format;
                        _logger.LogInformation(
                            "Media auditor raw recording {RecordingPath} first observed audio format {AudioFormat}.",
                            _mediaAuditorRecordingPath,
                            format);
                    }
                    else if (!string.Equals(_mediaAuditorFirstFormat, format, StringComparison.Ordinal) &&
                        Interlocked.Exchange(ref _mediaAuditorMixedFormatWarningLogged, 1) == 0)
                    {
                        _logger.LogWarning(
                            "Media auditor raw recording {RecordingPath} changed from {FirstFormat} to {CurrentFormat}; the headerless file now contains mixed encoded formats.",
                            _mediaAuditorRecordingPath,
                            _mediaAuditorFirstFormat,
                            format);
                    }

                    _mediaAuditorRecordingStream.Write(encodedMediaFrame.EncodedAudio, 0, encodedMediaFrame.EncodedAudio.Length);
                }
            }
            catch (Exception exception)
            {
                DisableMediaAuditorRecording(exception);
            }
        }

        partial void CloseMediaAuditorRecording()
        {
            lock (_mediaAuditorRecordingGate)
            {
                CloseMediaAuditorRecordingUnderLock();
            }
        }

        private void DisableMediaAuditorRecording(Exception exception)
        {
            lock (_mediaAuditorRecordingGate)
            {
                if (Interlocked.Exchange(ref _mediaAuditorRecordingFaulted, 1) == 0)
                {
                    _logger.LogWarning(
                        exception,
                        "Media auditor disabled raw recording for {RecordingPath}; the call will continue.",
                        _mediaAuditorRecordingPath ?? "an uncreated file");
                }
                CloseMediaAuditorRecordingUnderLock();
            }
        }

        private void CloseMediaAuditorRecordingUnderLock()
        {
            FileStream? stream = _mediaAuditorRecordingStream;
            _mediaAuditorRecordingStream = null;
            if (stream is not null)
            {
                try
                {
                    stream.Flush();
                }
                catch (Exception exception)
                {
                    if (Interlocked.Exchange(ref _mediaAuditorRecordingFaulted, 1) == 0)
                    {
                        _logger.LogWarning(exception, "Media auditor could not close raw recording {RecordingPath}.", _mediaAuditorRecordingPath);
                    }
                }
                finally
                {
                    try
                    {
                        stream.Dispose();
                    }
                    catch (Exception exception)
                    {
                        if (Interlocked.Exchange(ref _mediaAuditorRecordingFaulted, 1) == 0)
                        {
                            _logger.LogWarning(exception, "Media auditor could not dispose raw recording {RecordingPath}.", _mediaAuditorRecordingPath);
                        }
                    }
                }
            }
        }

        private static string SanitizeCallId(string callId)
        {
            HashSet<char> invalid = Path.GetInvalidFileNameChars().ToHashSet();
            string sanitized = new(callId.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
            sanitized = string.IsNullOrWhiteSpace(sanitized) ? "call" : sanitized;
            return sanitized.Length <= 80 ? sanitized : sanitized[..80];
        }
    }

    internal sealed partial class RealtimeAgentBridge
    {
        internal long MediaAuditorSinkFrameCount => _caller.MediaAuditorFrameCount;

        internal void EnableMediaAuditorRawRecording(string callId, string directoryPath)
            => _caller.EnableMediaAuditorRawRecording(callId, directoryPath);
    }

    internal sealed class MediaContractAuditor
    {
        private const int MaxDistinctEndpoints = 32;
        private const int MaxDistinctSsrcs = 32;
        private const int MaxWarnings = 3;
        private readonly object _gate = new();
        private readonly string _callId;
        private readonly ILogger _logger;
        private readonly VoIPMediaSession _mediaSession;
        private readonly Dictionary<int, long> _payloadTypes = new();
        private readonly Dictionary<string, long> _remoteEndpoints = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, long> _ssrcs = new();

        private RTPChannel? _channel;
        private MediaContract? _contract;
        private string? _contractBody;
        private int _contractCSeq = -1;
        private bool _answerBoundaryReached;
        private bool _detached;
        private bool _completed;
        private int _warningCount;
        private long _rawRtp;
        private long _rawRtcp;
        private long _preAnswerMalformedRtp;
        private long _postAnswerMalformedRtp;
        private long _preAnswerMalformedRtcp;
        private long _postAnswerMalformedRtcp;
        private long _preAnswerPackets;
        private long _postAnswerRtp;
        private long _payloadTypeViolations;
        private long _directionViolations;

        internal MediaContractAuditor(string callId, ILogger logger, VoIPMediaSession mediaSession)
        {
            _callId = callId;
            _logger = logger;
            _mediaSession = mediaSession;
        }

        internal void Attach()
        {
            RTPChannel channel = _mediaSession.AudioStream.GetRTPChannel();
            lock (_gate)
            {
                if (_detached || _channel is not null)
                {
                    return;
                }

                _channel = channel;
                channel.OnRTPDataReceived += OnRtpData;
                channel.OnControlDataReceived += OnControlData;
            }
        }

        internal void CaptureOutboundSdp(SIPResponse response)
        {
            lock (_gate)
            {
                int cseq = response.Header.CSeq;
                if (_contractCSeq >= 0 && cseq != _contractCSeq)
                {
                    return;
                }
                if (_contractCSeq == cseq)
                {
                    if (!string.Equals(_contractBody, response.Body, StringComparison.Ordinal))
                    {
                        Warn("received a differing retransmitted 200 OK SDP; retaining the first contract");
                    }
                    return;
                }

                _contractCSeq = cseq;
                _contractBody = response.Body;
                _answerBoundaryReached = true;
                try
                {
                    _contract = MediaContract.Parse(response.Body, cseq);
                    _logger.LogDebug(
                        "Media auditor captured call {CallId} INVITE CSeq {CSeq}: {Contract}.",
                        _callId,
                        cseq,
                        _contract.Description);
                }
                catch (Exception exception)
                {
                    _contract = MediaContract.Unavailable(cseq, exception.Message);
                    _logger.LogWarning(exception, "Media auditor could not parse outbound SDP for call {CallId}.", _callId);
                }
            }
        }

        internal void Detach()
        {
            RTPChannel? channel;
            lock (_gate)
            {
                if (_detached)
                {
                    return;
                }
                _detached = true;
                channel = _channel;
                _channel = null;
            }

            if (channel is not null)
            {
                channel.OnRTPDataReceived -= OnRtpData;
                channel.OnControlDataReceived -= OnControlData;
            }
        }

        internal void Complete(long? sinkFrames)
        {
            MediaAuditorSummary summary;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }
                _completed = true;
                summary = new MediaAuditorSummary(
                    GetDiagnosis(sinkFrames),
                    _contract?.Description ?? "no outbound 200 OK SDP captured",
                    _rawRtp,
                    _rawRtcp,
                    _preAnswerMalformedRtp + _postAnswerMalformedRtp,
                    _preAnswerMalformedRtcp + _postAnswerMalformedRtcp,
                    _preAnswerPackets,
                    _postAnswerRtp,
                    _payloadTypeViolations,
                    _directionViolations,
                    sinkFrames,
                    _payloadTypes.ToImmutableDictionary(),
                    _remoteEndpoints.ToImmutableDictionary(),
                    _ssrcs.ToImmutableDictionary());
            }

            _logger.LogInformation(
                "Media auditor call {CallId} diagnosis {Diagnosis}. Contract: {Contract}. RTP={Rtp}, RTCP={Rtcp}, malformed RTP/RTCP={MalformedRtp}/{MalformedRtcp}, pre-answer={PreAnswer}, post-answer RTP={PostAnswerRtp}, PT violations={PayloadTypeViolations}, direction violations={DirectionViolations}, sink frames={SinkFrames}; PT validation cannot detect codec bytes mislabeled under another permitted PT.",
                _callId,
                summary.Diagnosis,
                summary.Contract,
                summary.RawRtp,
                summary.RawRtcp,
                summary.MalformedRtp,
                summary.MalformedRtcp,
                summary.PreAnswerPackets,
                summary.PostAnswerRtp,
                summary.PayloadTypeViolations,
                summary.DirectionViolations,
                summary.SinkFrames?.ToString() ?? "unavailable");
            _logger.LogTrace(
                "Media auditor call {CallId} PT counts {@PayloadTypes}, endpoints {@RemoteEndpoints}, SSRCs {@Ssrcs}.",
                _callId,
                summary.PayloadTypes,
                summary.RemoteEndpoints,
                summary.Ssrcs);
        }

        private void OnRtpData(int localPort, IPEndPoint remoteEndPoint, byte[] packet)
        {
            try
            {
                lock (_gate)
                {
                    if (_contract?.RtcpMux == true &&
                        RTCPCompoundPacket.TryParse(packet, out RTCPCompoundPacket? _, out int _))
                    {
                        RecordRtcp(localPort, remoteEndPoint, fromRtpSocket: true);
                        return;
                    }

                    if (!RTPPacket.TryParse(packet, out RTPPacket? rtp, out int _))
                    {
                        RecordMalformedRtp();
                        return;
                    }

                    RecordRtp(localPort, remoteEndPoint, rtp.Header);
                }
            }
            catch (Exception exception)
            {
                _logger.LogTrace(exception, "Media auditor ignored an RTP diagnostic failure for call {CallId}.", _callId);
            }
        }

        private void OnControlData(int localPort, IPEndPoint remoteEndPoint, byte[] packet)
        {
            try
            {
                lock (_gate)
                {
                    if (RTCPCompoundPacket.TryParse(packet, out RTCPCompoundPacket? _, out int _))
                    {
                        RecordRtcp(localPort, remoteEndPoint, fromRtpSocket: false);
                    }
                    else
                    {
                        RecordMalformedRtcp();
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.LogTrace(exception, "Media auditor ignored an RTCP diagnostic failure for call {CallId}.", _callId);
            }
        }

        private void RecordRtp(int localPort, IPEndPoint remoteEndPoint, RTPHeader header)
        {
            _rawRtp++;
            RecordEndpoint(remoteEndPoint);
            RecordSsrc(header.SyncSource);
            _payloadTypes.TryGetValue(header.PayloadType, out long ptCount);
            _payloadTypes[header.PayloadType] = ptCount + 1;
            if (!_answerBoundaryReached)
            {
                _preAnswerPackets++;
                return;
            }

            _postAnswerRtp++;
            MediaContract? contract = _contract;
            if (contract?.Available == true)
            {
                if (!contract.AllowedPayloadTypes.Contains(header.PayloadType))
                {
                    _payloadTypeViolations++;
                    Warn($"received RTP payload type {header.PayloadType} outside the outbound SDP contract");
                }
                if (contract.Direction is MediaStreamStatusEnum.SendOnly or MediaStreamStatusEnum.Inactive)
                {
                    _directionViolations++;
                    Warn("received RTP despite an outbound SDP direction that does not receive audio");
                }
            }

            _logger.LogTrace(
                "Media auditor call {CallId} RTP {Remote}->{LocalPort}: v{Version}, marker={Marker}, PT={PayloadType}, SSRC={Ssrc}, seq={Sequence}, ts={Timestamp}.",
                _callId,
                remoteEndPoint,
                localPort,
                header.Version,
                header.MarkerBit,
                header.PayloadType,
                header.SyncSource,
                header.SequenceNumber,
                header.Timestamp);
        }

        private void RecordRtcp(int localPort, IPEndPoint remoteEndPoint, bool fromRtpSocket)
        {
            _rawRtcp++;
            RecordEndpoint(remoteEndPoint);
            if (!_answerBoundaryReached)
            {
                _preAnswerPackets++;
            }
            _logger.LogTrace(
                "Media auditor call {CallId} RTCP {Remote}->{LocalPort} ({Socket}).",
                _callId,
                remoteEndPoint,
                localPort,
                fromRtpSocket ? "RTP mux socket" : "control socket");
        }

        private void RecordEndpoint(IPEndPoint endpoint)
        {
            string key = endpoint.ToString();
            if (_remoteEndpoints.TryGetValue(key, out long count))
            {
                _remoteEndpoints[key] = count + 1;
            }
            else if (_remoteEndpoints.Count < MaxDistinctEndpoints)
            {
                _remoteEndpoints[key] = 1;
            }
        }

        private void RecordSsrc(uint ssrc)
        {
            if (_ssrcs.TryGetValue(ssrc, out long count))
            {
                _ssrcs[ssrc] = count + 1;
            }
            else if (_ssrcs.Count < MaxDistinctSsrcs)
            {
                _ssrcs[ssrc] = 1;
            }
        }

        private void RecordMalformedRtp()
        {
            if (_answerBoundaryReached)
            {
                _postAnswerMalformedRtp++;
            }
            else
            {
                _preAnswerMalformedRtp++;
            }
            Warn("received malformed RTP data");
        }

        private void RecordMalformedRtcp()
        {
            if (_answerBoundaryReached)
            {
                _postAnswerMalformedRtcp++;
            }
            else
            {
                _preAnswerMalformedRtcp++;
            }
            Warn("received malformed RTCP data");
        }

        private string GetDiagnosis(long? sinkFrames)
        {
            if (_contract?.Available != true)
            {
                return "UnknownContract";
            }
            if (_directionViolations > 0)
            {
                return "DirectionViolation";
            }
            if (_payloadTypeViolations > 0)
            {
                return "PayloadTypeViolation";
            }
            if (_postAnswerRtp == 0 && _postAnswerMalformedRtp > 0)
            {
                return "MalformedRtp";
            }
            if (_postAnswerRtp == 0)
            {
                return "NoRawRtp";
            }
            if (sinkFrames is null)
            {
                return "SinkMetricUnavailable";
            }
            return sinkFrames.Value > 0 ? "ReachedApplicationSink" : "MediaProcessingGap";
        }

        private void Warn(string message)
        {
            if (_warningCount++ < MaxWarnings)
            {
                _logger.LogWarning("Media auditor call {CallId} {Message}.", _callId, message);
            }
        }
    }

    internal sealed record MediaAuditorSummary(
        string Diagnosis,
        string Contract,
        long RawRtp,
        long RawRtcp,
        long MalformedRtp,
        long MalformedRtcp,
        long PreAnswerPackets,
        long PostAnswerRtp,
        long PayloadTypeViolations,
        long DirectionViolations,
        long? SinkFrames,
        ImmutableDictionary<int, long> PayloadTypes,
        ImmutableDictionary<string, long> RemoteEndpoints,
        ImmutableDictionary<uint, long> Ssrcs);

    internal sealed record MediaContract(
        bool Available,
        int InviteCSeq,
        MediaStreamStatusEnum Direction,
        string RtpAddress,
        int RtpPort,
        string RtcpAddress,
        int RtcpPort,
        string? RawRtcpAttribute,
        bool RtcpMux,
        ImmutableHashSet<int> AllowedPayloadTypes,
        ImmutableArray<string> AudioFormats,
        ImmutableArray<string> TelephoneEventFormats,
        ImmutableArray<string> ComfortNoiseFormats,
        string Description)
    {
        internal static MediaContract Parse(string body, int cseq)
        {
            SDP sdp = SDP.ParseSDPDescription(body);
            SDPMediaAnnouncement audio = sdp.Media.FirstOrDefault(media =>
                media.Media == SDPMediaTypesEnum.audio &&
                media.Port > 0 &&
                media.MediaStreamStatus != MediaStreamStatusEnum.Inactive)
                ?? throw new InvalidOperationException("The SDP did not contain an active audio announcement.");
            SDPConnectionInformation connection = audio.Connection ?? sdp.Connection
                ?? throw new InvalidOperationException("The active audio announcement had no connection address.");
            MediaStreamStatusEnum direction = audio.MediaStreamStatus ??
                sdp.SessionMediaStreamStatus ?? SDP.DEFAULT_STREAM_STATUS;
            bool rtcpMux = audio.ExtraMediaAttributes.Any(attribute =>
                string.Equals(attribute, "rtcp-mux", StringComparison.OrdinalIgnoreCase));
            ImmutableArray<SDPAudioVideoMediaFormat> formats = audio.MediaFormats.Values.ToImmutableArray();
            ImmutableArray<string> audioFormats = formats
                .Where(format => !IsTelephoneEvent(format) && !IsComfortNoise(format))
                .Select(DescribeFormat)
                .ToImmutableArray();
            ImmutableArray<string> telephoneEvents = formats.Where(IsTelephoneEvent).Select(DescribeFormat).ToImmutableArray();
            ImmutableArray<string> comfortNoise = formats.Where(IsComfortNoise).Select(DescribeFormat).ToImmutableArray();
            ImmutableHashSet<int> payloadTypes = formats.Select(format => format.ID).ToImmutableHashSet();
            string? rawRtcpAttribute = audio.ExtraMediaAttributes.FirstOrDefault(attribute =>
                attribute.StartsWith("rtcp:", StringComparison.OrdinalIgnoreCase));
            bool rtcpEndpointUnresolved = rawRtcpAttribute is not null;
            int rtcpPort = rtcpEndpointUnresolved ? 0 : rtcpMux ? audio.Port : audio.Port + 1;
            string rtcpAddress = rtcpEndpointUnresolved ? "unresolved" : connection.ConnectionAddress;
            string rtcpDescription = rtcpEndpointUnresolved
                ? $"unresolved ({rawRtcpAttribute})"
                : $"{rtcpAddress}:{rtcpPort}";
            string description = $"direction={direction}, RTP={connection.ConnectionAddress}:{audio.Port}, RTCP={rtcpDescription}, mux={rtcpMux}, audio=[{string.Join(", ", audioFormats)}], telephone-event=[{string.Join(", ", telephoneEvents)}], CN=[{string.Join(", ", comfortNoise)}]";
            return new MediaContract(
                true,
                cseq,
                direction,
                connection.ConnectionAddress,
                audio.Port,
                rtcpAddress,
                rtcpPort,
                rawRtcpAttribute,
                rtcpMux,
                payloadTypes,
                audioFormats,
                telephoneEvents,
                comfortNoise,
                description);
        }

        internal static MediaContract Unavailable(int cseq, string reason)
            => new(
                false,
                cseq,
                MediaStreamStatusEnum.Inactive,
                "unavailable",
                0,
                "unavailable",
                0,
                null,
                false,
                ImmutableHashSet<int>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                $"unavailable ({reason})");

        private static bool IsTelephoneEvent(SDPAudioVideoMediaFormat format)
            => string.Equals(format.Name(), "telephone-event", StringComparison.OrdinalIgnoreCase);

        private static bool IsComfortNoise(SDPAudioVideoMediaFormat format)
            => string.Equals(format.Name(), "CN", StringComparison.OrdinalIgnoreCase);

        private static string DescribeFormat(SDPAudioVideoMediaFormat format)
            => $"{format.ID}:{format.Name()}/{format.ClockRate()} ({format.Rtpmap})";
    }
}

namespace SIP2Agent.UserAgentService.Integration.LibRTIC
{
    internal sealed partial class LibRTICCallAgent : Service.IMediaAuditorFrameMetrics, Service.IMediaAuditorRawRecordingTarget
    {
        public long MediaAuditorSinkFrameCount => _audioBridge.MediaAuditorSinkFrameCount;

        public void EnableMediaAuditorRawRecording(string callId, string directoryPath)
            => _audioBridge.EnableMediaAuditorRawRecording(callId, directoryPath);
    }
}

#endif
