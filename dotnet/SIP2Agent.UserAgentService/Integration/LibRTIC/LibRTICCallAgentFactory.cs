using LibRTIC.Config;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SIP2Agent.UserAgentService.Service;
using SIPSorcery.Sys;

namespace SIP2Agent.UserAgentService.Integration.LibRTIC;

/// <summary>Owns one validated configuration and creates isolated per-call agents.</summary>
internal sealed class LibRTICCallAgentFactory
{
    private readonly RTICConfig _configuration;
    private readonly LibRTICCallAgentOptions _options;
    private readonly ILogger _logger;

    internal LibRTICCallAgentFactory(
        RTICConfig configuration,
        LibRTICCallAgentOptions? options = null,
        ILogger? logger = null)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options ?? new LibRTICCallAgentOptions();
        _logger = logger ?? NullLogger<LibRTICCallAgent>.Instance;
    }

    internal static LibRTICCallAgentFactory FromLoadResult(
        RTICConfigLoadResult result,
        LibRTICCallAgentOptions? options = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsSuccess)
        {
            throw new AgentPreparationException(
                AgentPreparationFailureKind.Configuration,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        }

        return new LibRTICCallAgentFactory(result.Config!, options, logger);
    }

    internal ICallAgent Create(bool acceptRtpFromAny, PortRange? rtpPortRange)
        => LibRTICCallAgent.Create(_configuration, _options, _logger, acceptRtpFromAny, rtpPortRange);
}
