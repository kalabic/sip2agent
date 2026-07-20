using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService;

public sealed class SIPEndpointOptions
{
    public int? LocalSipPort { get; private init; }

    public int? RtpPort { get; private init; }

    public RtpPortRange? RtpPortRange { get; private init; }

    public string? ContactHost { get; private init; }

    public string? RegistrarServer { get; private init; }

    public SIPProtocolsEnum? Transport { get; private init; }

    public string? Username { get; private init; }

    public string? Passwd { get; private init; }

    public string? PasswordFile { get; private init; }

    public string? DigestStoreFile { get; private init; }

    public string? Realm { get; private init; }

    public string? OutboundProxy { get; private init; }

    public int? MaxReconnectCycles { get; private init; }

    public int? RegisterRetryIntervalSeconds { get; private init; }

    public string? AnswerAudioFile { get; private init; }

    public bool? Headless { get; private init; }

    public bool? Verbose { get; private init; }

    public bool ShowHelp { get; private init; }

    public string? ConfigFilePath { get; private init; }

    public static SIPEndpointOptions Parse(string[] args)
    {
        if (args.Length > 0 && (args[0] == "--config" || args[0] == "-c"))
        {
            if (args.Length != 2)
            {
                throw new ArgumentException("Option '--config' must be supplied by itself with exactly one path.");
            }

            return new SIPEndpointOptions { ConfigFilePath = args[1] };
        }

        if (args.Any(arg => arg is "--config" or "-c"))
        {
            throw new ArgumentException("Option '--config' must be supplied by itself with exactly one path.");
        }

        int? localSipPort = null;
        int? rtpPort = null;
        RtpPortRange? rtpPortRange = null;
        string? contactHost = null;
        string? registrarServer = null;
        SIPProtocolsEnum? transport = null;
        string? username = null;
        string? passwd = null;
        string? passwordFile = null;
        string? digestStoreFile = null;
        string? realm = null;
        string? outboundProxy = null;
        int? maxReconnectCycles = null;
        int? registerRetryIntervalSeconds = null;
        string? answerAudioFile = null;
        bool? headless = null;
        bool? verbose = null;
        bool showHelp = false;
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            switch (arg)
            {
                case "--local-sip-port":
                    localSipPort = ParsePort(ReadValue(args, ref index, arg), arg, allowZero: true);
                    break;

                case "--rtp-port":
                    rtpPort = ParsePort(ReadValue(args, ref index, arg), arg, allowZero: true);
                    break;

                case "--rtp-port-range":
                    rtpPortRange = RtpPortRange.Parse(ReadValue(args, ref index, arg), arg);
                    break;

                case "--contact-host":
                    contactHost = ReadValue(args, ref index, arg);
                    break;

                case "--registrar":
                    registrarServer = ReadValue(args, ref index, arg);
                    break;

                case "--transport":
                    transport = ParseTransport(ReadValue(args, ref index, arg), arg);
                    break;

                case "--username":
                    username = ReadValue(args, ref index, arg);
                    break;

                case "--passwd":
                    passwd = ReadValue(args, ref index, arg);
                    break;

                case "--password-file":
                    passwordFile = ReadValue(args, ref index, arg);
                    break;

                case "--digest-store":
                    digestStoreFile = ReadValue(args, ref index, arg);
                    break;

                case "--realm":
                    realm = ReadValue(args, ref index, arg);
                    break;

                case "--outbound-proxy":
                    outboundProxy = ReadValue(args, ref index, arg);
                    break;

                case "--max-reconnects":
                    maxReconnectCycles = ParseNonNegativeInt(ReadValue(args, ref index, arg), arg);
                    break;

                case "--register-retry-interval":
                    registerRetryIntervalSeconds = ParsePositiveInt(ReadValue(args, ref index, arg), arg);
                    break;

                case "--answer-audio-file":
                    answerAudioFile = ReadValue(args, ref index, arg);
                    break;

                case "--headless":
                    headless = true;
                    break;

                case "--verbose":
                case "-v":
                    verbose = true;
                    break;

                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                default:
                    throw new ArgumentException($"Unrecognised option '{arg}'.");
            }
        }

        int credentialOptionCount =
            (passwd != null ? 1 : 0) +
            (passwordFile != null ? 1 : 0) +
            (digestStoreFile != null ? 1 : 0);
        if (credentialOptionCount > 1)
        {
            throw new ArgumentException("Options '--passwd', '--password-file' and '--digest-store' are mutually exclusive.");
        }

