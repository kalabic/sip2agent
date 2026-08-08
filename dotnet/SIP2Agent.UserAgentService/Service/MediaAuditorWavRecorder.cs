using AudioFormatLib;
using AudioFormatLib.Utils;
using Microsoft.Extensions.Logging;
using SIP2Agent.UserAgentService.Experimental.WAVFileLib;

namespace SIP2Agent.UserAgentService.Service;

/// <summary>
/// Converts independently paced mono PCM streams into a sample-aligned stereo WAV.
/// TX is the left channel and RX is the right channel.
/// </summary>
internal sealed class MediaAuditorWavRecorder : IDisposable
{
    internal const int OutputSampleRate = 24_000;
    internal const int SkewAllowanceSamples = OutputSampleRate / 50;
    internal const int BytesPerStereoFrame = sizeof(short) * 2;
    internal const long MaxClassicPcmDataBytes =
        ((uint.MaxValue - 36L) / BytesPerStereoFrame) * BytesPerStereoFrame;

    private const int MaximumWriteFrames = 4_096;
    // AudioFormatLib 1.9 requires final residual capacity to be supplied by the caller.
    private const int FinalResamplerDrainCapacitySamples = 4_096;

    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly long _maximumDataBytes;
    private readonly DirectionState _transmit = new();
    private readonly DirectionState _receive = new();

    private WavWriter? _writer;
    private long _framesWritten;
    private int _faulted;
    private bool _completed;

    internal MediaAuditorWavRecorder(string path, ILogger logger)
        : this(OpenRecordingStream(path), path, logger, MaxClassicPcmDataBytes)
    {
    }

