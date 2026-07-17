using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService;


public sealed class SIPEndpointConfig
{

    public int LocalSipPort { get; init; }

    public int RtpPort { get; init; }

    public RtpPortRange? RtpPortRange { get; init; }

    public string? ContactHost { get; init; }

    public bool Headless { get; init; }

    public bool Verbose { get; init; }

    public bool AcceptRtpFromAny { get; init; } = true;

    public string? AnswerAudioFile { get; init; }

    public IReadOnlyList<SIPRegistrationProfile> AccountProfiles { get; init; } = [];

    public static SIPEndpointConfig Load(SIPEndpointOptions options)
    {
        string? contactHost = options.ContactHost ?? Environment.GetEnvironmentVariable(S2AEnvVariables.PublicIpAddress);
        string? rtpPortRangeValue = Environment.GetEnvironmentVariable(S2AEnvVariables.RtpPortRange);
        IReadOnlyList<SIPRegistrationProfile> profiles = BuildAccountProfiles(options, contactHost);

        return new SIPEndpointConfig
        {
            LocalSipPort = options.LocalSipPort ?? GetEnvInt(S2AEnvVariables.LocalPort) ?? 0,
            RtpPort = options.RtpPort ?? 0,
            RtpPortRange = options.RtpPortRange ??
                (string.IsNullOrWhiteSpace(rtpPortRangeValue) ? null : RtpPortRange.Parse(rtpPortRangeValue, S2AEnvVariables.RtpPortRange)),
            ContactHost = contactHost,
            Headless = options.Headless ?? false,
            Verbose = options.Verbose ?? false,
            AnswerAudioFile = options.AnswerAudioFile,
            AccountProfiles = profiles
        };
    }

    public static void Validate(SIPEndpointConfig config)
    {
        if (config.RtpPort != 0)
        {
            throw new ArgumentException("A fixed RTP port is not supported for concurrent inbound calls. Use '--rtp-port-range <even-start>-<odd-end>' or omit '--rtp-port' to use OS-selected ports.");
        }

        if (!string.IsNullOrWhiteSpace(config.AnswerAudioFile) &&
            !File.Exists(ResolveAnswerAudioFilePath(config.AnswerAudioFile)))
        {
            throw new ArgumentException($"Answer audio file '{config.AnswerAudioFile}' does not exist.");
        }

        if (config.AccountProfiles.Any(profile => profile.Password != null && profile.DigestStore != null))
        {
            throw new ArgumentException("A SIP registration profile cannot specify both a password and a digest store.");
        }
    }

    public static string ResolveAnswerAudioFilePath(string answerAudioFile)
    {
        if (Path.IsPathRooted(answerAudioFile) || File.Exists(answerAudioFile))
        {
            return answerAudioFile;
        }

        return Path.Combine(AppContext.BaseDirectory, answerAudioFile);
    }

    private static IReadOnlyList<SIPRegistrationProfile> BuildAccountProfiles(SIPEndpointOptions options, string? contactHost)
    {
        string? registrar = options.RegistrarServer ?? Environment.GetEnvironmentVariable(S2AEnvVariables.Registrar);
        string? username = options.Username ?? Environment.GetEnvironmentVariable(S2AEnvVariables.Username);
        SIPProtocolsEnum transport =
            options.Transport ?? SIPProtocolsEnum.tls;

        if (string.IsNullOrWhiteSpace(registrar) || string.IsNullOrWhiteSpace(username))
        {
            return [];
        }

        SIPURI registrarUri = NormalizeRegistrarUri(registrar, transport);
        SIPProtocolsEnum effectiveTransport = registrarUri.Protocol;
        SIPEndPoint? outboundProxy = ParseOutboundProxy(options.OutboundProxy);
        string? password = GetPassword(options);
        SIPDigestStore? digestStore = GetDigestStore(options);

        return
        [
            new SIPRegistrationProfile(
                Name: "default",
                RegistrarUri: registrarUri,
                Transport: effectiveTransport,
                Username: username,
                Password: password,
                Realm: options.Realm,
                OutboundProxy: outboundProxy,
                ContactHost: contactHost,
                RegisterFailureRetryIntervalSeconds:
                    options.RegisterRetryIntervalSeconds ?? 30,
                MaxReconnectCycles: options.MaxReconnectCycles ?? 5,
                DigestStore: digestStore)
        ];
    }

    private static int? GetEnvInt(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out int result) ? result : null;
    }

    private static string? GetPassword(SIPEndpointOptions options)
    {
        if (options.Passwd != null)
        {
            return options.Passwd;
        }

        return options.PasswordFile != null
            ? PasswordFile.Read(options.PasswordFile)
            : null;
    }

    private static SIPDigestStore? GetDigestStore(SIPEndpointOptions options)
    {
        if (options.DigestStoreFile == null)
        {
            return null;
        }

        try
        {
            return SIPDigestStore.ReadFromFile(options.DigestStoreFile);
        }
        catch (Exception excp) when (excp is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException($"Digest store '{options.DigestStoreFile}' could not be read.", excp);
        }
    }

    private static SIPURI NormalizeRegistrarUri(string registrar, SIPProtocolsEnum transport)
    {
        string trimmedRegistrar = registrar.Trim();
        bool hasExplicitScheme =
            trimmedRegistrar.StartsWith("sip:", StringComparison.OrdinalIgnoreCase) ||
            trimmedRegistrar.StartsWith("sips:", StringComparison.OrdinalIgnoreCase);
        bool hasExplicitTransport =
            trimmedRegistrar.Contains(";transport=", StringComparison.OrdinalIgnoreCase);

        if (hasExplicitScheme)
        {
            return SIPURI.ParseSIPURIRelaxed(trimmedRegistrar);
        }

        if (hasExplicitTransport)
        {
            return SIPURI.ParseSIPURIRelaxed($"sip:{trimmedRegistrar}");
        }

        string registrarUri = transport == SIPProtocolsEnum.udp
            ? $"sip:{trimmedRegistrar};transport=udp"
            : $"sips:{trimmedRegistrar}";

        return SIPURI.ParseSIPURIRelaxed(registrarUri);
    }

    private static SIPEndPoint? ParseOutboundProxy(string? outboundProxy)
    {
        if (string.IsNullOrWhiteSpace(outboundProxy))
        {
            return null;
        }

        return SIPEndPoint.TryParse(outboundProxy) ??
               throw new ArgumentException($"Outbound proxy endpoint '{outboundProxy}' is invalid.");
    }
}
