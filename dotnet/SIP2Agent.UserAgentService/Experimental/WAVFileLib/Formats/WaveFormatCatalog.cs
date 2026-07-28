namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;

/// <summary>
/// Friendly metadata for known format tags and SubFormat GUIDs (names only; no codecs).
/// </summary>
[Experimental("S2AWAV001")]
internal static class WaveFormatCatalog
{
    [Experimental("S2AWAV001")]
    public readonly record struct Entry(
        string Name,
        string Family,
        WavePayloadKind DefaultPayloadKind,
        ushort? FormatTag = null,
        Guid? SubFormat = null);

    private static readonly Dictionary<ushort, Entry> ByTag;
    private static readonly Dictionary<Guid, Entry> ByGuid;

    static WaveFormatCatalog()
    {
        var entries = new List<Entry>
        {
            E(WaveFormatTag.Unknown, "WAVE_FORMAT_UNKNOWN", "Core", WavePayloadKind.Raw),
            E(WaveFormatTag.Pcm, "WAVE_FORMAT_PCM", "Core", WavePayloadKind.Pcm),
            E(WaveFormatTag.Adpcm, "WAVE_FORMAT_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.IeeeFloat, "WAVE_FORMAT_IEEE_FLOAT", "Core", WavePayloadKind.IeeeFloat),
            E(WaveFormatTag.ALaw, "WAVE_FORMAT_ALAW", "Core", WavePayloadKind.Raw),
            E(WaveFormatTag.MuLaw, "WAVE_FORMAT_MULAW", "Core", WavePayloadKind.Raw),
            E(WaveFormatTag.Drm, "WAVE_FORMAT_DRM", "Microsoft", WavePayloadKind.Raw),
            E(WaveFormatTag.WmaVoice9, "WAVE_FORMAT_WMAVOICE9", "Microsoft", WavePayloadKind.Raw),
            E(WaveFormatTag.OkiAdpcm, "WAVE_FORMAT_OKI_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.DviAdpcm, "WAVE_FORMAT_DVI_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.MediaSpaceAdpcm, "WAVE_FORMAT_MEDIASPACE_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.SierraAdpcm, "WAVE_FORMAT_SIERRA_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.DialogicOkiAdpcm, "WAVE_FORMAT_DIALOGIC_OKI_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.MediaVisionAdpcm, "WAVE_FORMAT_MEDIAVISION_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.YamahaAdpcm, "WAVE_FORMAT_YAMAHA_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.TrueSpeech, "WAVE_FORMAT_TRUESPEECH", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.DolbyAc2, "WAVE_FORMAT_DOLBY_AC2", "Other", WavePayloadKind.Raw),
            E(WaveFormatTag.Gsm610, "WAVE_FORMAT_GSM610", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.MsnAudio, "WAVE_FORMAT_MSNAUDIO", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.RockwellAdpcm, "WAVE_FORMAT_ROCKWELL_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.AntexG721, "WAVE_FORMAT_ANTEX_ADPCME", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.G728Celp, "WAVE_FORMAT_G728_CELP", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.Msg723, "WAVE_FORMAT_MSG723", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.G726, "WAVE_FORMAT_G726", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.Mpeg, "WAVE_FORMAT_MPEG", "MPEG", WavePayloadKind.Raw),
            E(WaveFormatTag.MpegLayer3, "WAVE_FORMAT_MPEGLAYER3", "MPEG", WavePayloadKind.Raw),
            E(WaveFormatTag.LucentG723, "WAVE_FORMAT_LUCENT_G723", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.Voxware, "WAVE_FORMAT_VOXWARE", "Other", WavePayloadKind.Raw),
            E(WaveFormatTag.G726Adpcm, "WAVE_FORMAT_G726_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.G722Adpcm, "WAVE_FORMAT_G722_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.G722, "WAVE_FORMAT_G722", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.DolbyAc3Spdif, "WAVE_FORMAT_DOLBY_AC3_SPDIF", "IEC61937", WavePayloadKind.Raw),
            E(WaveFormatTag.RawAac, "WAVE_FORMAT_RAW_AAC1", "MPEG", WavePayloadKind.Raw),
            E(WaveFormatTag.Wma1, "WAVE_FORMAT_WMAUDIO1", "Microsoft", WavePayloadKind.Raw),
            E(WaveFormatTag.Wma2, "WAVE_FORMAT_WMAUDIO2", "Microsoft", WavePayloadKind.Raw),
            E(WaveFormatTag.WmaPro, "WAVE_FORMAT_WMAUDIO3", "Microsoft", WavePayloadKind.Raw),
            E(WaveFormatTag.WmaLossless, "WAVE_FORMAT_WMAUDIO_LOSSLESS", "Microsoft", WavePayloadKind.Raw),
            E(WaveFormatTag.MpegAdtsAac, "WAVE_FORMAT_MPEG_ADTS_AAC", "MPEG", WavePayloadKind.Raw),
            E(WaveFormatTag.MpegLoas, "WAVE_FORMAT_MPEG_LOAS", "MPEG", WavePayloadKind.Raw),
            E(WaveFormatTag.MpegHeAac, "WAVE_FORMAT_MPEG_HEAAC", "MPEG", WavePayloadKind.Raw),
            E(WaveFormatTag.CreativeAdpcm, "WAVE_FORMAT_CREATIVE_ADPCM", "ADPCM", WavePayloadKind.Raw),
            E(WaveFormatTag.Alac, "WAVE_FORMAT_ALAC", "Modern", WavePayloadKind.Raw),
            E(WaveFormatTag.Opus, "WAVE_FORMAT_OPUS", "Modern", WavePayloadKind.Raw),
            E(WaveFormatTag.AmrNb, "WAVE_FORMAT_AMR_NB", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.AmrNbAlt1, "WAVE_FORMAT_AMR_NB_ALT1", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.AmrNbAlt2, "WAVE_FORMAT_AMR_NB_ALT2", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.Speex, "WAVE_FORMAT_SPEEX", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.PolycomG722, "WAVE_FORMAT_POLYCOM_G722", "Telephony", WavePayloadKind.Raw),
            E(WaveFormatTag.Extensible, "WAVE_FORMAT_EXTENSIBLE", "Core", WavePayloadKind.Raw),
        };

        ByTag = new Dictionary<ushort, Entry>(entries.Count);
        ByGuid = new Dictionary<Guid, Entry>();

        foreach (var entry in entries)
        {
            if (entry.FormatTag is ushort tag)
            {
                ByTag[tag] = entry;
                if (tag != WaveFormatTag.Extensible)
                {
                    var guid = WaveSubFormatGuids.FromTag(tag);
                    ByGuid[guid] = entry with { SubFormat = guid };
                }
            }
        }

        // Named / IEC GUIDs that may not equal FromTag for the same meaning
        AddGuid(WaveSubFormatGuids.Iec61937DolbyDigital, "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937Dts, "KSDATAFORMAT_SUBTYPE_IEC61937_DTS", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937DolbyDigitalPlus, "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_DIGITAL_PLUS", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937DtsHd, "KSDATAFORMAT_SUBTYPE_IEC61937_DTS_HD", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937DolbyMlp, "KSDATAFORMAT_SUBTYPE_IEC61937_DOLBY_MLP", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937Aac, "KSDATAFORMAT_SUBTYPE_IEC61937_AAC", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937Mpeg1, "KSDATAFORMAT_SUBTYPE_IEC61937_MPEG1", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937Mpeg2, "KSDATAFORMAT_SUBTYPE_IEC61937_MPEG2", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937Mpeg3, "KSDATAFORMAT_SUBTYPE_IEC61937_MPEG3", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937WmaPro, "KSDATAFORMAT_SUBTYPE_IEC61937_WMA_PRO", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937Atrac, "KSDATAFORMAT_SUBTYPE_IEC61937_ATRAC", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937OneBitAudio, "KSDATAFORMAT_SUBTYPE_IEC61937_ONE_BIT_AUDIO", "IEC61937", WavePayloadKind.Raw);
        AddGuid(WaveSubFormatGuids.Iec61937Dst, "KSDATAFORMAT_SUBTYPE_IEC61937_DST", "IEC61937", WavePayloadKind.Raw);
    }

    private static Entry E(ushort tag, string name, string family, WavePayloadKind kind)
        => new(name, family, kind, tag, WaveSubFormatGuids.FromTag(tag));

    private static void AddGuid(Guid guid, string name, string family, WavePayloadKind kind)
    {
        ByGuid[guid] = new Entry(name, family, kind, SubFormat: guid);
    }

    public static bool TryGet(ushort formatTag, out Entry entry)
        => ByTag.TryGetValue(formatTag, out entry);

    public static bool TryGet(Guid subFormat, out Entry entry)
        => ByGuid.TryGetValue(subFormat, out entry);

    public static bool TryGet(in WaveFormat format, out Entry entry)
    {
        if (format.FormatTag == WaveFormatTag.Extensible)
        {
            if (format.SubFormat != Guid.Empty && TryGet(format.SubFormat, out entry))
            {
                return true;
            }

            if (WaveSubFormatGuids.TryGetTag(format.SubFormat, out var tag) && TryGet(tag, out entry))
            {
                return true;
            }
        }

        return TryGet(format.FormatTag, out entry);
    }

    public static string GetName(in WaveFormat format)
        => TryGet(format, out var entry) ? entry.Name : $"Unknown (0x{format.FormatTag:X4})";

    public static string GetName(ushort formatTag)
        => TryGet(formatTag, out var entry) ? entry.Name : $"Unknown (0x{formatTag:X4})";

    public static string GetName(Guid subFormat)
    {
        if (TryGet(subFormat, out var entry))
        {
            return entry.Name;
        }

        if (WaveSubFormatGuids.TryGetTag(subFormat, out var tag) && TryGet(tag, out entry))
        {
            return entry.Name;
        }

        return subFormat == Guid.Empty ? "Empty" : "Unknown";
    }

    public static string GetFamily(in WaveFormat format)
        => TryGet(format, out var entry) ? entry.Family : "Unknown";

    /// <summary>"0x028F (WAVE_FORMAT_G722)" style label for a format tag.</summary>
    public static string FormatTagLabel(ushort formatTag)
        => $"0x{formatTag:X4} ({GetName(formatTag)})";

    /// <summary>GUID with catalog name, e.g. "{…} (WAVE_FORMAT_PCM)".</summary>
    public static string SubFormatLabel(Guid subFormat)
        => $"{subFormat} ({GetName(subFormat)})";

    /// <summary>
    /// Human-oriented one-line description including aliases for common dual tags.
    /// </summary>
    public static string GetDisplayName(in WaveFormat format)
    {
        if (format.FormatTag == WaveFormatTag.Extensible)
        {
            string effective = GetName(format);
            return effective.StartsWith("Unknown", StringComparison.Ordinal)
                ? $"WAVE_FORMAT_EXTENSIBLE ({format.SubFormat})"
                : $"WAVE_FORMAT_EXTENSIBLE → {effective}";
        }

        string name = GetName(format);
        return format.FormatTag switch
        {
            WaveFormatTag.G722 => $"{name} (G.722, ITU/FFmpeg; MS alias 0x0065)",
            WaveFormatTag.G722Adpcm => $"{name} (G.722, Microsoft; ITU/FFmpeg alias 0x028F)",
            WaveFormatTag.G726 => $"{name} (G.726, ITU/FFmpeg; MS alias 0x0064)",
            WaveFormatTag.G726Adpcm => $"{name} (G.726, Microsoft; ITU/FFmpeg alias 0x0045)",
            WaveFormatTag.DviAdpcm => $"{name} (IMA / Intel DVI ADPCM)",
            WaveFormatTag.Adpcm => $"{name} (Microsoft ADPCM)",
            WaveFormatTag.Speex => $"{name} (community tag)",
            _ => name,
        };
    }

    public static IReadOnlyCollection<Entry> AllTags => ByTag.Values;

    public static IEnumerable<Entry> Filter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ByTag.Values.OrderBy(e => e.FormatTag);
        }

        return ByTag.Values
            .Where(e =>
                e.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                e.Family.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (e.FormatTag is ushort t && t.ToString("X4").Contains(text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(e => e.FormatTag);
    }
}
