using System.Buffers.Binary;
using SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;

namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Convenience factories for common core and telephony WAVE formats.
/// Values follow typical WAVEFORMATEX usage (FFmpeg / Windows); opaque codecs still need
/// the app to supply correctly framed payload bytes.
/// </summary>
internal readonly partial record struct WaveFormat
{
    // ---- Core linear ----

    /// <summary>Classic integer PCM (<c>WAVE_FORMAT_PCM</c>).</summary>
    public static WaveFormat Pcm(ushort channels = 2, uint sampleRate = 44100, ushort bitsPerSample = 16)
    {
        RequireChannels(channels);
        RequireSampleRate(sampleRate);
        if (bitsPerSample == 0 || bitsPerSample % 8 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "Bits per sample must be a positive multiple of 8.");
        }

        ushort blockAlign = checked((ushort)(channels * (bitsPerSample / 8)));
        return new WaveFormat
        {
            FormatTag = WaveFormatTag.Pcm,
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = bitsPerSample,
            BlockAlign = blockAlign,
            AverageBytesPerSecond = sampleRate * blockAlign,
            ExtraBytes = [],
        };
    }

    /// <summary>Classic IEEE float PCM (<c>WAVE_FORMAT_IEEE_FLOAT</c>), 32 or 64 bits.</summary>
    public static WaveFormat IeeeFloat(ushort channels = 2, uint sampleRate = 44100, ushort bitsPerSample = 32)
    {
        RequireChannels(channels);
        RequireSampleRate(sampleRate);
        if (bitsPerSample is not (32 or 64))
        {
            throw new ArgumentOutOfRangeException(nameof(bitsPerSample), "IEEE float WAV typically uses 32 or 64 bits.");
        }

        ushort blockAlign = checked((ushort)(channels * (bitsPerSample / 8)));
        return new WaveFormat
        {
            FormatTag = WaveFormatTag.IeeeFloat,
            Channels = channels,
            SampleRate = sampleRate,
            BitsPerSample = bitsPerSample,
            BlockAlign = blockAlign,
            AverageBytesPerSecond = sampleRate * blockAlign,
            ExtraBytes = [],
        };
    }

    // ---- helpers ----

    private static void RequireChannels(ushort channels)
    {
        if (channels == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(channels));
        }
    }

    private static void RequireSampleRate(uint sampleRate)
    {
        if (sampleRate == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        }
    }
}
