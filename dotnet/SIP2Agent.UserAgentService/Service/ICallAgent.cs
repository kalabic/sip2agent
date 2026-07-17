using SIPSorcery.Media;

namespace SIP2Agent.UserAgentService.Service;

internal interface ICallAgent
{
    VoIPMediaSession MediaSession { get; }

    Task Completion { get; }

    Task PrepareAsync(CancellationToken cancellationToken);

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(string reason, CancellationToken cancellationToken);
}
