namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib;

/// <summary>
/// Thrown when a RIFF/WAVE container is structurally invalid or an I/O operation violates the API contract.
/// </summary>
[Experimental("S2AWAV001")]
internal sealed class WavException : Exception
{
    public WavException()
    {
    }

    public WavException(string message)
        : base(message)
    {
    }

    public WavException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
