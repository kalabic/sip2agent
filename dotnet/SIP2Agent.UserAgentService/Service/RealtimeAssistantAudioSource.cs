using AudioFormatLib;
using AudioFormatLib.Buffers;
using AudioFormatLib.Extensions;
using AudioFormatLib.Utils;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System.Threading.Channels;

namespace SIP2Agent.UserAgentService.Service;

internal sealed class RealtimeAssistantAudioSource : IAudioSource, IDisposable
{
    internal const int SipSampleRate = 8_000;
    internal const int RealtimeSampleRate = 24_000;
    internal const int SipSamplesPerPacket = 160;
    internal const int OutputPrebufferPackets = 2;
    internal const int OutputMaxRealtimeSamples = 720_000;
    internal const int OutputCommandCapacity = 2_048;
    private const int RealtimeSamplesPer20Milliseconds = 480;

    private static readonly TimeSpan PacketDuration = TimeSpan.FromMilliseconds(20);

    private readonly object _gate = new();
    private readonly object _playbackGate = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly long _packetTimestampUnits;
    private readonly AudioEncoder _encoder;
    private readonly G711FormatNegotiation _formats = new();
    private readonly Channel<OutputCommand> _commands;
    private readonly CancellationTokenSource _workerCancellation = new();
    private readonly Action<Exception> _onFault;

    private IRealtimeAgentSession? _session;
    private MediaEndpointState _state;
    private Task? _worker;
    private PlaybackCursor? _playbackCursor;
    private RealtimeOutputIdentity? _cancelledIdentity;
    private long _epoch;
    private long _unplayedRealtimeSamples;
    private long _outputOverflowCount;
    private int _faulted;
    private int _disposed;

    public event EncodedSampleDelegate? OnAudioSourceEncodedSample;

#pragma warning disable CS0067
    public event Action<EncodedAudioFrame>? OnAudioSourceEncodedFrameReady;
#pragma warning restore CS0067

    [Obsolete("The audio source only generates encoded samples.")]
    public event RawAudioSampleDelegate OnAudioSourceRawSample
    {
        add { }
        remove { }
    }

    public event SourceErrorDelegate? OnAudioSourceError;

    internal long OutputOverflowCount => Interlocked.Read(ref _outputOverflowCount);

    internal long UnplayedRealtimeSampleCount => Interlocked.Read(ref _unplayedRealtimeSamples);

    internal RealtimeAssistantAudioSource(
        ILogger logger,
        TimeProvider timeProvider,
        Action<Exception> onFault)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(onFault);

        _logger = logger;
        _timeProvider = timeProvider;
        _onFault = onFault;
        _packetTimestampUnits = Math.Max(
            1,
            (long)Math.Round(PacketDuration.TotalSeconds * _timeProvider.TimestampFrequency));

