namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;

/// <summary>
/// Named SubFormat GUIDs for <see cref="WaveFormatTag.Extensible"/> and the classic
/// Microsoft <c>DEFINE_WAVEFORMATEX_GUID</c> mapping rule.
/// </summary>
[Experimental("S2AWAV001")]
internal static class WaveSubFormatGuids
{
    /// <summary>
    /// Bytes after Data1 (4 bytes): Data2, Data3, Data4 for the classic WAVEFORMATEX GUID template.
    /// </summary>
    private static readonly byte[] WaveFormatExGuidTail =
    [
        0x00, 0x00, // Data2
        0x10, 0x00, // Data3
        0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 // Data4
    ];

    public static readonly Guid WaveFormatEx = FromTag(0x0000);
    public static readonly Guid Pcm = FromTag(WaveFormatTag.Pcm);
    public static readonly Guid Adpcm = FromTag(WaveFormatTag.Adpcm);
    public static readonly Guid IeeeFloat = FromTag(WaveFormatTag.IeeeFloat);
    public static readonly Guid ALaw = FromTag(WaveFormatTag.ALaw);
    public static readonly Guid MuLaw = FromTag(WaveFormatTag.MuLaw);
    public static readonly Guid Drm = FromTag(WaveFormatTag.Drm);
    public static readonly Guid Mpeg = FromTag(WaveFormatTag.Mpeg);
    public static readonly Guid DolbyDigital = FromTag(WaveFormatTag.DolbyAc3Spdif);

    // IEC 61937 / HDMI / S/PDIF (from docs; some use 0cea Data2)
    public static readonly Guid Iec61937DolbyDigital = new("00000092-0000-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937Dts = new("00000008-0000-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937DolbyDigitalPlus = new("0000000a-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937DtsHd = new("0000000b-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937DolbyMlp = new("0000000c-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937Aac = new("00000006-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937Mpeg1 = new("00000003-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937Mpeg2 = new("00000004-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937Mpeg3 = new("00000005-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937WmaPro = new("00000164-0000-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937Atrac = new("00000008-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937OneBitAudio = new("00000009-0cea-0010-8000-00aa00389b71");
    public static readonly Guid Iec61937Dst = new("0000000d-0cea-0010-8000-00aa00389b71");

    public static readonly Guid Opus = FromTag(WaveFormatTag.Opus);
    public static readonly Guid Alac = FromTag(WaveFormatTag.Alac);
    public static readonly Guid Speex = FromTag(WaveFormatTag.Speex);
    public static readonly Guid MpegLayer3 = FromTag(WaveFormatTag.MpegLayer3);
    public static readonly Guid Gsm610 = FromTag(WaveFormatTag.Gsm610);
    public static readonly Guid DviAdpcm = FromTag(WaveFormatTag.DviAdpcm);
    public static readonly Guid G722Adpcm = FromTag(WaveFormatTag.G722Adpcm);
    public static readonly Guid G726Adpcm = FromTag(WaveFormatTag.G726Adpcm);
    public static readonly Guid G722 = FromTag(WaveFormatTag.G722);
    public static readonly Guid G726 = FromTag(WaveFormatTag.G726);

    /// <summary>
    /// Builds a SubFormat GUID from a traditional 16-bit format tag using the Microsoft rule:
    /// <c>{ (USHORT)tag, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71 }</c>.
    /// </summary>
    public static Guid FromTag(ushort formatTag)
    {
        // Windows GUID layout: Data1 (4 LE) | Data2 (2) | Data3 (2) | Data4 (8)
        // Data1 = zero-extended format tag.
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = (byte)formatTag;
        bytes[1] = (byte)(formatTag >> 8);
        bytes[2] = 0;
        bytes[3] = 0;
        WaveFormatExGuidTail.CopyTo(bytes[4..]);
        return new Guid(bytes);
    }

    /// <summary>
    /// If <paramref name="guid"/> matches the classic WAVEFORMATEX template, returns the embedded tag.
    /// </summary>
    public static bool TryGetTag(Guid guid, out ushort formatTag)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!guid.TryWriteBytes(bytes))
        {
            formatTag = 0;
            return false;
        }

        // Data1 high word must be zero for classic tag embedding.
        if (bytes[2] != 0 || bytes[3] != 0)
        {
            formatTag = 0;
            return false;
        }

        for (int i = 0; i < WaveFormatExGuidTail.Length; i++)
        {
            if (bytes[i + 4] != WaveFormatExGuidTail[i])
            {
                formatTag = 0;
                return false;
            }
        }

        formatTag = (ushort)(bytes[0] | (bytes[1] << 8));
        return true;
    }
}
