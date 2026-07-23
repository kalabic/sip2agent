using System.Net;
using System.Threading.Channels;
using AudioFormatLib;
using AudioFormatLib.Buffers;
using AudioFormatLib.IO;
using AudioFormatLib.Utils;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace SIP2Agent.UserAgentService.Service;

internal sealed partial class RealtimeCallerAudioSink : IAudioSink, IDisposable
{
    internal const int InputChannelCapacity = 10;
    internal const int RealtimeSampleRate = 24_000;

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly AudioFormatNegotiation _formats = new();
    private readonly AudioEncoder _decoder;
    private readonly AudioStreamBuffer _callerAudioBuffer;
    private readonly IPcm16FrameInput _callerAudioInput;
    private readonly Channel<InboundFrame> _frames;
    private readonly CancellationTokenSource _workerCancellation = new();
    private readonly Action<Exception> _onFault;

    private MediaEndpointState _state;
    private Task? _worker;
    private long _epoch;
    private long _droppedFrames;
    private int _faulted;
    private int _disposed;

    public event SourceErrorDelegate? OnAudioSinkError;

    internal long DroppedFrameCount => Interlocked.Read(ref _droppedFrames);

    internal IPcm16FrameOutput CallerAudioOutput { get; }

    internal RealtimeCallerAudioSink(ILogger logger, Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(onFault);

        _logger = logger;
        _onFault = onFault;
        _decoder = new AudioEncoder(AudioFormatNegotiation.CreateSupportedFormats().ToArray());
        _callerAudioBuffer = AudioStreamBuffer.CreateForDuration(
            new APcmFormat(
                ASampleValueFormat.S16,
                RealtimeSampleRate,
                1,
                byteOrder: AByteOrder.LittleEndian),
            TimeSpan.FromSeconds(2));
        _callerAudioInput = _callerAudioBuffer.Input.Pcm16Frames
            ?? throw new InvalidOperationException("The caller audio buffer is not PCM16-compatible.");
        CallerAudioOutput = _callerAudioBuffer.Output.Pcm16Frames
            ?? throw new InvalidOperationException("The caller audio buffer is not PCM16-compatible.");
        _frames = Channel.CreateBounded<InboundFrame>(
            new BoundedChannelOptions(InputChannelCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            },
            _ => Interlocked.Increment(ref _droppedFrames));
    }

    void IAudioSink.RestrictFormats(Func<AudioFormat, bool> filter)
    {
        lock (_gate)
        {
            _formats.RestrictFormats(filter);
        }
    }

    public List<AudioFormat> GetAudioSinkFormats()
    {
        lock (_gate)
        {
            return _formats.GetFormats();
        }
    }

    public void SetAudioSinkFormat(AudioFormat audioFormat)
    {
        Exception? activeChangeFailure = null;
        lock (_gate)
        {
            if (_state != MediaEndpointState.NotStarted)
            {
                if (!AudioFormatNegotiation.AreEquivalent(_formats.SelectedFormat, audioFormat))
                {
                    activeChangeFailure = new InvalidOperationException(
                        "The negotiated caller-audio format cannot change after its worker starts.");
                }
            }
            else
            {
                _formats.Select(audioFormat);
            }
        }

        if (activeChangeFailure is not null)
        {
            Fail(activeChangeFailure);
        }
    }