    internal MediaAuditorWavRecorder(
        Stream stream,
        string path,
        ILogger logger,
        long maximumDataBytes = MaxClassicPcmDataBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(logger);
        if (maximumDataBytes < 0 || maximumDataBytes % BytesPerStereoFrame != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDataBytes),
                "The WAV data limit must be a non-negative number of complete stereo PCM16 frames.");
        }

        _path = path;
        _logger = logger;
        _maximumDataBytes = Math.Min(maximumDataBytes, MaxClassicPcmDataBytes);
        _writer = WavWriter.Create(
            stream,
            WaveFormat.Pcm(channels: 2, sampleRate: OutputSampleRate, bitsPerSample: 16),
            new WaveWriteOptions { WriteIsftInfo = false },
            leaveOpen: false);
    }

    internal string Path => _path;

    internal bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    internal long FramesWritten
    {
        get
        {
            lock (_gate)
            {
                return _framesWritten;
            }
        }
    }

    internal void RecordTransmit(int sampleRate, ReadOnlySpan<short> samples)
        => Record(_transmit, sampleRate, samples);

    internal void RecordReceive(int sampleRate, ReadOnlySpan<short> samples)
        => Record(_receive, sampleRate, samples);

    internal void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (IsFaulted)
            {
                DisposeWriterUnderLock();
                return;
            }

            try
            {
                _transmit.Complete();
                _receive.Complete();
                DrainUnderLock(final: true);
                DisposeWriterUnderLock();
            }
            catch (Exception exception)
            {
                FaultUnderLock(exception);
            }
            finally
            {
                _transmit.Dispose();
                _receive.Dispose();
            }
        }
    }

    public void Dispose() => Complete();

    private void Record(DirectionState direction, int sampleRate, ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            if (_completed || IsFaulted)
            {
                return;
            }

            try
            {
                direction.Append(sampleRate, samples);
                DrainUnderLock(final: false);
            }
            catch (Exception exception)
            {
                FaultUnderLock(exception);
            }
        }
    }

    private void DrainUnderLock(bool final)
    {
        while (true)
        {
            int paired = Math.Min(_transmit.Pending.Count, _receive.Pending.Count);
            if (paired > 0)
            {
                WriteFramesUnderLock(
                    Math.Min(paired, MaximumWriteFrames),
                    transmitSilence: false,
                    receiveSilence: false);
                continue;
            }

            int allowance = final ? 0 : SkewAllowanceSamples;
            int transmitExcess = _transmit.Pending.Count - allowance;
            if (transmitExcess > 0)
            {
                WriteFramesUnderLock(
                    Math.Min(transmitExcess, MaximumWriteFrames),
                    transmitSilence: false,
                    receiveSilence: true);
                continue;
            }

            int receiveExcess = _receive.Pending.Count - allowance;
            if (receiveExcess > 0)
            {
                WriteFramesUnderLock(
                    Math.Min(receiveExcess, MaximumWriteFrames),
                    transmitSilence: true,
                    receiveSilence: false);
                continue;
            }

            return;
        }
    }

    private void WriteFramesUnderLock(
        int frameCount,
        bool transmitSilence,
        bool receiveSilence)
    {
        WavWriter writer = _writer
            ?? throw new InvalidOperationException("The WAV recorder has no active writer.");
        int byteCount = checked(frameCount * BytesPerStereoFrame);
        if (writer.DataBytesWritten > _maximumDataBytes - byteCount)
        {
            throw new WavException(
                $"The recording reached its classic RIFF data limit of {_maximumDataBytes} bytes.");
        }

        short[] transmit = transmitSilence
            ? new short[frameCount]
            : Dequeue(_transmit.Pending, frameCount);
        short[] receive = receiveSilence
            ? new short[frameCount]
            : Dequeue(_receive.Pending, frameCount);
        short[] stereo = Multiplex(transmit, receive);
        writer.WritePcmFrames(S16LittleEndian.Encode(stereo));
        _framesWritten += frameCount;
    }

    private static short[] Dequeue(Queue<short> source, int count)
    {
        var result = new short[count];
        for (int index = 0; index < result.Length; index++)
        {
            result[index] = source.Dequeue();
        }

        return result;
    }

    private static unsafe short[] Multiplex(
        ReadOnlySpan<short> transmit,
        ReadOnlySpan<short> receive)
    {
        if (transmit.Length != receive.Length)
        {
            throw new ArgumentException("TX and RX must contain the same number of PCM frames.");
        }

        var stereo = new short[checked(transmit.Length * 2)];
        var monoFormat = new ASampleFormat(
            AValueFormat.S16,
            OutputSampleRate,
            channelCount: 1,
            byteOrder: AByteOrder.LittleEndian);
        var stereoFormat = new ASampleFormat(
            AValueFormat.S16,
            OutputSampleRate,
            channelCount: 2,
            planar: false,
            byteOrder: AByteOrder.LittleEndian);

        fixed (short* transmitPointer = transmit)
        fixed (short* receivePointer = receive)
        fixed (short* stereoPointer = stereo)
        {
            var transmitSpan = new AudioSpan(
                (byte*)transmitPointer,
                offset: 0,
                transmit.Length * sizeof(short),
                monoFormat);
            var receiveSpan = new AudioSpan(
                (byte*)receivePointer,
                offset: 0,
                receive.Length * sizeof(short),
                monoFormat);
            var stereoSpan = new AudioSpan(
                (byte*)stereoPointer,
                offset: 0,
                stereo.Length * sizeof(short),
                stereoFormat);
            ATools.ConvertToStereo(transmitSpan, receiveSpan, stereoSpan);
        }

        return stereo;
    }

    private void FaultUnderLock(Exception exception)
    {
        if (Interlocked.Exchange(ref _faulted, 1) == 0)
        {
            _logger.LogWarning(
                exception,
                "Media auditor disabled WAV recording for {RecordingPath}; the call will continue.",
                _path);
        }

        _transmit.Dispose();
        _receive.Dispose();
        DisposeWriterUnderLock();
    }

    private void DisposeWriterUnderLock()
    {
        WavWriter? writer = _writer;
        _writer = null;
        if (writer is null)
        {
            return;
        }

        try
        {
            writer.Dispose();
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref _faulted, 1) == 0)
            {
                _logger.LogWarning(
                    exception,
                    "Media auditor could not finalize WAV recording {RecordingPath}.",
                    _path);
            }
        }
    }

    private static FileStream OpenRecordingStream(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
    }

    private sealed class DirectionState : IDisposable
    {
        private AudioResampler? _resampler;
        private int _sourceSampleRate;
        private long _segmentInputSamples;
        private long _segmentOutputSamples;

        internal Queue<short> Pending { get; } = new();

        internal void Append(int sampleRate, ReadOnlySpan<short> samples)
        {
            if (sampleRate <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            if (_sourceSampleRate != 0 && _sourceSampleRate != sampleRate)
            {
                CompleteSegment();
            }

            if (_sourceSampleRate == 0)
            {
                _sourceSampleRate = sampleRate;
                if (sampleRate != OutputSampleRate)
                {
                    _resampler = AudioResamplerChecked.CreateS16(
                        sampleRate,
                        OutputSampleRate);
                }
            }

            _segmentInputSamples += samples.Length;
            if (_resampler is null)
            {
                Enqueue(samples);
                _segmentOutputSamples += samples.Length;
            }
            else
            {
                short[] converted = AudioResamplerChecked.Process(
                    _resampler,
                    samples.ToArray(),
                    endOfInput: false);
                Enqueue(converted);
                _segmentOutputSamples += converted.Length;
            }
        }

        internal void Complete() => CompleteSegment();

        public void Dispose()
        {
            _resampler?.Dispose();
            _resampler = null;
            _sourceSampleRate = 0;
            _segmentInputSamples = 0;
            _segmentOutputSamples = 0;
            Pending.Clear();
        }

        private void CompleteSegment()
        {
            if (_sourceSampleRate == 0)
            {
                return;
            }

            long expected = checked(
                (_segmentInputSamples * OutputSampleRate + _sourceSampleRate - 1) /
                _sourceSampleRate);
            if (_resampler is not null)
            {
                AudioPacket emptyInput = new(
                    _resampler.InputFormat,
                    sampleCapacity: 0);
                AudioPacket flushOutput = new(
                    _resampler.OutputFormat,
                    FinalResamplerDrainCapacitySamples);
                _ = AudioResamplerChecked.Process(
                    _resampler,
                    emptyInput,
                    ref flushOutput,
                    endOfInput: true);
                short[] flushed = flushOutput.AsValues<short>().Values.ToArray();
                long required = expected - _segmentOutputSamples;
                if (required < 0)
                {
                    throw new InvalidDataException(
                        "The recording resampler produced more samples than expected before final flush.");
                }

                int use = checked((int)Math.Min(required, flushed.LongLength));
                Enqueue(flushed.AsSpan(0, use));
                _segmentOutputSamples += use;
            }

            long missing = expected - _segmentOutputSamples;
            if (missing < 0)
            {
                throw new InvalidDataException(
                    "The recording resampler produced more samples than expected.");
            }
            if (missing > int.MaxValue)
            {
                throw new InvalidDataException(
                    "The recording resampler final flush was unexpectedly short.");
            }

            for (int index = 0; index < (int)missing; index++)
            {
                Pending.Enqueue(0);
            }

            _resampler?.Dispose();
            _resampler = null;
            _sourceSampleRate = 0;
            _segmentInputSamples = 0;
            _segmentOutputSamples = 0;
        }

        private void Enqueue(ReadOnlySpan<short> samples)
        {
            foreach (short sample in samples)
            {
                Pending.Enqueue(sample);
            }
        }
    }
}
