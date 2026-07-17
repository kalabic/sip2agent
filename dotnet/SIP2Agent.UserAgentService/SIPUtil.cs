using System.Net;
using Microsoft.Extensions.Logging;
using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService;

internal static class SIPUtil
{
    public static void StartTransport(SIPEndpointConfig config, SIPTransport transport, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(config.ContactHost))
        {
            transport.ContactHost = config.ContactHost;
            logger.LogInformation("Using SIP Contact host {ContactHost}.", config.ContactHost);
        }

        if (config.Verbose)
        {
            transport.EnableTraceLogs();
        }

        transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.Any, config.LocalSipPort)));

        if (config.LocalSipPort != 0)
        {
            transport.AddSIPChannel(new SIPUDPChannel(new IPEndPoint(IPAddress.IPv6Any, config.LocalSipPort)));
            transport.AddSIPChannel(new SIPTCPChannel(new IPEndPoint(IPAddress.Any, config.LocalSipPort)));
        }

        foreach (SIPChannel channel in transport.GetSIPChannels())
        {
            logger.LogInformation("SIP channel ready on {LocalSipEndPoint}.", channel.ListeningSIPEndPoint);
        }
    }

    public static void ShutdownTransport(SIPTransport transport, ILogger logger)
    {
        logger.LogInformation("Shutting down SIP transport.");
        transport.Shutdown();
    }

    public static Task SendResponseAsync(SIPTransport transport, SIPRequest sipRequest, SIPResponseStatusCodesEnum status)
    {
        SIPResponse response = SIPResponse.GetResponse(sipRequest, status, null);
        return transport.SendResponseAsync(response);
    }

    public static bool IsInDialogRequest(SIPRequest sipRequest)
    {
        return sipRequest.Header.From?.FromTag != null &&
               sipRequest.Header.To?.ToTag != null;
    }

}
