namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Options for <see cref="WavWriter"/>. Packet sizes control logical framing of the write API,
/// not a second RIFF layer.
/// </summary>
[Experimental("S2AWAV001")]
internal sealed class WaveWriteOptions
{
    /// <summary>
    /// Preferred packet size for framed writes; 0 means unbounded continuous append.
    /// When set, <see cref="WavWriter.WritePacket"/> payloads larger than this are rejected.
    /// </summary>
    public int PreferredPacketSize { get; init; }

    /// <summary>
    /// When set, every packet must be exactly this many bytes (often <c>nBlockAlign</c>).
    /// </summary>
    public int? FixedPacketSize { get; init; }

    /// <summary>
    /// When true, every <see cref="WavWriter.WritePacket"/> call must include a timestamp.
    /// </summary>
    public bool RequireTimestamps { get; init; }

    /// <summary>
    /// When true, reserve a <c>fact</c> chunk at create time (sample count may be a placeholder).
    /// Set the final count via <see cref="FactSampleLength"/> at create, and/or
    /// <see cref="WavWriter.SetFactSampleLength"/> before dispose; value is patched on finalize.
    /// Also reserved automatically when <see cref="UseWaveList"/> is true (classic WAVE recommends
    /// <c>fact</c> for wave lists) unless you only care about raw bytes.
    /// </summary>
    public bool WriteFactChunk { get; init; }

    /// <summary>
    /// Write a LIST/INFO/ISFT chunk identifying this library.
    /// </summary>
    public bool WriteIsftInfo { get; init; } = true;

    /// <summary>
    /// Force <c>WAVE_FORMAT_EXTENSIBLE</c> even when a classic tag would suffice.
    /// </summary>
    public bool UseExtensibleFmt { get; init; }

    /// <summary>
    /// When true, audio is written as a classic WAVE wave list:
    /// <c>LIST</c> form type <c>wavl</c> with ordered <c>data</c> / <c>slnt</c> segments
    /// via <see cref="WavWriter.WriteSoundSegment"/> and <see cref="WavWriter.WriteSilenceSegment"/>.
    /// Poorly supported by many players — prefer classic single <c>data</c> for interchange.
    /// </summary>
    public bool UseWaveList { get; init; }

    /// <summary>
    /// Initial sample-frame count for the <c>fact</c> chunk (optional).
    /// May be updated later with <see cref="WavWriter.SetFactSampleLength"/> before dispose.
    /// If still unset at finalize, the writer may auto-fill for PCM/float + known silence
    /// when a <c>fact</c> was reserved; otherwise the placeholder remains 0.
    /// </summary>
    public uint? FactSampleLength { get; init; }
}
