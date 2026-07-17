using System.Net;

namespace SIP2Agent.UserAgentService;

/// <summary>
/// An inclusive range of UDP ports from which RTP/RTCP pairs can be allocated.
/// </summary>
public sealed record RtpPortRange
{
    public int StartPort { get; }

    public int EndPort { get; }

    public RtpPortRange(int startPort, int endPort)
    {
        if (startPort is < 1 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentException("The RTP port range start must be a valid IP port.", nameof(startPort));
        }

        if (endPort is < 1 or > IPEndPoint.MaxPort)
        {
            throw new ArgumentException("The RTP port range end must be a valid IP port.", nameof(endPort));
        }

        if (startPort % 2 != 0)
        {
            throw new ArgumentException("The RTP port range start must be even.", nameof(startPort));
        }

        if (endPort % 2 == 0)
        {
            throw new ArgumentException("The RTP port range end must be odd so it includes complete RTP/RTCP pairs.", nameof(endPort));
        }

        // SIPSorcery's PortRange requires at least two complete pairs and rotates through them when a port is busy.
        if (endPort - startPort < 3)
        {
            throw new ArgumentException("The RTP port range must contain at least two RTP/RTCP port pairs.", nameof(endPort));
        }

        StartPort = startPort;
        EndPort = endPort;
    }

    public static RtpPortRange Parse(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Option '{optionName}' requires an inclusive range in the form <even-start>-<odd-end>.");
        }

        string[] parts = value.Split('-', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out int startPort) ||
            !int.TryParse(parts[1], out int endPort))
        {
            throw new ArgumentException($"Option '{optionName}' must be an inclusive range in the form <even-start>-<odd-end>.");
        }

        try
        {
            return new RtpPortRange(startPort, endPort);
        }
        catch (ArgumentException excp)
        {
            throw new ArgumentException($"Option '{optionName}' is invalid: {excp.Message}", excp);
        }
    }
}
