using SIPSorcery.SIP;

namespace SIP2Agent.UserAgentService;

/// <summary>
/// Stores precomputed HTTP Digest HA1 values for SIP registration authentication.
/// </summary>
public sealed class SIPDigestStore
{
    public const string HA1MD5Key = "ha1_md5";

    public const string HA1SHA256Key = "ha1_sha256";

    public string? HA1MD5 { get; }

    public string? HA1SHA256 { get; }

    public SIPDigestStore(string? ha1MD5, string? ha1SHA256)
    {
        HA1MD5 = ha1MD5;
        HA1SHA256 = ha1SHA256;
    }

    public static SIPDigestStore NewFromPassword(string username, string realm, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(realm);
        ArgumentNullException.ThrowIfNull(password);

        return new SIPDigestStore(
            HTTPDigest.DigestCalcHA1(username, realm, password, DigestAlgorithmsEnum.MD5),
            HTTPDigest.DigestCalcHA1(username, realm, password, DigestAlgorithmsEnum.SHA256));
    }

    public static SIPDigestStore ReadFromFile(string path)
    {
        string? ha1MD5 = null;
        string? ha1SHA256 = null;

        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            int separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            if (!IsValidDigestValue(value))
            {
                continue;
            }

            if (key.Equals(HA1MD5Key, StringComparison.OrdinalIgnoreCase))
            {
                ha1MD5 = value;
            }
            else if (key.Equals(HA1SHA256Key, StringComparison.OrdinalIgnoreCase))
            {
                ha1SHA256 = value;
            }
        }

        return new SIPDigestStore(ha1MD5, ha1SHA256);
    }

    public static void WriteToFile(string path, string username, string realm, string password)
    {
        NewFromPassword(username, realm, password).Write(path);
    }

    public string? GetHA1Digest(string username, string realm, DigestAlgorithmsEnum digestAlgorithm)
    {
        _ = username;
        _ = realm;

        return digestAlgorithm switch
        {
            DigestAlgorithmsEnum.SHA256 => HA1SHA256,
            DigestAlgorithmsEnum.MD5 => HA1MD5,
            _ => null
        };
    }

    public void Write(string path)
    {
        string content =
            $"{HA1MD5Key}={HA1MD5}{Environment.NewLine}" +
            $"{HA1SHA256Key}={HA1SHA256}{Environment.NewLine}";

        File.WriteAllText(path, content);
    }

    private static bool IsValidDigestValue(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length % 2 != 0)
        {
            return false;
        }

        return value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
