using System.Buffers.Binary;
using System.Text;
using SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;
using SIP2Agent.UserAgentService.Experimental.WAVFileLib.Internal;

namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Writes a RIFF/WAVE container without encoding audio codecs.
/// Requires a seekable stream so RIFF sizes can be patched on finalize.
/// <para>
/// <b>Shell:</b> <see cref="Create(string, WaveFormat, WaveWriteOptions?)"/> writes format once;
/// <see cref="Write"/>, <see cref="WritePacket"/>, <see cref="WritePcmFrames"/>, <see cref="WriteFloatFrames"/>
/// append into one <c>data</c> chunk; <see cref="Complete"/> or <see cref="Dispose"/> finalizes sizes.
/// </para>
/// Opt-in advanced: <see cref="WaveWriteOptions.UseWaveList"/> for <c>LIST</c>/<c>wavl</c> multi-segment.
/// </summary>
[Experimental("S2AWAV001")]
internal sealed partial class WavWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly WaveFormat _format;
    private readonly WaveWriteOptions _options;
    private readonly PacketIndex _packetIndex = new();
    private readonly long _riffSizePosition;

    // Classic single-data mode
    private readonly long _dataSizePosition;
    private long _dataBytesWritten;

    // Wave-list mode
    private readonly long _listSizePosition;
    private MemoryStream? _openSoundBuffer;
    private int _waveListSegmentCount;
    private uint _silenceSampleFramesTotal;

    // fact chunk: reserved at create, patched on finalize
    private readonly long _factSampleOffset; // -1 if not reserved
    private uint? _factSampleLength;

    private bool _finalized;
    private bool _disposed;

    private WavWriter(Stream stream, bool leaveOpen, WaveFormat format, WaveWriteOptions options)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
        _format = format;
        _options = options;
        _factSampleLength = options.FactSampleLength;
        _factSampleOffset = -1;

        Span<byte> header = stackalloc byte[12];
        RiffIds.WriteId(header, RiffIds.Riff);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 0);
        RiffIds.WriteId(header[8..], RiffIds.Wave);
        _stream.Write(header);
        _riffSizePosition = 4;

        byte[] fmtPayload = FmtChunkWriter.BuildPayload(format, options.UseExtensibleFmt);
        WriteChunk(RiffIds.Fmt, fmtPayload);

        // Reserve fact when app asks, when initial length is known, or for wave lists (recommended).
        bool reserveFact = options.WriteFactChunk
                           || options.FactSampleLength is not null
                           || options.UseWaveList;
        if (reserveFact)
        {
            uint initial = options.FactSampleLength ?? 0;
            Span<byte> factHeader = stackalloc byte[8];
            RiffIds.WriteId(factHeader, RiffIds.Fact);
            BinaryPrimitives.WriteUInt32LittleEndian(factHeader[4..], 4);
            _stream.Write(factHeader);
            _factSampleOffset = _stream.Position;
            Span<byte> factPayload = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(factPayload, initial);
            _stream.Write(factPayload);
        }

        if (options.UseWaveList)
        {
            // LIST + size placeholder + form type wavl
            Span<byte> listHeader = stackalloc byte[12];
            RiffIds.WriteId(listHeader, RiffIds.List);
            BinaryPrimitives.WriteUInt32LittleEndian(listHeader[4..], 0);
            RiffIds.WriteId(listHeader[8..], RiffIds.Wavl);
            _stream.Write(listHeader);
            _listSizePosition = _stream.Position - 8; // size field
            _dataSizePosition = -1;
        }
        else
        {
            if (options.WriteIsftInfo)
            {
                WriteIsftChunk();
            }

            Span<byte> dataHeader = stackalloc byte[8];
            RiffIds.WriteId(dataHeader, RiffIds.Data);
            BinaryPrimitives.WriteUInt32LittleEndian(dataHeader[4..], 0);
            _stream.Write(dataHeader);
            _dataSizePosition = _stream.Position - 4;
            _listSizePosition = -1;
        }
    }

    public WaveFormat Format => _format;
    public bool IsWaveList => _options.UseWaveList;
    public long DataBytesWritten => _dataBytesWritten;
    public int WaveListSegmentCount => _waveListSegmentCount;

    /// <summary>True when a <c>fact</c> chunk was reserved and can be patched before dispose.</summary>
    public bool HasFactChunk => _factSampleOffset >= 0;

    /// <summary>Current sample-frame count that will be written to <c>fact</c> (if reserved).</summary>
    public uint? FactSampleLength => _factSampleLength;

    public IReadOnlyList<PacketIndexEntryView> PacketIndex =>
        _packetIndex.Entries.Select(e => new PacketIndexEntryView(e.ByteOffset, e.Length, e.Timestamp, e.Duration)).ToList();

    public static WavWriter Create(string path, WaveFormat format, WaveWriteOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var stream = File.Open(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            return Create(stream, format, options, leaveOpen: false);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static WavWriter Create(Stream stream, WaveFormat format, WaveWriteOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
        {
            throw new ArgumentException("Stream must be writable.", nameof(stream));
        }

        if (!stream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable so RIFF sizes can be finalized.", nameof(stream));
        }

        ValidateFormat(format);
        return new WavWriter(stream, leaveOpen, format, options ?? new WaveWriteOptions());
    }

    /// <summary>
    /// Appends raw bytes. Classic mode: single <c>data</c> chunk.
    /// Wave-list mode: requires an open sound segment.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFinalized();
        if (data.IsEmpty)
        {
            return;
        }

        if (_options.UseWaveList)
        {
            if (_openSoundBuffer is null)
            {
                throw new WavException(
                    "Wave-list mode: call BeginSoundSegment() or WriteSoundSegment() before Write().");
            }

            _openSoundBuffer.Write(data);
            _dataBytesWritten += data.Length;
            return;
        }

        _stream.Write(data);
        _dataBytesWritten += data.Length;
    }

    public void WritePcmFrames(ReadOnlySpan<byte> interleavedPcmFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_format.PayloadKind != WavePayloadKind.Pcm)
        {
            throw new WavException("WritePcmFrames requires a PCM format (tag or extensible PCM subtype).");
        }

        ValidateFrameBytes(interleavedPcmFrames);
        Write(interleavedPcmFrames);
    }

    public void WriteFloatFrames(ReadOnlySpan<byte> interleavedFloatFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_format.PayloadKind != WavePayloadKind.IeeeFloat)
        {
            throw new WavException("WriteFloatFrames requires an IEEE float format (tag or extensible float subtype).");
        }

        ValidateFrameBytes(interleavedFloatFrames);
        Write(interleavedFloatFrames);
    }

    public void WritePacket(ReadOnlySpan<byte> payload, WaveTime? timestamp = null, WaveTime? duration = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFinalized();

        if (_options.RequireTimestamps && timestamp is null)
        {
            throw new WavException("Write options require a timestamp for every packet.");
        }

        if (_options.FixedPacketSize is int fixedSize && payload.Length != fixedSize)
        {
            throw new WavException($"Packet length {payload.Length} does not match FixedPacketSize {fixedSize}.");
        }

        if (_options.PreferredPacketSize > 0 && payload.Length > _options.PreferredPacketSize)
        {
            throw new WavException(
                $"Packet length {payload.Length} exceeds PreferredPacketSize {_options.PreferredPacketSize}.");
        }

        long offset = _dataBytesWritten;
        Write(payload);
        _packetIndex.Add(offset, payload.Length, timestamp, duration);
    }

    /// <summary>Begins a new sound (<c>data</c>) segment inside a wave list.</summary>
    public void BeginSoundSegment()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFinalized();
        EnsureWaveList();
        if (_openSoundBuffer is not null)
        {
            throw new WavException("A sound segment is already open; call EndSoundSegment() first.");
        }

        _openSoundBuffer = new MemoryStream();
    }

    /// <summary>Closes the current sound segment and emits a <c>data</c> chunk in the wave list.</summary>
    public void EndSoundSegment()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFinalized();
        EnsureWaveList();
        if (_openSoundBuffer is null)
        {
            throw new WavException("No sound segment is open.");
        }

        byte[] payload = _openSoundBuffer.ToArray();
        _openSoundBuffer.Dispose();
        _openSoundBuffer = null;
        // Bytes were already counted in Write(); emit chunk without double-counting
        WriteChunk(RiffIds.Data, payload);
        _waveListSegmentCount++;
    }

    /// <summary>Writes one complete sound segment (<c>data</c>) into the wave list.</summary>
    public void WriteSoundSegment(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFinalized();
        EnsureWaveList();
        if (_openSoundBuffer is not null)
        {
            throw new WavException("A sound segment is already open; call EndSoundSegment() first.");
        }

        WriteChunk(RiffIds.Data, payload);
        _dataBytesWritten += payload.Length;
        _waveListSegmentCount++;
    }

    /// <summary>
    /// Writes a silence segment (<c>slnt</c>) with the given number of silent sample frames.
    /// </summary>
    public void WriteSilenceSegment(uint sampleFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFinalized();
        EnsureWaveList();
        if (_openSoundBuffer is not null)
        {
            throw new WavException("Close the open sound segment before writing silence.");
        }

        Span<byte> body = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(body, sampleFrames);
        WriteChunk(RiffIds.Silence, body);
        _waveListSegmentCount++;
        _silenceSampleFramesTotal += sampleFrames;
    }

    /// <summary>
    /// Sets the total sample-frame count for the reserved <c>fact</c> chunk.
    /// Call anytime before dispose when the total becomes known.
    /// Requires that a <c>fact</c> was reserved at create
    /// (<see cref="WaveWriteOptions.WriteFactChunk"/>, <see cref="WaveWriteOptions.FactSampleLength"/>,
    /// or <see cref="WaveWriteOptions.UseWaveList"/>).
    /// </summary>
    public void SetFactSampleLength(uint sampleFrames)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureNotFinalized();
        if (_factSampleOffset < 0)
        {
            throw new WavException(
                "No fact chunk was reserved. Create the writer with WriteFactChunk = true, " +
                "FactSampleLength set, or UseWaveList = true.");
        }

        _factSampleLength = sampleFrames;
    }

    /// <summary>
    /// Finalizes RIFF / <c>data</c> / <c>fact</c> / wave-list sizes without disposing the stream.
    /// Safe to call more than once; subsequent calls are no-ops.
    /// <see cref="Dispose"/> also finalizes, then closes the stream unless leaveOpen was set.
    /// </summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FinalizeFile();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            FinalizeFile();
        }
        finally
        {
            _openSoundBuffer?.Dispose();
            _disposed = true;
            if (!_leaveOpen)
            {
                _stream.Dispose();
            }
        }
    }

    private void FinalizeFile()
    {
        if (_finalized)
        {
            return;
        }

        _finalized = true;
        Span<byte> sizeBuf = stackalloc byte[4];

        if (_options.UseWaveList)
        {
            if (_openSoundBuffer is not null)
            {
                throw new WavException("Wave list still has an open sound segment; call EndSoundSegment() before dispose.");
            }

            if (_waveListSegmentCount == 0)
            {
                throw new WavException("Wave list contains no segments.");
            }

            // Close LIST/wavl before optional INFO (INFO must not be inside wavl size).
            long listEnd = _stream.Position;
            uint listSize = checked((uint)(listEnd - (_listSizePosition + 4)));
            _stream.Seek(_listSizePosition, SeekOrigin.Begin);
            BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, listSize);
            _stream.Write(sizeBuf);
            _stream.Seek(listEnd, SeekOrigin.Begin);

            if (_options.WriteIsftInfo)
            {
                WriteIsftChunk();
            }

            long end = _stream.Position;
            PatchFactSampleLength();
            uint riffSize = checked((uint)(end - 8));
            _stream.Seek(_riffSizePosition, SeekOrigin.Begin);
            BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, riffSize);
            _stream.Write(sizeBuf);
            _stream.Seek(end, SeekOrigin.Begin);
            _stream.Flush();
            return;
        }

        if ((_dataBytesWritten & 1) != 0)
        {
            _stream.WriteByte(0);
        }

        long classicEnd = _stream.Position;
        uint dataSize = checked((uint)_dataBytesWritten);
        uint classicRiffSize = checked((uint)(classicEnd - 8));

        _stream.Seek(_dataSizePosition, SeekOrigin.Begin);
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, dataSize);
        _stream.Write(sizeBuf);

        PatchFactSampleLength();

        _stream.Seek(_riffSizePosition, SeekOrigin.Begin);
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBuf, classicRiffSize);
        _stream.Write(sizeBuf);

        _stream.Seek(classicEnd, SeekOrigin.Begin);
        _stream.Flush();
    }

    private void PatchFactSampleLength()
    {
        if (_factSampleOffset < 0)
        {
            return;
        }

        uint value = _factSampleLength ?? TryComputeFactSampleLength() ?? 0;
        _factSampleLength = value;

        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        long pos = _stream.Position;
        _stream.Seek(_factSampleOffset, SeekOrigin.Begin);
        _stream.Write(buf);
        _stream.Seek(pos, SeekOrigin.Begin);
    }

    /// <summary>
    /// For PCM/float: soundBytes / BlockAlign + silence frames.
    /// Opaque codecs: null (app must call <see cref="SetFactSampleLength"/>).
    /// </summary>
    private uint? TryComputeFactSampleLength()
    {
        if (_format.PayloadKind is not (WavePayloadKind.Pcm or WavePayloadKind.IeeeFloat))
        {
            return null;
        }

        if (_format.BlockAlign == 0 || _dataBytesWritten % _format.BlockAlign != 0)
        {
            return null;
        }

        long soundFrames = _dataBytesWritten / _format.BlockAlign;
        long total = soundFrames + _silenceSampleFramesTotal;
        if (total > uint.MaxValue)
        {
            return null;
        }

        return (uint)total;
    }

    private void EnsureWaveList()
    {
        if (!_options.UseWaveList)
        {
            throw new WavException("Wave-list segment APIs require WaveWriteOptions.UseWaveList = true.");
        }
    }

    private void ValidateFrameBytes(ReadOnlySpan<byte> frames)
    {
        if (_format.BlockAlign == 0)
        {
            throw new WavException("BlockAlign is zero; cannot validate frame size.");
        }

        if (frames.Length % _format.BlockAlign != 0)
        {
            throw new WavException(
                $"Frame payload length {frames.Length} is not a multiple of BlockAlign {_format.BlockAlign}.");
        }
    }

    private void EnsureNotFinalized()
    {
        if (_finalized)
        {
            throw new ObjectDisposedException(nameof(WavWriter), "Writer has already been finalized.");
        }
    }

    private void WriteChunk(string id, ReadOnlySpan<byte> payload)
    {
        Span<byte> header = stackalloc byte[8];
        RiffIds.WriteId(header, id);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], (uint)payload.Length);
        _stream.Write(header);
        _stream.Write(payload);
        if ((payload.Length & 1) != 0)
        {
            _stream.WriteByte(0);
        }
    }

    private void WriteIsftChunk()
    {
        string software = "WAVFileLib-0.1.0";
        byte[] isftText = Encoding.ASCII.GetBytes(software);
        int listPayloadLen = 4 + 8 + isftText.Length + (isftText.Length & 1);
        var listPayload = new byte[listPayloadLen];
        RiffIds.WriteId(listPayload.AsSpan(0, 4), RiffIds.Info);
        RiffIds.WriteId(listPayload.AsSpan(4, 4), RiffIds.Isft);
        BinaryPrimitives.WriteUInt32LittleEndian(listPayload.AsSpan(8, 4), (uint)isftText.Length);
        isftText.CopyTo(listPayload.AsSpan(12));
        WriteChunk(RiffIds.List, listPayload);
    }

    private static void ValidateFormat(in WaveFormat format)
    {
        if (format.Channels == 0)
        {
            throw new WavException("WaveFormat.Channels must be non-zero.");
        }

        if (format.SampleRate == 0)
        {
            throw new WavException("WaveFormat.SampleRate must be non-zero.");
        }
    }
}

/// <summary>Public view of an in-memory packet index entry created by <see cref="WavWriter"/>.</summary>
[Experimental("S2AWAV001")]
internal readonly record struct PacketIndexEntryView(
    long ByteOffset,
    int Length,
    WaveTime? Timestamp,
    WaveTime? Duration);