    public Task StartAudioSink()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_state == MediaEndpointState.Closed)
            {
                throw new InvalidOperationException("The caller-audio sink has already been closed.");
            }
            if (_state is MediaEndpointState.Running or MediaEndpointState.Paused)
            {
                return Task.CompletedTask;
            }

            _state = MediaEndpointState.Running;
            _worker = Task.Run(() => RunWorkerAsync(_workerCancellation.Token));
        }

        return Task.CompletedTask;
    }

    public Task PauseAudioSink()
    {
        lock (_gate)
        {
            if (_state == MediaEndpointState.Running)
            {
                _state = MediaEndpointState.Paused;
                _epoch++;
            }
        }

        return Task.CompletedTask;
    }

    public Task ResumeAudioSink()
    {
        lock (_gate)
        {
            if (_state == MediaEndpointState.Paused)
            {
                _state = MediaEndpointState.Running;
            }
        }

        return Task.CompletedTask;
    }

    public Task CloseAudioSink()
    {
        Task? worker;
        lock (_gate)
        {
            RequestStopUnderLock();
            worker = _worker;
        }

        return worker ?? Task.CompletedTask;
    }

    internal bool IsPaused()
    {
        lock (_gate)
        {
            return _state == MediaEndpointState.Paused;
        }
    }

    public void GotEncodedMediaFrame(EncodedAudioFrame encodedMediaFrame)
    {
        RecordMediaAuditorFrame(encodedMediaFrame);
        try
        {
            long epoch;
            lock (_gate)
            {
                if (_state is MediaEndpointState.Closed or MediaEndpointState.Paused)
                {
                    return;
                }

                epoch = _epoch;
            }

            _frames.Writer.TryWrite(InboundFrame.CopyFrom(encodedMediaFrame, epoch));
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    [Obsolete("Use GotEncodedMediaFrame instead.")]
    public void GotAudioRtp(
        IPEndPoint remoteEndPoint,
        uint ssrc,
        uint seqnum,
        uint timestamp,
        int payloadID,
        bool marker,
        byte[] payload)
        => _logger.LogTrace(
            "Ignoring legacy raw RTP callback from {RemoteEndPoint} for payload {PayloadId}.",
            remoteEndPoint,
            payloadID);

    internal void RequestStop()
    {
        lock (_gate)
        {
            RequestStopUnderLock();
        }
    }

    internal void DisposeOwnedResources()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CloseMediaAuditorRecording();
        _decoder.Dispose();
        _callerAudioBuffer.Dispose();
        _workerCancellation.Dispose();
        OnAudioSinkError = null;
    }

    public void Dispose()
    {
        Task worker = CloseAudioSink();
        if (worker.IsCompleted)
        {
            DisposeOwnedResources();
        }
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        AudioResampler? resampler = null;
        long workerEpoch = -1;
        int workerSampleRate = 0;
        int samplesWithoutOutput = 0;

        try
        {
            await foreach (InboundFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                long currentEpoch = GetEpoch();
                if (frame.Epoch != currentEpoch)
                {
                    continue;
                }

                AudioCodecProfile profile = AudioFormatNegotiation.GetProfile(frame.Format);
                if (workerEpoch != frame.Epoch || workerSampleRate != profile.PcmSampleRate)
                {
                    resampler?.Dispose();
                    resampler = AudioResampler.CreatePcm16(
                        profile.PcmSampleRate,
                        RealtimeSampleRate);
                    samplesWithoutOutput = 0;
                    workerEpoch = frame.Epoch;
                    workerSampleRate = profile.PcmSampleRate;
                }

                short[] decoded = _decoder.DecodeAudio(frame.Payload, frame.Format);
                AudioFormatNegotiation.ValidateDecodedPayload(
                    profile,
                    frame.Payload.Length,
                    decoded.Length);

                short[] converted = resampler!.Process(decoded, endOfInput: false);
                if (converted.Length == 0)
                {
                    samplesWithoutOutput += decoded.Length;
                    if (samplesWithoutOutput >= profile.PcmSamplesPerPacket)
                    {
                        throw new InvalidDataException(
                            "The SIP input resampler produced no audio for a complete packet.");
                    }
                }
                else
                {
                    samplesWithoutOutput = 0;
                    if (!_callerAudioInput.TryWrite(converted.AsSpan()))
                    {
                        throw new InvalidOperationException(
                            "The caller audio buffer is closed or does not have enough free space.");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_workerCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
        finally
        {
            resampler?.Dispose();
        }
    }

    private long GetEpoch()
    {
        lock (_gate)
        {
            return _epoch;
        }
    }

    private void RequestStopUnderLock()
    {
        if (_state == MediaEndpointState.Closed)
        {
            return;
        }

        _state = MediaEndpointState.Closed;
        _epoch++;
        _frames.Writer.TryComplete();
        _workerCancellation.Cancel();
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        _logger.LogError(exception, "The caller-to-Realtime audio pipeline failed.");
        _onFault(exception);
        RequestStop();
        RaiseErrorSafely("The incoming audio pipeline failed.");
    }

    private void RaiseErrorSafely(string message)
    {
        SourceErrorDelegate? handlers = OnAudioSinkError;
        if (handlers is null)
        {
            return;
        }

        foreach (SourceErrorDelegate handler in handlers.GetInvocationList())
        {
            try
            {
                handler(message);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "An audio-sink error subscriber threw.");
            }
        }
    }

    private sealed record InboundFrame(AudioFormat Format, byte[] Payload, long Epoch)
    {
        internal static InboundFrame CopyFrom(EncodedAudioFrame frame, long epoch)
        {
            ArgumentNullException.ThrowIfNull(frame);
            ArgumentNullException.ThrowIfNull(frame.AudioFormat);
            ArgumentNullException.ThrowIfNull(frame.EncodedAudio);
            if (frame.EncodedAudio.Length == 0)
            {
                throw new InvalidDataException("An encoded SIP audio frame was empty.");
            }

            return new InboundFrame(
                new AudioFormat(frame.AudioFormat),
                frame.EncodedAudio.ToArray(),
                epoch);
        }
    }

    partial void RecordMediaAuditorFrame(EncodedAudioFrame encodedMediaFrame);

    partial void CloseMediaAuditorRecording();
}
