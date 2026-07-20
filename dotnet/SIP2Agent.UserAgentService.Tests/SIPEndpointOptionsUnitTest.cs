using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class SIPEndpointOptionsUnitTest
{
    [Theory]
    [MemberData(nameof(CredentialConflicts))]
    public void ParseRejectsCredentialOptionConflicts(string[] args)
    {
        Assert.Throws<ArgumentException>(() => SIPEndpointOptions.Parse(args));
    }

    public static TheoryData<string[]> CredentialConflicts => new()
    {
        { new[] { "--passwd", "secret", "--password-file", "secret.txt" } },
        { new[] { "--passwd", "secret", "--digest-store", "credentials.txt" } },
        { new[] { "--password-file", "secret.txt", "--digest-store", "credentials.txt" } }
    };

    [Fact]
    public void ParseRtpPortRangeOption()
    {
        SIPEndpointOptions options = SIPEndpointOptions.Parse(["--rtp-port-range", "10000-10007"]);

        RtpPortRange range = Assert.IsType<RtpPortRange>(options.RtpPortRange);
        Assert.Equal(10000, range.StartPort);
        Assert.Equal(10007, range.EndPort);
    }

    [Theory]
    [InlineData("10001-10007")]
    [InlineData("10000-10006")]
    [InlineData("10000-10001")]
    [InlineData("not-a-range")]
    public void ParseRejectsInvalidRtpPortRange(string value)
    {
        Assert.Throws<ArgumentException>(() => SIPEndpointOptions.Parse(["--rtp-port-range", value]));
    }

    [Fact]
    public void ConfigLoadsRtpPortRangeFromEnvironment()
    {
        using var rtpPortRange = new EnvironmentVariableScope(S2AEnvVariables.RtpPortRange, "12000-12007");

        SIPEndpointConfig config = SIPEndpointConfig.Load(SIPEndpointOptions.Parse([]));

        RtpPortRange range = Assert.IsType<RtpPortRange>(config.RtpPortRange);
        Assert.Equal(12000, range.StartPort);
        Assert.Equal(12007, range.EndPort);
    }

    [Fact]
    public void ConfigLoadsPasswordAndPasswordFile()
    {
        string passwordFile = Path.GetTempFileName();
        try
        {
            SIPEndpointConfig passwordConfig = SIPEndpointConfig.Load(SIPEndpointOptions.Parse(
            [
                "--registrar", "pbx.example.com",
                "--username", "alice",
                "--passwd", "secret"
            ]));
            Assert.Equal("secret", Assert.Single(passwordConfig.AccountProfiles).Password);

            File.WriteAllText(passwordFile, $"secret{Environment.NewLine}");

            SIPEndpointConfig config = SIPEndpointConfig.Load(SIPEndpointOptions.Parse(
            [
                "--registrar", "pbx.example.com",
                "--username", "alice",
                "--password-file", passwordFile
            ]));

            SIPRegistrationProfile profile = Assert.Single(config.AccountProfiles);
            Assert.Equal("secret", profile.Password);
        }
        finally
        {
            File.Delete(passwordFile);
        }
    }

    [Fact]
    public void ConfigLoadsDigestStoreWithoutPassword()
    {
        string digestStoreFile = Path.GetTempFileName();
        try
        {
            SIPDigestStore.WriteToFile(digestStoreFile, "alice", "example.com", "secret");

            SIPEndpointConfig config = SIPEndpointConfig.Load(SIPEndpointOptions.Parse(
            [
                "--registrar", "pbx.example.com",
                "--username", "alice",
                "--digest-store", digestStoreFile
            ]));

            SIPRegistrationProfile profile = Assert.Single(config.AccountProfiles);
            Assert.Null(profile.Password);
            Assert.Equal("b1726872c344b6dc8365b774f8fd6412", profile.DigestStore?.HA1MD5);
            SIPEndpointConfig.Validate(config);
        }
        finally
        {
            File.Delete(digestStoreFile);
        }
    }

    [Fact]
    public void ConfigAllowsUnauthenticatedProfile()
    {
        var config = new SIPEndpointConfig
        {
            AccountProfiles =
            [
                new SIPRegistrationProfile(
                    "anonymous",
                    SIPSorcery.SIP.SIPURI.ParseSIPURI("sip:example.com"),
                    SIPSorcery.SIP.SIPProtocolsEnum.udp,
                    "alice",
                    null,
                    null,
                    null,
                    null)
            ]
        };

        SIPEndpointConfig.Validate(config);
    }

    [Fact]
    public void ConfigRejectsProfileWithPasswordAndDigestStore()
    {
        var config = new SIPEndpointConfig
        {
            AccountProfiles =
            [
                new SIPRegistrationProfile(
                    "conflict",
                    SIPSorcery.SIP.SIPURI.ParseSIPURI("sip:example.com"),
                    SIPSorcery.SIP.SIPProtocolsEnum.udp,
                    "alice",
                    "secret",
                    null,
                    null,
                    null,
                    DigestStore: new SIPDigestStore("0011", null))
            ]
        };

        Assert.Throws<ArgumentException>(() => SIPEndpointConfig.Validate(config));
    }

    [Theory]
    [MemberData(nameof(ConfigOptionErrors))]
    public void ConfigOptionMustBeExclusive(string[] args)
    {
        Assert.Throws<ArgumentException>(() => SIPEndpointOptions.Parse(args));
    }

    public static TheoryData<string[]> ConfigOptionErrors => new()
    {
        { new[] { "--config" } },
        { new[] { "--config", "agent.yaml", "--verbose" } },
        { new[] { "--verbose", "--config", "agent.yaml" } },
        { new[] { "-c", "agent.yaml", "-c", "other.yaml" } }
    };

    [Fact]
    public void ConfigOptionSelectsYamlMode()
    {
        SIPEndpointOptions options = SIPEndpointOptions.Parse(["-c", "agent.yaml"]);

        Assert.Equal("agent.yaml", options.ConfigFilePath);
    }

    [Fact]
    public void LoadYamlUsesOnlyYamlValuesAndResolvesConfigRelativePaths()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string passwordPath = Path.Combine(directory, "password.txt");
            string apiPath = Path.Combine(directory, "rtic-api.yml");
            string sessionPath = Path.Combine(directory, "rtic-session.yaml");
            File.WriteAllText(passwordPath, "secret");
            File.WriteAllText(apiPath, "this is deliberately opaque to SIP2Agent");
            File.WriteAllText(sessionPath, "also opaque");
            string yamlPath = Path.Combine(directory, "agent.yaml");
            File.WriteAllText(yamlPath, """
                endpoint:
                  local_sip_port: 0
                  contact_host: 203.0.113.10
                registration:
                  registrar: pbx.example.com
                  transport: tls
                  username: alice
                  max_reconnects: 5
                  register_retry_interval_seconds: 30
                  credentials:
                    password_file: password.txt
                media:
                  rtp_port_range: 10000-10019
                runtime:
                  headless: true
                  verbose: false
                lib_rtic:
                  api_config_path: rtic-api.yml
                  session_config_path: rtic-session.yaml
                """);
            using var ignoredEnvironment = new EnvironmentVariableScope(S2AEnvVariables.Registrar, "ignored.example.com");

            SIPEndpointConfig config = SIPEndpointConfig.Load(SIPEndpointOptions.Parse(["--config", yamlPath]));

            SIPRegistrationProfile profile = Assert.Single(config.AccountProfiles);
            Assert.Equal("alice", profile.Username);
            Assert.Equal("secret", profile.Password);
            Assert.True(config.Headless);
            Assert.False(config.Verbose);
            Assert.Equal(Path.GetFullPath(apiPath), config.LibRTICApiConfigPath);
            Assert.Equal(Path.GetFullPath(sessionPath), config.LibRTICSessionConfigPath);
            Assert.Equal(10000, config.RtpPortRange?.StartPort);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("unknown: value")]
    [InlineData("Endpoint: {}")]
    [InlineData("endpoint: []")]
    [InlineData("endpoint: {}\nendpoint: {}")]
    [InlineData("endpoint: &endpoint {}")]
    public void LoadYamlRejectsUnsupportedOrUnknownStructure(string yaml)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(path, yaml);
            Assert.Throws<ArgumentException>(() => SIPEndpointConfig.LoadYaml(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}