        return new SIPEndpointOptions
        {
            LocalSipPort = localSipPort,
            RtpPort = rtpPort,
            RtpPortRange = rtpPortRange,
            ContactHost = contactHost,
            RegistrarServer = registrarServer,
            Transport = transport,
            Username = username,
            Passwd = passwd,
            PasswordFile = passwordFile,
            DigestStoreFile = digestStoreFile,
            Realm = realm,
            OutboundProxy = outboundProxy,
            MaxReconnectCycles = maxReconnectCycles,
            RegisterRetryIntervalSeconds = registerRetryIntervalSeconds,
            AnswerAudioFile = answerAudioFile,
            Headless = headless,
            Verbose = verbose,
            ShowHelp = showHelp
        };
    }

    public static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: (Windows '.exe' example)");
        writer.WriteLine("  SIP2Agent.AgentCli.exe --registrar <sip-uri-or-host> --username <value> [--realm <value>]");
        writer.WriteLine("                          [--digest-store <path> | --passwd <value> | --password-file <path>] [options]");
        writer.WriteLine("  SIP2Agent.AgentCli.exe --config <path>");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --registrar <sip-uri-or-host>    SIP registrar/PBX trunk server.");
        writer.WriteLine("  --transport tls|udp              SIP transport for bare registrar values. Default: tls.");
        writer.WriteLine("  --realm <value>                  Optional SIP auth realm.");
        writer.WriteLine("  --username <value>               SIP account username.");
        writer.WriteLine("  --passwd <value>                 SIP account password in clear text.");
        writer.WriteLine("  --password-file <path>           File containing the SIP account password.");
        writer.WriteLine("  --digest-store <path>            File containing precomputed SIP HA1 digest credentials.");
        writer.WriteLine("  --outbound-proxy <endpoint>      Optional outbound proxy, e.g. udp:127.0.0.1:5060.");
        writer.WriteLine("  --max-reconnects <count>         Temporary registration failure budget. Default: 5.");
        writer.WriteLine("  --contact-host <host>            Contact host or public IP advertised in SIP headers.");
        writer.WriteLine("  --local-sip-port <port>          Local SIP receive port. Default: 0, selected by the OS.");
        writer.WriteLine("  --rtp-port-range <start-end>     Inclusive even RTP/odd RTCP port range, e.g. 10000-10019.");
        writer.WriteLine("  --rtp-port <port>                Deprecated fixed RTP port; endpoint startup will reject it.");
        writer.WriteLine("  --register-retry-interval <sec>  Delay after temporary registration failure. Default: 30.");
        writer.WriteLine("  --answer-audio-file <path>       Raw 8 kHz audio file to play after answering an inbound call.");
        writer.WriteLine("  --headless                       Run until Ctrl+C without interactive key commands.");
        writer.WriteLine("  --verbose, -v                    Enable SIP transport trace logging.");
        writer.WriteLine("  --config, -c <path>              Authoritative YAML configuration; must be the only option.");
        writer.WriteLine("  --help, -h                       Show this help.");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  digest                           Create a SIP digest-store file. Run 'digest --help' for details.");
        writer.WriteLine();
        writer.WriteLine("Create digest file for SIP HA1 authentication:");
        writer.WriteLine("  SIP2Agent.AgentCli.exe digest --username <value> --realm <value> (--passwd <value> | --password-file <path>) --out <path>");
        writer.WriteLine();
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static int ParsePort(string value, string option, bool allowZero = false)
    {
        if (!int.TryParse(value, out int port) ||
            port < (allowZero ? 0 : 1) ||
            port > ushort.MaxValue)
        {
            throw new ArgumentException($"Option '{option}' must be a valid IP port.");
        }

        return port;
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, out int result) || result < 1)
        {
            throw new ArgumentException($"Option '{option}' must be a positive integer.");
        }

        return result;
    }

    private static int ParseNonNegativeInt(string value, string option)
    {
        if (!int.TryParse(value, out int result) || result < 0)
        {
            throw new ArgumentException($"Option '{option}' must be a non-negative integer.");
        }

        return result;
    }

    private static SIPProtocolsEnum ParseTransport(string value, string option)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "tls" => SIPProtocolsEnum.tls,
            "udp" => SIPProtocolsEnum.udp,
            _ => throw new ArgumentException($"Option '{option}' must be either 'tls' or 'udp'.")
        };
    }

}
