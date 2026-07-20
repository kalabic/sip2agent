using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using SIP2Agent.UserAgentService;
using SIP2Agent.UserAgentService.Service;
using SIPSorcery.SIP;

namespace SIP2Agent.AgentCli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("digest", StringComparison.OrdinalIgnoreCase))
        {
            return SIPDigestCommand.Run(args[1..], System.Console.Out, System.Console.Error);
        }

        SIPEndpointOptions options;
        try
        {
            options = SIPEndpointOptions.Parse(args);
        }
        catch (ArgumentException argExcp)
        {
            System.Console.Error.WriteLine(argExcp.Message);
            SIPEndpointOptions.PrintUsage(System.Console.Error);
            return 1;
        }

        if (options.ShowHelp)
        {
            SIPEndpointOptions.PrintUsage(System.Console.Out);
            return 0;
        }

        SIPEndpointConfig config;
        try
        {
            config = SIPEndpointConfig.Load(options);
        }
        catch (ArgumentException argExcp)
        {
            System.Console.Error.WriteLine(argExcp.Message);
            return 1;
        }

        using var loggerFactory = CreateLoggerFactory(config.Verbose);
        SIPSorcery.LogFactory.Set(loggerFactory);
        var logger = loggerFactory.CreateLogger(nameof(Program));

        try
        {
            SIPEndpointConfig.Validate(config);
        }
        catch (ArgumentException argExcp)
        {
            System.Console.Error.WriteLine(argExcp.Message);
            return 1;
        }

        logger.LogInformation("SIP2Agent Agent CLI.");
        logger.LogInformation("Local SIP port {LocalSipPort}.", config.LocalSipPort == 0 ? "OS selected" : config.LocalSipPort);
        if (config.RtpPortRange is { } rtpPortRange)
        {
            logger.LogInformation("RTP/RTCP ports will be allocated from {StartPort}-{EndPort}.", rtpPortRange.StartPort, rtpPortRange.EndPort);
        }
        else
        {
            logger.LogInformation("RTP/RTCP ports will be selected by the OS for each call.");
        }
        if (!string.IsNullOrWhiteSpace(config.AnswerAudioFile))
        {
            logger.LogInformation("Answer audio file {AnswerAudioFile}.", config.AnswerAudioFile);
        }

        try
        {
            await using var endpoint = new SIPEndpointService(config, loggerFactory);
            using var console = new SIPEndpointConsole(endpoint, config, loggerFactory);
            await console.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception excp)
        {
            logger.LogError(excp, "SIP endpoint failed.");
            return 1;
        }
    }

    private static ILoggerFactory CreateLoggerFactory(bool verbose)
    {
        var serilogLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Is(verbose ? Serilog.Events.LogEventLevel.Verbose : Serilog.Events.LogEventLevel.Debug)
            .WriteTo.Console()
            .CreateLogger();

        return new SerilogLoggerFactory(serilogLogger, dispose: true);
    }
}
