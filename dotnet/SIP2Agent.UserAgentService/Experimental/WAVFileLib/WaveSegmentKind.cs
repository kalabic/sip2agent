namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Kind of one segment in a WAVE wave-list (<c>LIST</c>/<c>wavl</c>) or a classic single-<c>data</c> file.
/// </summary>
[Experimental("S2AWAV001")]
internal enum WaveSegmentKind
{
    /// <summary>RIFF <c>data</c> body — opaque audio bytes.</summary>
    Sound = 0,

    /// <summary>RIFF <c>slnt</c> body — silent sample-frame count (not raw audio bytes).</summary>
    Silence = 1,
}
