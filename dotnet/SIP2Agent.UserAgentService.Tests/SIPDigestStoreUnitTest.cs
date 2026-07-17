using SIPSorcery.SIP;
using Xunit;

namespace SIP2Agent.UserAgentService.Tests;

public sealed class SIPDigestStoreUnitTest
{
    [Fact]
    public void NewFromPasswordCalculatesBothAlgorithms()
    {
        SIPDigestStore store = SIPDigestStore.NewFromPassword("alice", "example.com", "secret");

        Assert.Equal("b1726872c344b6dc8365b774f8fd6412", store.HA1MD5);
        Assert.Equal("ed8925b20f9a77b8f8f8d5f8e4467fe32b866f7208ab9e4b20595e9821a0fdee", store.HA1SHA256);
        Assert.Equal(store.HA1MD5, store.GetHA1Digest("ignored", "ignored", DigestAlgorithmsEnum.MD5));
        Assert.Equal(store.HA1SHA256, store.GetHA1Digest("ignored", "ignored", DigestAlgorithmsEnum.SHA256));
    }

    [Fact]
    public void ReadFromFileIsPermissiveAndAllowsNoRecognisedDigests()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, string.Join(Environment.NewLine,
            [
                "unknown=0011",
                "not-a-pair",
                "ha1_md5=ABCDEF",
                "ha1_md5=abc",
                "ha1_md5=0011",
                "HA1_MD5=aabb",
                "ha1_sha256=",
                "ha1_sha256=ccdd=",
                "HA1_SHA256=2233"
            ]));

            SIPDigestStore store = SIPDigestStore.ReadFromFile(path);

            Assert.Equal("aabb", store.HA1MD5);
            Assert.Equal("2233", store.HA1SHA256);

            File.WriteAllText(path, "unknown=value");

            store = SIPDigestStore.ReadFromFile(path);
            Assert.Null(store.HA1MD5);
            Assert.Null(store.HA1SHA256);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
