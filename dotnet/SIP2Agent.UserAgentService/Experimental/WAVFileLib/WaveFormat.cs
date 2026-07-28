using SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;

namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Full WAVE format descriptor corresponding to the <c>fmt </c> chunk (classic and extensible).
/// </summary>
[Experimental("S2AWAV001")]
internal readonly partial record struct WaveFormat
{
    public ushort FormatTag { get; init; }
    public ushort Channels { get; init; }
    public uint SampleRate { get; init; }
    public uint AverageBytesPerSecond { get; init; }
    public ushort BlockAlign { get; init; }
    public ushort BitsPerSample { get; init; }

    /// <summary>Extensible: valid bits per sample; 0 when not applicable.</summary>
    public ushort ValidBitsPerSample { get; init; }

    /// <summary>Extensible channel mask; 0 when not applicable.</summary>
    public uint ChannelMask { get; init; }

    /// <summary>Extensible SubFormat GUID; <see cref="Guid.Empty"/> when not extensible.</summary>
    public Guid SubFormat { get; init; }

    /// <summary>
    /// Opaque <c>fmt </c> extension bytes after the 16-byte core (includes cbSize payload as stored),
    /// or empty when the format is classic 16-byte only. For extensible, this is the raw extension
    /// after the core (typically starting with cbSize). Prefer structured fields when present.
    /// </summary>
    public byte[] ExtraBytes { get; init; } = [];

    public WaveFormat()
    {
        FormatTag = WaveFormatTag.Pcm;
        Channels = 2;
        SampleRate = 44100;
        BitsPerSample = 16;
        BlockAlign = 4;
        AverageBytesPerSecond = 44100 * 4;
        ValidBitsPerSample = 0;
        ChannelMask = 0;
        SubFormat = Guid.Empty;
        ExtraBytes = [];
    }

    /// <summary>Creates a classic PCM format. Prefer <see cref="Pcm"/>.</summary>
    public static WaveFormat CreatePcm(ushort channels, uint sampleRate, ushort bitsPerSample)
        => Pcm(channels, sampleRate, bitsPerSample);

    /// <summary>Creates a classic IEEE float format. Prefer <see cref="IeeeFloat"/>.</summary>
    public static WaveFormat CreateIeeeFloat(ushort channels, uint sampleRate, ushort bitsPerSample = 32)
        => IeeeFloat(channels, sampleRate, bitsPerSample);

    public Guid EffectiveSubFormat
    {
        get
        {
            if (FormatTag == WaveFormatTag.Extensible)
            {
                return SubFormat;
            }

            return WaveSubFormatGuids.FromTag(FormatTag);
        }
    }

    public WavePayloadKind PayloadKind
    {
        get
        {
            var effectiveTag = FormatTag;
            if (FormatTag == WaveFormatTag.Extensible)
            {
                if (SubFormat == WaveSubFormatGuids.Pcm ||
                    (WaveSubFormatGuids.TryGetTag(SubFormat, out var t) && t == WaveFormatTag.Pcm))
                {
                    return WavePayloadKind.Pcm;
                }

                if (SubFormat == WaveSubFormatGuids.IeeeFloat ||
                    (WaveSubFormatGuids.TryGetTag(SubFormat, out t) && t == WaveFormatTag.IeeeFloat))
                {
                    return WavePayloadKind.IeeeFloat;
                }

                if (WaveFormatCatalog.TryGet(SubFormat, out var entry))
                {
                    return entry.DefaultPayloadKind;
                }

                return WavePayloadKind.Raw;
            }

            return effectiveTag switch
            {
                WaveFormatTag.Pcm => WavePayloadKind.Pcm,
                WaveFormatTag.IeeeFloat => WavePayloadKind.IeeeFloat,
                _ => WaveFormatCatalog.TryGet(effectiveTag, out var e)
                    ? e.DefaultPayloadKind
                    : WavePayloadKind.Raw,
            };
        }
    }

    public override string ToString()
    {
        var name = WaveFormatCatalog.GetName(this);
        if (FormatTag == WaveFormatTag.Extensible && SubFormat != Guid.Empty)
        {
            return $"{name} subtype={SubFormat} {SampleRate} Hz, {Channels} ch, {BitsPerSample} bit, block={BlockAlign}";
        }

        return $"{name} (0x{FormatTag:X4}) {SampleRate} Hz, {Channels} ch, {BitsPerSample} bit, block={BlockAlign}";
    }
}
