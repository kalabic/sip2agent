namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Peer payload models exposed by the library. None of these imply codec decode/encode.
/// </summary>
[Experimental("S2AWAV001")]
internal enum WavePayloadKind
{
    /// <summary>Integer linear PCM as declared by the format (raw little-endian sample frames).</summary>
    Pcm = 0,

    /// <summary>IEEE floating-point PCM as declared by the format (raw little-endian sample frames).</summary>
    IeeeFloat = 1,

    /// <summary>Opaque byte stream for any other or unknown codec; app owns framing.</summary>
    Raw = 2,
}
