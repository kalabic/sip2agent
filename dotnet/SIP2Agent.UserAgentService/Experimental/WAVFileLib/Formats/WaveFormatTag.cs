namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;

/// <summary>
/// Known WAVE <c>wFormatTag</c> values from the project format documentation.
/// Unknown tags remain valid as raw <see cref="ushort"/> on <see cref="WaveFormat"/>.
/// </summary>
[Experimental("S2AWAV001")]
internal static class WaveFormatTag
{
    public const ushort Unknown = 0x0000;
    public const ushort Pcm = 0x0001;
    public const ushort Adpcm = 0x0002;
    public const ushort IeeeFloat = 0x0003;
    public const ushort ALaw = 0x0006;
    public const ushort MuLaw = 0x0007;
    public const ushort Drm = 0x0009;
    public const ushort WmaVoice9 = 0x000A;
    public const ushort OkiAdpcm = 0x0010;
    public const ushort DviAdpcm = 0x0011; // Intel DVI / IMA ADPCM
    public const ushort MediaSpaceAdpcm = 0x0012;
    public const ushort SierraAdpcm = 0x0013;
    public const ushort DialogicOkiAdpcm = 0x0017;
    public const ushort MediaVisionAdpcm = 0x0018;
    public const ushort YamahaAdpcm = 0x0020;
    public const ushort TrueSpeech = 0x0022;
    public const ushort DolbyAc2 = 0x0030;
    public const ushort Gsm610 = 0x0031;
    public const ushort MsnAudio = 0x0032;
    public const ushort RockwellAdpcm = 0x003B;
    public const ushort AntexG721 = 0x0040;
    public const ushort G728Celp = 0x0041;
    public const ushort Msg723 = 0x0042;
    /// <summary>ITU / FFmpeg G.726 ADPCM (common in ffmpeg-written WAV).</summary>
    public const ushort G726 = 0x0045;
    public const ushort Mpeg = 0x0050;
    public const ushort MpegLayer3 = 0x0055; // MP3
    public const ushort LucentG723 = 0x0059;
    public const ushort Voxware = 0x0062;
    /// <summary>Microsoft-registered G.726 ADPCM tag.</summary>
    public const ushort G726Adpcm = 0x0064;
    /// <summary>Microsoft-registered G.722 ADPCM tag.</summary>
    public const ushort G722Adpcm = 0x0065;
    /// <summary>ITU G.722 / FFmpeg <c>WAVE_FORMAT_G722</c> (common in ffmpeg-written WAV).</summary>
    public const ushort G722 = 0x028F;
    public const ushort DolbyAc3Spdif = 0x0092;
    public const ushort RawAac = 0x00FF;
    public const ushort Wma1 = 0x0160;
    public const ushort Wma2 = 0x0161;
    public const ushort WmaPro = 0x0162;
    public const ushort WmaLossless = 0x0163;
    public const ushort MpegAdtsAac = 0x1600;
    public const ushort MpegLoas = 0x1602;
    public const ushort MpegHeAac = 0x1610;
    public const ushort CreativeAdpcm = 0x0200;
    public const ushort Alac = 0x6C61;
    public const ushort Opus = 0x704F;
    public const ushort AmrNb = 0x7361;
    public const ushort AmrNbAlt1 = 0x7A21;
    public const ushort AmrNbAlt2 = 0x7A22;
    public const ushort Speex = 0xA109;
    public const ushort PolycomG722 = 0xA112;
    public const ushort Extensible = 0xFFFE;
}
