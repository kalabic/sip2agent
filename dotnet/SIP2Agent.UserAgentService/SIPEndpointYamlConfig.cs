using System.Globalization;
using SIPSorcery.SIP;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SIP2Agent.UserAgentService;

public sealed partial class SIPEndpointConfig
{
    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithDuplicateKeyChecking()
        .WithEnforceNullability()
        .Build();

    /// <summary>Loads a SIP2Agent-owned, authoritative YAML configuration file.</summary>
    public static SIPEndpointConfig LoadYaml(string path)
    {
        string fullPath = RequireYamlFile(path, "$", null);
        string yaml;
        try
        {
            yaml = File.ReadAllText(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw YamlError(fullPath, "$", null, "Configuration file could not be read.");
        }

        var marks = new YamlMarks();
        ScanYaml(yaml, fullPath, marks);
        ValidateYamlShape(fullPath, marks);

        YamlEndpointDocument document;
        try
        {
            document = YamlDeserializer.Deserialize<YamlEndpointDocument>(yaml)
                ?? throw new YamlException("Empty document.");
        }
        catch (YamlException exception)
        {
            string message = exception.Message.Contains("Property", StringComparison.OrdinalIgnoreCase) &&
                exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                ? "Unknown or incorrectly cased configuration key."
                : "YAML does not match the SIP2Agent configuration schema.";
            throw YamlError(fullPath, marks.PathAt(exception.Start), exception.Start, message);
        }
        catch (Exception)
        {
            throw YamlError(fullPath, "$", null, "YAML does not match the SIP2Agent configuration schema.");
        }

        return ConvertYamlDocument(document, fullPath, marks);
    }

    private static SIPEndpointConfig ConvertYamlDocument(YamlEndpointDocument document, string configPath, YamlMarks marks)
    {
        YamlEndpointDocument.RegistrationMapping? registration = document.Registration;
        string? contactHost = document.Endpoint?.ContactHost;
        if (document.Endpoint?.LocalSipPort is < 0 or > ushort.MaxValue)
        {
            throw YamlError(configPath, "$.endpoint.local_sip_port", marks.At("$.endpoint.local_sip_port"), "Local SIP port must be from 0 through 65535.");
        }
        IReadOnlyList<SIPRegistrationProfile> profiles = [];
        if (registration is not null)
        {
            string registrar = RequireString(registration.Registrar, configPath, "$.registration.registrar", marks);
            string username = RequireString(registration.Username, configPath, "$.registration.username", marks);
            int credentialCount = (registration.Credentials?.Password is not null ? 1 : 0) +
                (registration.Credentials?.PasswordFile is not null ? 1 : 0) +
                (registration.Credentials?.DigestStoreFile is not null ? 1 : 0);
            if (credentialCount > 1)
            {
                throw YamlError(configPath, "$.registration.credentials", marks.At("$.registration.credentials"), "password, password_file, and digest_store_file are mutually exclusive.");
            }

            SIPProtocolsEnum transport = ParseYamlTransport(registration.Transport, configPath, marks);
            if (registration.MaxReconnects is < 0)
            {
                throw YamlError(configPath, "$.registration.max_reconnects", marks.At("$.registration.max_reconnects"), "max_reconnects must be a non-negative integer.");
            }
            if (registration.RegisterRetryIntervalSeconds is < 1)
            {
                throw YamlError(configPath, "$.registration.register_retry_interval_seconds", marks.At("$.registration.register_retry_interval_seconds"), "register_retry_interval_seconds must be a positive integer.");
            }
            SIPURI registrarUri;
            try { registrarUri = NormalizeRegistrarUri(registrar, transport); }
            catch (Exception) { throw YamlError(configPath, "$.registration.registrar", marks.At("$.registration.registrar"), "Registrar is invalid."); }

            SIPEndPoint? outboundProxy;
            try { outboundProxy = ParseOutboundProxy(registration.OutboundProxy); }
            catch (ArgumentException) { throw YamlError(configPath, "$.registration.outbound_proxy", marks.At("$.registration.outbound_proxy"), "Outbound proxy endpoint is invalid."); }

            string? password = registration.Credentials?.Password;
            if (registration.Credentials?.PasswordFile is { } passwordFile)
            {
                string resolved = ResolveConfigPath(configPath, passwordFile);
                try { password = PasswordFile.Read(resolved); }
                catch (ArgumentException) { throw YamlError(configPath, "$.registration.credentials.password_file", marks.At("$.registration.credentials.password_file"), "Password file could not be read."); }
            }

            SIPDigestStore? digestStore = null;
            if (registration.Credentials?.DigestStoreFile is { } digestStoreFile)
            {
                string resolved = ResolveConfigPath(configPath, digestStoreFile);
                try { digestStore = SIPDigestStore.ReadFromFile(resolved); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
                { throw YamlError(configPath, "$.registration.credentials.digest_store_file", marks.At("$.registration.credentials.digest_store_file"), "Digest store file could not be read."); }
            }

            profiles = [new SIPRegistrationProfile("default", registrarUri, registrarUri.Protocol, username, password,
                registration.Realm, outboundProxy, contactHost,
                RegisterFailureRetryIntervalSeconds: registration.RegisterRetryIntervalSeconds ?? 30,
                MaxReconnectCycles: registration.MaxReconnects ?? 5, DigestStore: digestStore)];
        }

        string? answerAudio = GetOptionalConfigPath(document.Media?.AnswerAudioFile, configPath, "$.media.answer_audio_file", marks);
        string? apiPath = GetOptionalConfigPath(document.LibRtic?.ApiConfigPath, configPath, "$.lib_rtic.api_config_path", marks);
        string? sessionPath = GetOptionalConfigPath(document.LibRtic?.SessionConfigPath, configPath, "$.lib_rtic.session_config_path", marks);
        if (document.LibRtic is not null && string.IsNullOrWhiteSpace(apiPath))
        {
            throw YamlError(configPath, "$.lib_rtic.api_config_path", marks.At("$.lib_rtic"), "A non-empty API configuration path is required.");
        }

        var config = new SIPEndpointConfig
        {
            LocalSipPort = document.Endpoint?.LocalSipPort ?? 0,
            RtpPort = 0,
            RtpPortRange = document.Media?.RtpPortRange is { } range ? ParseYamlRange(range, configPath, marks) : null,
            ContactHost = contactHost,
            Headless = document.Runtime?.Headless ?? false,
            Verbose = document.Runtime?.Verbose ?? false,
            AnswerAudioFile = answerAudio,
            LibRTICApiConfigPath = apiPath,
            LibRTICSessionConfigPath = sessionPath,
            AccountProfiles = profiles
        };
        try { Validate(config); }
        catch (ArgumentException)
        {
            string pathForError = !string.IsNullOrWhiteSpace(answerAudio) ? "$.media.answer_audio_file" : "$.lib_rtic";
            throw YamlError(configPath, pathForError, marks.At(pathForError), "Referenced file is invalid or unavailable.");
        }
        return config;
    }

    private static void ScanYaml(string yaml, string file, YamlMarks marks)
    {
        try
        {
            var parser = new Parser(new StringReader(yaml));
            parser.MoveNext();
            if (parser.Current is not StreamStart) throw new YamlException("Missing stream start.");
            parser.MoveNext();
            if (parser.Current is not DocumentStart) throw YamlError(file, "$", MarkOf(parser.Current), "A single YAML document is required.");
            parser.MoveNext();
            if (parser.Current is not MappingStart) throw YamlError(file, "$", MarkOf(parser.Current), "The YAML root must be a mapping.");
            ScanMapping(parser, file, "$", marks);
            if (parser.Current is not DocumentEnd) throw YamlError(file, "$", MarkOf(parser.Current), "A single YAML document is required.");
            parser.MoveNext();
            if (parser.Current is not StreamEnd) throw YamlError(file, "$", MarkOf(parser.Current), "Only one YAML document is accepted.");
        }
        catch (ArgumentException) { throw; }
        catch (YamlException exception) { throw YamlError(file, "$", exception.Start, "Malformed YAML."); }
    }

    private static void ScanMapping(IParser parser, string file, string path, YamlMarks marks)
    {
        MappingStart start = (MappingStart)parser.Current!;
        CheckNode(start, file, path);
        marks.Add(path, start.Start, YamlNodeKind.Mapping);
        parser.MoveNext();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (parser.Current is not MappingEnd)
        {
            if (parser.Current is not Scalar key) throw YamlError(file, path, MarkOf(parser.Current), "Mapping keys must be scalars.");
            CheckNode(key, file, path);
            string childPath = path + "." + key.Value;
            if (!keys.Add(key.Value)) throw YamlError(file, childPath, key.Start, "Duplicate keys are not allowed.");
            parser.MoveNext();
            ScanNode(parser, file, childPath, marks);
        }
        parser.MoveNext();
    }

    private static void ScanNode(IParser parser, string file, string path, YamlMarks marks)
    {
        switch (parser.Current)
        {
            case MappingStart: ScanMapping(parser, file, path, marks); return;
            case Scalar scalar:
                CheckNode(scalar, file, path);
                marks.Add(path, scalar.Start, YamlNodeKind.Scalar, scalar); parser.MoveNext(); return;
            case SequenceStart sequence: throw YamlError(file, path, sequence.Start, "Sequences are not supported.");
            case AnchorAlias alias: throw YamlError(file, path, alias.Start, "YAML aliases are not supported.");
            default: throw YamlError(file, path, MarkOf(parser.Current), "Unsupported YAML construct.");
        }
    }

    private static void CheckNode(NodeEvent node, string file, string path)
    {
        if (!IsDefaultTag(node)) throw YamlError(file, path, node.Start, "Custom YAML tags are not supported.");
        if (!node.Anchor.IsEmpty) throw YamlError(file, path, node.Start, "YAML anchors and aliases are not supported.");
    }

    private static void ValidateYamlShape(string file, YamlMarks marks)
    {
        var mappings = new HashSet<string>(StringComparer.Ordinal)
        {
            "$.endpoint", "$.registration", "$.registration.credentials", "$.media", "$.runtime", "$.lib_rtic"
        };
        var integers = new HashSet<string>(StringComparer.Ordinal)
        {
            "$.endpoint.local_sip_port", "$.registration.max_reconnects", "$.registration.register_retry_interval_seconds"
        };
        var booleans = new HashSet<string>(StringComparer.Ordinal) { "$.runtime.headless", "$.runtime.verbose" };
        foreach ((string path, YamlMark node) in marks.Nodes)
        {
            if (mappings.Contains(path))
            {
                if (node.Kind != YamlNodeKind.Mapping) throw YamlError(file, path, node.Mark, "A mapping is required.");
                continue;
            }
            if (node.Kind != YamlNodeKind.Scalar) continue; // Unknown mapping is reported by the deserializer.
            if (integers.Contains(path) && (node.Scalar!.Style != ScalarStyle.Plain || !int.TryParse(node.Scalar.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
                throw YamlError(file, path, node.Mark, "An integer is required.");
            if (booleans.Contains(path) && (node.Scalar!.Style != ScalarStyle.Plain || !bool.TryParse(node.Scalar.Value, out _)))
                throw YamlError(file, path, node.Mark, "A boolean is required.");
            if (!integers.Contains(path) && !booleans.Contains(path) && !IsStringScalar(node.Scalar!))
                throw YamlError(file, path, node.Mark, "A string is required.");
        }
    }

    private static string RequireYamlFile(string path, string yamlPath, Mark? mark)
    {
        if (string.IsNullOrWhiteSpace(path)) throw YamlError(path, yamlPath, mark, "A YAML configuration path is required.");
        string fullPath;
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception) { throw YamlError(path, yamlPath, mark, "Configuration path is invalid."); }
        string extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".yml", StringComparison.OrdinalIgnoreCase))
            throw YamlError(fullPath, yamlPath, mark, "Configuration files must use .yaml or .yml.");
        if (!File.Exists(fullPath)) throw YamlError(fullPath, yamlPath, mark, "Configuration file was not found.");
        return fullPath;
    }

    private static string ResolveConfigPath(string configPath, string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Path.GetDirectoryName(configPath)!, path));
    private static string? GetOptionalConfigPath(string? value, string file, string path, YamlMarks marks)
    {
        if (value is null) return null;
        if (string.IsNullOrWhiteSpace(value)) throw YamlError(file, path, marks.At(path), "A non-empty path is required.");
        return ResolveConfigPath(file, value);
    }
    private static string RequireString(string? value, string file, string path, YamlMarks marks) => !string.IsNullOrWhiteSpace(value) ? value : throw YamlError(file, path, marks.At(path), "A non-empty value is required.");
    private static SIPProtocolsEnum ParseYamlTransport(string? value, string file, YamlMarks marks) => value?.Trim().ToLowerInvariant() switch { null => SIPProtocolsEnum.tls, "tls" => SIPProtocolsEnum.tls, "udp" => SIPProtocolsEnum.udp, _ => throw YamlError(file, "$.registration.transport", marks.At("$.registration.transport"), "Transport must be tls or udp.") };
    private static RtpPortRange ParseYamlRange(string value, string file, YamlMarks marks) { try { return RtpPortRange.Parse(value, "rtp_port_range"); } catch (ArgumentException) { throw YamlError(file, "$.media.rtp_port_range", marks.At("$.media.rtp_port_range"), "RTP port range is invalid."); } }
    private static bool IsStringScalar(Scalar scalar) => scalar.Style != ScalarStyle.Plain || !(scalar.Value is "~" or "null" or "Null" or "NULL" or "true" or "True" or "TRUE" or "false" or "False" or "FALSE") && !double.TryParse(scalar.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
    private static bool IsDefaultTag(NodeEvent node) => string.IsNullOrEmpty(node.Tag.ToString()) || node.Tag.ToString() == "?";
    private static Mark? MarkOf(ParsingEvent? value) => value?.Start;
    private static ArgumentException YamlError(string? file, string path, Mark? mark, string message) => new($"YAML configuration '{file ?? "configuration"}' {path} (line {(mark?.Line ?? 0) + 1}, column {(mark?.Column ?? 0) + 1}): {message}");

    private enum YamlNodeKind { Mapping, Scalar }
    private sealed record YamlMark(Mark Mark, YamlNodeKind Kind, Scalar? Scalar);
    private sealed class YamlMarks
    {
        public Dictionary<string, YamlMark> Nodes { get; } = new(StringComparer.Ordinal);
        public void Add(string path, Mark mark, YamlNodeKind kind, Scalar? scalar = null) => Nodes[path] = new(mark, kind, scalar);
        public Mark? At(string path) => Nodes.TryGetValue(path, out YamlMark? node) ? node.Mark : null;
        public string PathAt(Mark mark) => Nodes.FirstOrDefault(entry => entry.Value.Mark.Index == mark.Index).Key ?? "$";
    }

    // Bindings deliberately remain separate from the runtime endpoint configuration.
    private sealed class YamlEndpointDocument
    {
        public EndpointMapping? Endpoint { get; set; }
        public RegistrationMapping? Registration { get; set; }
        public MediaMapping? Media { get; set; }
        public RuntimeMapping? Runtime { get; set; }
        public LibRticMapping? LibRtic { get; set; }
        internal sealed class EndpointMapping { public int? LocalSipPort { get; set; } public string? ContactHost { get; set; } }
        internal sealed class RegistrationMapping { public string? Registrar { get; set; } public string? Transport { get; set; } public string? Username { get; set; } public string? Realm { get; set; } public string? OutboundProxy { get; set; } public int? MaxReconnects { get; set; } public int? RegisterRetryIntervalSeconds { get; set; } public CredentialsMapping? Credentials { get; set; } }
        internal sealed class CredentialsMapping { public string? Password { get; set; } public string? PasswordFile { get; set; } public string? DigestStoreFile { get; set; } }
        internal sealed class MediaMapping { public string? RtpPortRange { get; set; } public string? AnswerAudioFile { get; set; } }
        internal sealed class RuntimeMapping { public bool? Headless { get; set; } public bool? Verbose { get; set; } }
        internal sealed class LibRticMapping { public string? ApiConfigPath { get; set; } public string? SessionConfigPath { get; set; } }
    }
}
