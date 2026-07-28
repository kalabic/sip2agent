using System.Buffers.Binary;
using SIP2Agent.UserAgentService.Experimental.WAVFileLib.Formats;

namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib.Internal;

[Experimental("S2AWAV001")]
internal static class FmtChunkWriter
{
    public static byte[] BuildPayload(in WaveFormat format, bool forceExtensible)
    {
        bool extensible = forceExtensible || format.FormatTag == WaveFormatTag.Extensible;

        if (extensible)
        {
            return BuildExtensible(format);
        }

        // Prefer structured classic 16-byte when no extra bytes, or when extra is empty.
        if (format.ExtraBytes is { Length: > 0 })
        {
            // Preserve opaque extension (cbSize + body already included as stored after core).
            var buffer = new byte[16 + format.ExtraBytes.Length];
            WriteCore(buffer, format.FormatTag, format);
            format.ExtraBytes.CopyTo(buffer.AsSpan(16));
            return buffer;
        }

        var classic = new byte[16];
        WriteCore(classic, format.FormatTag, format);
        return classic;
    }

    private static void WriteCore(Span<byte> dest, ushort formatTag, in WaveFormat format)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(dest, formatTag);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[2..], format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[4..], format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(dest[8..], format.AverageBytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[12..], format.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(dest[14..], format.BitsPerSample);
    }

    private static byte[] BuildExtensible(in WaveFormat format)
    {
        // If caller already provided full extra bytes for extensible, reuse when complete.
        if (format.FormatTag == WaveFormatTag.Extensible &&
            format.ExtraBytes is { Length: >= 24 } &&
            format.SubFormat != Guid.Empty)
        {
            var buffer = new byte[16 + format.ExtraBytes.Length];
            WriteCore(buffer, WaveFormatTag.Extensible, format);
            format.ExtraBytes.CopyTo(buffer.AsSpan(16));
            return buffer;
        }

        Guid sub = format.SubFormat;
        if (sub == Guid.Empty)
        {
            // Map classic tag into extensible subtype.
            ushort baseTag = format.FormatTag == WaveFormatTag.Extensible
                ? WaveFormatTag.Pcm
                : format.FormatTag;
            sub = WaveSubFormatGuids.FromTag(baseTag == WaveFormatTag.Extensible ? WaveFormatTag.Pcm : baseTag);
            if (format.FormatTag is WaveFormatTag.Pcm or WaveFormatTag.IeeeFloat)
            {
                sub = WaveSubFormatGuids.FromTag(format.FormatTag);
            }
            else if (format.FormatTag != WaveFormatTag.Extensible)
            {
                sub = WaveSubFormatGuids.FromTag(format.FormatTag);
            }
        }

        ushort validBits = format.ValidBitsPerSample != 0
            ? format.ValidBitsPerSample
            : format.BitsPerSample;

        var payload = new byte[40]; // 16 core + 2 cbSize + 22 extension
        WriteCore(payload, WaveFormatTag.Extensible, format);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(18), validBits);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20), format.ChannelMask);
        if (!sub.TryWriteBytes(payload.AsSpan(24, 16)))
        {
            throw new WavException("Failed to write SubFormat GUID.");
        }

        return payload;
    }
}
