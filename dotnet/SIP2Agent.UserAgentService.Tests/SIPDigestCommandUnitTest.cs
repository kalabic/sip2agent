using SIP2Agent.AgentCli;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class SIPDigestCommandUnitTest
{
    [Fact]
    public void CommandCreatesAndOverwritesDigestStoreFromPassword()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "old-content");
            var output = new StringWriter();
            var error = new StringWriter();

            int result = SIPDigestCommand.Run(
                ["--username", "alice", "--realm", "example.com", "--passwd", "secret", "--out", path],
                output,
                error);

            Assert.Equal(0, result);
            Assert.Empty(error.ToString());
            Assert.DoesNotContain("old-content", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Equal("b1726872c344b6dc8365b774f8fd6412", SIPDigestStore.ReadFromFile(path).HA1MD5);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void CommandRejectsInvalidArguments(string[] args)
    {
        var error = new StringWriter();

        int result = SIPDigestCommand.Run(args, TextWriter.Null, error);

        Assert.Equal(1, result);
        Assert.NotEmpty(error.ToString());
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        { new[] { "--realm", "example.com", "--passwd", "secret", "--out", "store.txt" } },
        { new[] { "--username", "alice", "--passwd", "secret", "--out", "store.txt" } },
        { new[] { "--username", "alice", "--realm", "example.com", "--passwd", "secret" } },
        { new[] { "--username", "alice", "--realm", "example.com", "--out", "store.txt" } },
        { new[] { "--username", "alice", "--realm", "example.com", "--passwd", "one", "--password-file", "two", "--out", "store.txt" } }
    };
}
