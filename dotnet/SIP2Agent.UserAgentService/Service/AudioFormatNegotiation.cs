using SIPSorceryMedia.Abstractions;

namespace SIP2Agent.UserAgentService.Service;

internal sealed record AudioCodecProfile(
    AudioFormat Format,
    int PcmSampleRate,
    int RtpClockRate,
    int PcmSamplesPerPacket,
    uint RtpUnitsPerPacket,
    int EncodedBytesPerPacket);

internal sealed class AudioFormatNegotiation
{
    internal const int PacketDurationMilliseconds = 20;

    private const int G729EncodedBytesPerFrame = 10;
    private const int G729PcmSamplesPerFrame = 80;

    private static readonly AudioCodecProfile[] SupportedProfiles =
    [
        CreateProfile(SDPWellKnownMediaFormatsEnum.PCMU, encodedBytesPerPacket: 160),
        CreateProfile(SDPWellKnownMediaFormatsEnum.PCMA, encodedBytesPerPacket: 160),
        CreateProfile(SDPWellKnownMediaFormatsEnum.G722, encodedBytesPerPacket: 160),
        CreateProfile(SDPWellKnownMediaFormatsEnum.G729, encodedBytesPerPacket: 20),
    ];

    private readonly MediaFormatManager<AudioFormat> _formats;

    internal AudioFormatNegotiation()
    {
        _formats = new MediaFormatManager<AudioFormat>(CreateSupportedFormats());
        _formats.SetSelectedFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
    }

    internal AudioFormat SelectedFormat => new(_formats.SelectedFormat);

    internal AudioCodecProfile SelectedProfile => GetProfile(_formats.SelectedFormat);

    internal static List<AudioFormat> CreateSupportedFormats()
        => SupportedProfiles.Select(profile => new AudioFormat(profile.Format)).ToList();

    internal List<AudioFormat> GetFormats()
        => _formats.GetSourceFormats().Select(format => new AudioFormat(format)).ToList();

    internal void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _formats.RestrictFormats(filter);
    }

    internal void Select(AudioFormat format)
    {
        _ = GetProfile(format);
        if (!_formats.GetSourceFormats().Any(candidate => AreEquivalent(candidate, format)))
        {
            throw new ArgumentException(
                "The audio format is not permitted for this media direction.",
                nameof(format));
        }

        try
        {
            _formats.SetSelectedFormat(new AudioFormat(format));
        }
        catch (Exception exception)
        {
            throw new ArgumentException(
                "The audio format could not be selected for this media direction.",
                nameof(format),
                exception);
        }
    }

    internal static AudioCodecProfile GetProfile(AudioFormat format)
    {
        foreach (AudioCodecProfile profile in SupportedProfiles)
        {
            if (AreEquivalent(profile.Format, format))
            {
                return profile;
            }
        }

        throw new ArgumentException(
            "Only mono PCMU, PCMA, G722, and G729 with their standard media and RTP clocks are supported.",
            nameof(format));
    }

    internal static void ValidateDecodedPayload(
        AudioCodecProfile profile,
        int payloadLength,
        int decodedSampleCount)
    {
        if (payloadLength <= 0)
        {
            throw new InvalidDataException("An encoded SIP audio frame was empty.");
        }

        int expectedSampleCount = profile.Format.Codec switch
        {
            AudioCodecsEnum.PCMU or AudioCodecsEnum.PCMA => payloadLength,
            AudioCodecsEnum.G722 => checked(payloadLength * 2),
            AudioCodecsEnum.G729 when payloadLength % G729EncodedBytesPerFrame == 0
                => checked(payloadLength / G729EncodedBytesPerFrame * G729PcmSamplesPerFrame),
            AudioCodecsEnum.G729 => throw new InvalidDataException(
                "A G.729 payload must contain complete 10-byte speech frames."),
            _ => throw new InvalidDataException(
                $"The {profile.Format.FormatName} payload cannot be validated."),
        };

        if (decodedSampleCount != expectedSampleCount)
        {
            throw new InvalidDataException(
                $"The {profile.Format.FormatName} decoder returned {decodedSampleCount} samples; " +
                $"{expectedSampleCount} were expected for a {payloadLength}-byte payload.");
        }
    }

    internal static bool AreEquivalent(AudioFormat left, AudioFormat right)
        => left.Codec == right.Codec &&
           left.ClockRate == right.ClockRate &&
           left.RtpClockRate == right.RtpClockRate &&
           left.ChannelCount == right.ChannelCount;

    private static AudioCodecProfile CreateProfile(
        SDPWellKnownMediaFormatsEnum wellKnownFormat,
        int encodedBytesPerPacket)
    {
        AudioFormat format = new(wellKnownFormat);
        return new AudioCodecProfile(
            format,
            format.ClockRate,
            format.RtpClockRate,
            checked(format.ClockRate * PacketDurationMilliseconds / 1_000),
            checked((uint)(format.RtpClockRate * PacketDurationMilliseconds / 1_000)),
            encodedBytesPerPacket);
    }
}
