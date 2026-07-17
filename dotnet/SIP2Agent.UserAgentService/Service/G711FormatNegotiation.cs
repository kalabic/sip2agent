using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;

namespace SIP2Agent.UserAgentService.Service;

internal sealed class G711FormatNegotiation
{
    internal const int SampleRate = 8_000;

    private readonly MediaFormatManager<AudioFormat> _formats;

    internal G711FormatNegotiation()
    {
        _formats = new MediaFormatManager<AudioFormat>(
        [
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU),
            new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA),
        ]);
        _formats.SetSelectedFormat(new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMU));
    }

    internal AudioFormat SelectedFormat => new(_formats.SelectedFormat);

    internal List<AudioFormat> GetFormats()
        => _formats.GetSourceFormats().Select(format => new AudioFormat(format)).ToList();

    internal void RestrictFormats(Func<AudioFormat, bool> filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        _formats.RestrictFormats(filter);
    }

    internal void Select(AudioFormat format)
    {
        Validate(format);
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

    internal static void Validate(AudioFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (format.Codec is not AudioCodecsEnum.PCMU and not AudioCodecsEnum.PCMA ||
            format.ClockRate != SampleRate ||
            format.RtpClockRate != SampleRate ||
            format.ChannelCount != 1)
        {
            throw new ArgumentException(
                "Only mono PCMU and PCMA with 8 kHz media and RTP clocks are supported.",
                nameof(format));
        }
    }

    internal static bool AreEquivalent(AudioFormat left, AudioFormat right)
        => left.Codec == right.Codec &&
           left.ClockRate == right.ClockRate &&
           left.RtpClockRate == right.RtpClockRate &&
           left.ChannelCount == right.ChannelCount;
}
