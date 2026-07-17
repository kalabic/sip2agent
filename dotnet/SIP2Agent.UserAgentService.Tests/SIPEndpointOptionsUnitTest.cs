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