        _encoder = new AudioEncoder(
        [
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA),
        ]);
        _commands = Channel.CreateBounded<OutputCommand>(
            new BoundedChannelOptions(OutputCommandCapacity)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
            });
    }

    internal void AttachSession(IRealtimeAgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_session is not null)
            {
                throw new InvalidOperationException("A Realtime session is already attached.");
            }

            _session = session;
            session.MediaUpdate += HandleMediaUpdate;
        }
    }

    void IAudioSource.RestrictFormats(Func<AudioFormat, bool> filter)
    {
        lock (_gate)
        {
            _formats.RestrictFormats(filter);
        }
    }

    public List<AudioFormat> GetAudioSourceFormats()
    {
        lock (_gate)
        {
            return _formats.GetFormats();
        }
    }

    public void SetAudioSourceFormat(AudioFormat audioFormat)
    {
        Exception? activeChangeFailure = null;
        lock (_gate)
        {
            if (_state != MediaEndpointState.NotStarted)
            {
                if (!G711FormatNegotiation.AreEquivalent(_formats.SelectedFormat, audioFormat))
                {
                    activeChangeFailure = new InvalidOperationException(
                        "The negotiated assistant-audio format cannot change after its worker starts.");
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

    public bool HasEncodedAudioSubscribers()
        => OnAudioSourceEncodedSample is not null;

    public bool IsAudioSourcePaused()
    {
        lock (_gate)
        {
            return _state == MediaEndpointState.Paused;
        }
    }

    public void ExternalAudioSourceRawSample(
        AudioSamplingRatesEnum samplingRate,
        uint durationMilliseconds,
        short[] sample)
        => _logger.LogTrace(
            "Ignoring raw external audio ({SamplingRate}, {DurationMilliseconds} ms, {SampleCount} samples).",
            samplingRate,
            durationMilliseconds,
            sample?.Length ?? 0);

    public Task StartAudio()
    {
        lock (_gate)
        {
            EnsureSessionAttached();
            if (_state == MediaEndpointState.Closed)
            {
                throw new InvalidOperationException("The assistant-audio source has already been closed.");
            }
            if (_state is MediaEndpointState.Running or MediaEndpointState.Paused)
            {
                return Task.CompletedTask;
            }

            _state = MediaEndpointState.Running;
            _worker = Task.Run(() => RunOutputWorkerAsync(_workerCancellation.Token));
        }

        return Task.CompletedTask;
    }

    public Task PauseAudio()
    {
        long epoch;
        lock (_gate)
        {
            if (_state != MediaEndpointState.Running)
            {
                return Task.CompletedTask;
            }

            _state = MediaEndpointState.Paused;
            epoch = ++_epoch;
        }

        if (!_commands.Writer.TryWrite(new ResetOutput(epoch)))
        {
            SignalOutputOverflow("The output command queue could not accept a playback reset.");
        }

        return Task.CompletedTask;
    }

    public Task ResumeAudio()
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

    public Task CloseAudio()
    {
        Task? worker;
        lock (_gate)
        {
            RequestStopUnderLock();
            worker = _worker;
        }

        return worker ?? Task.CompletedTask;
    }

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

        DetachSession();
        _encoder.Dispose();
        _workerCancellation.Dispose();
        Interlocked.Exchange(ref _unplayedRealtimeSamples, 0);
        OnAudioSourceEncodedSample = null;
        OnAudioSourceError = null;
    }

    public void Dispose()
    {
        Task worker = CloseAudio();
        if (worker.IsCompleted)
        {
            DisposeOwnedResources();
        }
    }

    private async Task RunOutputWorkerAsync(CancellationToken cancellationToken)
    {
        ActiveOutput? active = null;
        Task<bool>? pendingRead = null;
        bool idleSilence = false;
        long nextIdlePacketTimestamp = 0;
        Task? idlePacketDeadlineTask = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (active is not null && IsPlaybackDue(active))
                {
                    if (EmitPacket(ref active))
                    {
                        StartIdleSilence(ref idleSilence, ref nextIdlePacketTimestamp, ref idlePacketDeadlineTask);
                    }
                    continue;
                }

                if (ShouldEmitIdleSilence(active, idleSilence, nextIdlePacketTimestamp))
                {
                    EmitIdleSilence(ref nextIdlePacketTimestamp, ref idlePacketDeadlineTask);
                    continue;
                }

                if (_commands.Reader.TryRead(out OutputCommand? command))
                {
                    if (command is InterruptOutput interrupt)
                    {
                        if (interrupt.Epoch == GetEpoch())
                        {
                            ClearActive(ref active);
                            await ProcessInterruptAsync(interrupt, cancellationToken)
                                .ConfigureAwait(false);
                            StartIdleSilence(ref idleSilence, ref nextIdlePacketTimestamp, ref idlePacketDeadlineTask);
                        }
                        continue;
                    }

                    if (command is ResetOutput)
                    {
                        idleSilence = false;
                        idlePacketDeadlineTask = null;
                    }
                    ProcessOutputCommand(command, ref active);
                    continue;
                }

                pendingRead ??= _commands.Reader
                    .WaitToReadAsync(cancellationToken)
                    .AsTask();

                Task? playbackDeadline = active is not null && ShouldPace(active)
                    ? active.PacketDeadlineTask
                    : null;
                Task? idleDeadline = ShouldPaceIdleSilence(active, idleSilence)
                    ? idlePacketDeadlineTask
                    : null;
                if (playbackDeadline is null && idleDeadline is null)
                {
                    bool canRead = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    if (!canRead)
                    {
                        break;
                    }
                    continue;
                }

                Task delayTask = playbackDeadline ?? idleDeadline
                    ?? throw new InvalidOperationException("The output packet deadline was not scheduled.");
                Task completed = await Task.WhenAny(pendingRead, delayTask).ConfigureAwait(false);
                if (completed == pendingRead)
                {
                    bool canRead = await pendingRead.ConfigureAwait(false);
                    pendingRead = null;
                    if (!canRead)
                    {
                        break;
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
            ClearActive(ref active);
            while (_commands.Reader.TryRead(out OutputCommand? command))
            {
                if (command is AudioDelta delta)
                {
                    ReleaseOutputBudget(delta.Pcm16At24Khz.Length / sizeof(short));
                }
            }
        }
    }

    private void ProcessOutputCommand(OutputCommand command, ref ActiveOutput? active)
    {
        long currentEpoch = GetEpoch();
        if (command.Epoch != currentEpoch)
        {
            if (command is AudioDelta staleDelta)
            {
                ReleaseOutputBudget(staleDelta.Pcm16At24Khz.Length / sizeof(short));
            }
            return;
        }

        switch (command)
        {
            case ResetOutput:
                ClearActive(ref active);
                return;

            case AudioDelta delta:
                ProcessAudioDelta(delta, ref active);
                return;

            case AudioFinished finished:
                ProcessAudioFinished(finished, ref active);
                return;

            default:
                throw new InvalidOperationException(
                    $"Unsupported output command {command.GetType().Name}.");
        }
    }

    private void ProcessAudioDelta(AudioDelta delta, ref ActiveOutput? active)
    {
        int realtimeSamples = delta.Pcm16At24Khz.Length / sizeof(short);
        if (active is null)
        {
            active = new ActiveOutput(
                delta.Identity,
                delta.Epoch,
                AudioResampler.CreatePcm16(RealtimeSampleRate, SipSampleRate),
                _workerCancellation.Token);
        }
        else if (active.Identity != delta.Identity)
        {
            ReleaseOutputBudget(realtimeSamples);
            throw new InvalidDataException(
                "Realtime output audio overlapped a different active response item.");
        }

        short[] input = Pcm16LittleEndian.Decode(delta.Pcm16At24Khz);
        active.TotalRealtimeSamplesReceived += input.Length;
        active.ReservedRealtimeSamples += input.Length;

        short[] converted = active.Resampler.Process(input, endOfInput: false);
        if (converted.Length == 0)
        {
            active.RealtimeSamplesWithoutOutput += input.Length;
            if (active.RealtimeSamplesWithoutOutput >= RealtimeSamplesPer20Milliseconds)
            {
                throw new InvalidDataException(
                    "The Realtime output resampler produced no audio for a complete block.");
            }
        }
        else
        {
            active.RealtimeSamplesWithoutOutput = 0;
            active.PendingSipAudio.WriteSampleValuesExactly(converted, 0, converted.Length);
            active.TotalSipSamplesProduced += converted.Length;
        }

        if (!active.PlaybackStarted &&
            active.PendingSipAudio.StoredSampleCount >= OutputPrebufferPackets * SipSamplesPerPacket)
        {
            StartPlayback(active);
        }
    }

    private void ProcessAudioFinished(AudioFinished finished, ref ActiveOutput? active)
    {
        if (active is null)
        {
            active = new ActiveOutput(
                finished.Identity,
                finished.Epoch,
                AudioResampler.CreatePcm16(RealtimeSampleRate, SipSampleRate),
                _workerCancellation.Token);
        }
        else if (active.Identity != finished.Identity)
        {
            throw new InvalidDataException(
                "The Realtime audio completion marker did not match the active response item.");
        }

        short[] flushed = active.Resampler.Process(Array.Empty<short>(), endOfInput: true);
        long expectedOutput = (active.TotalRealtimeSamplesReceived + 2) / 3;
        long requiredFlush = expectedOutput - active.TotalSipSamplesProduced;
        if (requiredFlush < 0)
        {
            throw new InvalidDataException(
                "The Realtime output resampler produced more samples than expected before final flush.");
        }
        if (flushed.LongLength < requiredFlush)
        {
            throw new InvalidDataException(
                "The Realtime output resampler final flush was shorter than expected.");
        }
        if (flushed.LongLength > requiredFlush)
        {
            Array.Resize(ref flushed, checked((int)requiredFlush));
        }

        if (flushed.Length > 0)
        {
            active.PendingSipAudio.WriteSampleValuesExactly(flushed, 0, flushed.Length);
        }
        active.TotalSipSamplesProduced += flushed.Length;
        active.IsFinal = true;

        if (!active.PlaybackStarted)
        {
            StartPlayback(active);
        }
    }

    private bool IsPlaybackDue(ActiveOutput active)
        => active.Epoch == GetEpoch() &&
           ShouldPace(active) &&
           _timeProvider.GetTimestamp() >= active.NextPacketTimestamp;

    private async Task ProcessInterruptAsync(
        InterruptOutput interrupt,
        CancellationToken cancellationToken)
    {
        IRealtimeAgentSession session = GetSession();
        if (!interrupt.Cursor.IsFinal)
        {
            await session.InterruptResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        TimeSpan playedAudio = TimeSpan.FromSeconds(
            interrupt.Cursor.RealSipSamplesEmitted / (double)SipSampleRate);
        await session.TruncateOutputItemAsync(
            interrupt.Cursor.ItemId,
            interrupt.Cursor.ContentIndex,
            playedAudio,
            cancellationToken).ConfigureAwait(false);
    }

    private bool ShouldPace(ActiveOutput active)
    {
        lock (_gate)
        {
            return _state == MediaEndpointState.Running && active.PlaybackStarted;
        }
    }

    private bool ShouldPaceIdleSilence(ActiveOutput? active, bool idleSilence)
    {
        lock (_gate)
        {
            return _state == MediaEndpointState.Running &&
                idleSilence &&
                (active is null || !active.PlaybackStarted);
        }
    }

    private bool ShouldEmitIdleSilence(
        ActiveOutput? active,
        bool idleSilence,
        long nextIdlePacketTimestamp)
        => ShouldPaceIdleSilence(active, idleSilence) &&
            _timeProvider.GetTimestamp() >= nextIdlePacketTimestamp;

    private bool EmitPacket(ref ActiveOutput? active)
    {
        ActiveOutput current = active
            ?? throw new InvalidOperationException("There is no active output to pace.");

        lock (_playbackGate)
        {
            if (current.Epoch != GetEpoch())
            {
                ClearActive(ref active);
                return false;
            }

            if (current.IsFinal && current.PendingSipAudio.StoredSampleCount == 0)
            {
                ClearActive(ref active);
                return true;
            }

            short[] packet = new short[SipSamplesPerPacket];
            int realSamples;
            if (current.PendingSipAudio.StoredSampleCount >= SipSamplesPerPacket)
            {
                realSamples = SipSamplesPerPacket;
                current.PendingSipAudio.ReadSampleValuesExactly(packet, 0, realSamples);
            }
            else if (current.IsFinal)
            {
                realSamples = current.PendingSipAudio.StoredSampleCount;
                if (realSamples > 0)
                {
                    current.PendingSipAudio.ReadSampleValuesExactly(packet, 0, realSamples);
                }
            }
            else
            {
                realSamples = 0;
            }

            AudioFormat selectedFormat = GetSelectedSourceFormat();
            byte[] encoded = _encoder.EncodeAudio(packet, selectedFormat);
            if (encoded.Length != SipSamplesPerPacket)
            {
                throw new InvalidDataException(
                    $"The {selectedFormat.FormatName} encoder returned {encoded.Length} bytes for a 20 ms packet.");
            }

            bool completesResponse = current.IsFinal && current.PendingSipAudio.StoredSampleCount == 0;
            if (!completesResponse)
            {
                ScheduleNextPacket(current);
            }

            OnAudioSourceEncodedSample?.Invoke(SipSamplesPerPacket, encoded);

            if (realSamples > 0)
            {
                current.TotalRealSipSamplesEmitted += realSamples;
                long release = Math.Min(current.ReservedRealtimeSamples, realSamples * 3L);
                current.ReservedRealtimeSamples -= release;
                ReleaseOutputBudget(release);
                PublishPlaybackCursor(current);
            }

            if (completesResponse)
            {
                ClearActive(ref active);
            }

            return completesResponse;
        }
    }

    private void StartIdleSilence(
        ref bool idleSilence,
        ref long nextIdlePacketTimestamp,
        ref Task? idlePacketDeadlineTask)
    {
        idleSilence = true;
        nextIdlePacketTimestamp = checked(_timeProvider.GetTimestamp() + _packetTimestampUnits);
        idlePacketDeadlineTask = Task.Delay(
            GetDelayUntil(nextIdlePacketTimestamp),
            _timeProvider,
            _workerCancellation.Token);
    }

    private void EmitIdleSilence(ref long nextIdlePacketTimestamp, ref Task? idlePacketDeadlineTask)
    {
        lock (_playbackGate)
        {
            byte[] encoded = _encoder.EncodeAudio(new short[SipSamplesPerPacket], GetSelectedSourceFormat());
            if (encoded.Length != SipSamplesPerPacket)
            {
                throw new InvalidDataException("The G.711 encoder returned an invalid silence packet length.");
            }

            OnAudioSourceEncodedSample?.Invoke(SipSamplesPerPacket, encoded);
            long now = _timeProvider.GetTimestamp();
            TimeSpan lateness = now >= nextIdlePacketTimestamp
                ? _timeProvider.GetElapsedTime(nextIdlePacketTimestamp, now)
                : TimeSpan.Zero;
            nextIdlePacketTimestamp = lateness >= PacketDuration
                ? checked(now + _packetTimestampUnits)
                : checked(nextIdlePacketTimestamp + _packetTimestampUnits);
            idlePacketDeadlineTask = Task.Delay(
                GetDelayUntil(nextIdlePacketTimestamp),
                _timeProvider,
                _workerCancellation.Token);
        }
    }

    private void ScheduleNextPacket(ActiveOutput current)
    {
        long now = _timeProvider.GetTimestamp();
        TimeSpan lateness = now >= current.NextPacketTimestamp
            ? _timeProvider.GetElapsedTime(current.NextPacketTimestamp, now)
            : TimeSpan.Zero;
        current.NextPacketTimestamp = lateness >= PacketDuration
            ? checked(now + _packetTimestampUnits)
            : checked(current.NextPacketTimestamp + _packetTimestampUnits);
        current.PacketDeadlineTask = Task.Delay(
            GetDelayUntil(current.NextPacketTimestamp),
            _timeProvider,
            current.PacingCancellation.Token);
    }

    private void StartPlayback(ActiveOutput active)
    {
        active.PlaybackStarted = true;
        active.NextPacketTimestamp = _timeProvider.GetTimestamp();
        active.PacketDeadlineTask = Task.CompletedTask;
    }

    private TimeSpan GetDelayUntil(long timestamp)
    {
        long now = _timeProvider.GetTimestamp();
        return timestamp <= now
            ? TimeSpan.Zero
            : _timeProvider.GetElapsedTime(now, timestamp);
    }

    private void HandleMediaUpdate(RealtimeAgentMediaUpdate update)
    {
        try
        {
            switch (update)
            {
                case RealtimeOutputAudioDelta delta:
                    HandleOutputAudioDelta(delta);
                    break;
                case RealtimeOutputAudioFinished finished:
                    HandleOutputAudioFinished(finished);
                    break;
                case RealtimeInputSpeechStarted:
                    HandleInputSpeechStarted();
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported Realtime media update {update.GetType().Name}.");
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void HandleOutputAudioDelta(RealtimeOutputAudioDelta update)
    {
        byte[] pcm = update.Pcm16LittleEndian.ToArray();
        if (pcm.Length == 0)
        {
            return;
        }
        if ((pcm.Length & 1) != 0)
        {
            throw new InvalidDataException("Realtime PCM audio must contain complete 16-bit sample values.");
        }

        if (!TryAcceptOutputEvent(update.Identity, isFinal: false, out long epoch))
        {
            return;
        }

        int samples = pcm.Length / sizeof(short);
        if (!TryReserveOutputBudget(samples))
        {
            SignalOutputOverflow("Realtime output exceeded the 30 second playback budget.");
            return;
        }

        if (!_commands.Writer.TryWrite(new AudioDelta(update.Identity, pcm, epoch)))
        {
            ReleaseOutputBudget(samples);
            SignalOutputOverflow("The Realtime output command queue is full.");
        }
    }

    private void HandleOutputAudioFinished(RealtimeOutputAudioFinished update)
    {
        if (!TryAcceptOutputEvent(update.Identity, isFinal: true, out long epoch))
        {
            return;
        }

        if (!_commands.Writer.TryWrite(new AudioFinished(update.Identity, epoch)))
        {
            SignalOutputOverflow("The Realtime output command queue is full.");
        }
    }

    private void HandleInputSpeechStarted()
    {
        PlaybackCursor? cursor;
        long epoch;
        lock (_playbackGate)
        {
            lock (_gate)
            {
                if (_state != MediaEndpointState.Running ||
                    _playbackCursor is null ||
                    Volatile.Read(ref _faulted) != 0)
                {
                    return;
                }

                cursor = _playbackCursor;
                epoch = ++_epoch;
                _cancelledIdentity = cursor.Identity;
                _playbackCursor = null;
            }
        }

        if (!_commands.Writer.TryWrite(new InterruptOutput(cursor, epoch)))
        {
            SignalOutputOverflow("The output command queue could not accept a barge-in request.");
        }
    }

    private bool TryAcceptOutputEvent(
        RealtimeOutputIdentity identity,
        bool isFinal,
        out long epoch)
    {
        epoch = 0;
        if (Volatile.Read(ref _faulted) != 0)
        {
            return false;
        }

        lock (_gate)
        {
            if (_state is not MediaEndpointState.NotStarted and not MediaEndpointState.Running)
            {
                return false;
            }

            if (_cancelledIdentity == identity)
            {
                return false;
            }

            if (_cancelledIdentity is not null && _cancelledIdentity != identity)
            {
                _cancelledIdentity = null;
            }

            if (_playbackCursor is null)
            {
                _playbackCursor = new PlaybackCursor(identity, _epoch, 0, isFinal);
            }
            else if (_playbackCursor.Identity == identity)
            {
                _playbackCursor = _playbackCursor with
                {
                    IsFinal = _playbackCursor.IsFinal || isFinal,
                };
            }

            epoch = _epoch;
            return true;
        }
    }

    private void PublishPlaybackCursor(ActiveOutput active)
    {
        lock (_gate)
        {
            if (_playbackCursor is not null &&
                _playbackCursor.Identity == active.Identity &&
                _playbackCursor.Epoch == active.Epoch)
            {
                _playbackCursor = _playbackCursor with
                {
                    RealSipSamplesEmitted = active.TotalRealSipSamplesEmitted,
                    IsFinal = _playbackCursor.IsFinal || active.IsFinal,
                };
            }
        }
    }

    private void SignalOutputOverflow(string message)
    {
        Interlocked.Increment(ref _outputOverflowCount);
        Fail(new InvalidDataException(message));
    }

    private bool TryReserveOutputBudget(int samples)
    {
        while (true)
        {
            long current = Interlocked.Read(ref _unplayedRealtimeSamples);
            long updated = current + samples;
            if (updated > OutputMaxRealtimeSamples)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _unplayedRealtimeSamples, updated, current) == current)
            {
                return true;
            }
        }
    }

    private void ReleaseOutputBudget(long samples)
    {
        if (samples <= 0)
        {
            return;
        }

        long updated = Interlocked.Add(ref _unplayedRealtimeSamples, -samples);
        if (updated < 0)
        {
            Interlocked.Exchange(ref _unplayedRealtimeSamples, 0);
        }
    }

    internal void CancelSessionSafely()
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
            _logger.LogWarning(exception, "Failed to request Realtime cancellation after a media failure.");
        }
    }

    private void DetachSession()
    {
        IRealtimeAgentSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is not null)
        {
            session.MediaUpdate -= HandleMediaUpdate;
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
        _commands.Writer.TryComplete();
        _workerCancellation.Cancel();
    }

    private void Fail(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        _logger.LogError(exception, "The Realtime-to-caller audio pipeline failed.");
        _onFault(exception);
        RequestStop();
        RaiseErrorSafely("The outgoing audio pipeline failed.");
    }

    private void RaiseErrorSafely(string message)
    {
        SourceErrorDelegate? handlers = OnAudioSourceError;
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
                _logger.LogWarning(exception, "An audio-source error subscriber threw.");
            }
        }
    }

    private AudioFormat GetSelectedSourceFormat()
    {
        lock (_gate)
        {
            return _formats.SelectedFormat;
        }
    }

    private IRealtimeAgentSession GetSession()
    {
        lock (_gate)
        {
            return _session ?? throw new InvalidOperationException("No Realtime session is attached.");
        }
    }

    private void EnsureSessionAttached()
    {
        if (_session is null)
        {
            throw new InvalidOperationException("A Realtime session must be attached before media starts.");
        }
    }

    private long GetEpoch()
    {
        lock (_gate)
        {
            return _epoch;
        }
    }

    private void ClearActive(ref ActiveOutput? active)
    {
        if (active is null)
        {
            return;
        }

        ReleaseOutputBudget(active.ReservedRealtimeSamples);
        active.ReservedRealtimeSamples = 0;
        ClearPlaybackCursor(active);
        active.Dispose();
        active = null;
    }

    private void ClearPlaybackCursor(ActiveOutput active)
    {
        lock (_gate)
        {
            if (_playbackCursor is not null &&
                _playbackCursor.Identity == active.Identity &&
                _playbackCursor.Epoch == active.Epoch)
            {
                _playbackCursor = null;
            }
        }
    }

    private abstract record OutputCommand(long Epoch);

    private sealed record AudioDelta(
        RealtimeOutputIdentity Identity,
        byte[] Pcm16At24Khz,
        long Epoch) : OutputCommand(Epoch);

    private sealed record AudioFinished(
        RealtimeOutputIdentity Identity,
        long Epoch) : OutputCommand(Epoch);

    private sealed record ResetOutput(long Epoch) : OutputCommand(Epoch);

    private sealed record InterruptOutput(
        PlaybackCursor Cursor,
        long Epoch) : OutputCommand(Epoch);

    private sealed record PlaybackCursor(
        RealtimeOutputIdentity Identity,
        long Epoch,
        long RealSipSamplesEmitted,
        bool IsFinal)
    {
        public string ItemId => Identity.ItemId;

        public int ContentIndex => Identity.ContentIndex;
    }

    private sealed class ActiveOutput : IDisposable
    {
        public RealtimeOutputIdentity Identity { get; }
        public long Epoch { get; }
        public AudioResampler Resampler { get; }
        public CancellationTokenSource PacingCancellation { get; }
        public AudioStreamBuffer PendingSipAudio { get; } =
            AudioStreamBuffer.CreateForDuration(
                new APcmFormat(ASampleValueFormat.S16, SipSampleRate, 1),
                TimeSpan.FromSeconds(30));
        public long TotalRealtimeSamplesReceived { get; set; }
        public long TotalSipSamplesProduced { get; set; }
        public long TotalRealSipSamplesEmitted { get; set; }
        public long ReservedRealtimeSamples { get; set; }
        public int RealtimeSamplesWithoutOutput { get; set; }
        public bool IsFinal { get; set; }
        public bool PlaybackStarted { get; set; }
        public long NextPacketTimestamp { get; set; }
        public Task? PacketDeadlineTask { get; set; }

        public ActiveOutput(
            RealtimeOutputIdentity identity,
            long epoch,
            AudioResampler resampler,
            CancellationToken cancellationToken)
        {
            Identity = identity;
            Epoch = epoch;
            Resampler = resampler;
            PacingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public void Dispose()
        {
            PacingCancellation.Cancel();
            PacingCancellation.Dispose();
            Resampler.Dispose();
            PendingSipAudio.Dispose();
        }
    }
}
