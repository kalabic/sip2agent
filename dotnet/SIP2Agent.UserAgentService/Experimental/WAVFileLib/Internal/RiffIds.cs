using System.Text;

namespace SIP2Agent.UserAgentService.Experimental.WAVFileLib.Internal;

[Experimental("S2AWAV001")]
internal static class RiffIds
{
    public const string Riff = "RIFF";
    public const string Wave = "WAVE";
    public const string Fmt = "fmt ";
    public const string Data = "data";
    public const string Fact = "fact";
    public const string List = "LIST";
    public const string Info = "INFO";
    public const string Isft = "ISFT";
    public const string Wavl = "wavl";
    public const string Silence = "slnt";

    public static bool EqualsId(ReadOnlySpan<byte> fourCc, string id)
    {
        if (fourCc.Length < 4 || id.Length != 4)
        {
            return false;
        }

        return fourCc[0] == (byte)id[0]
               && fourCc[1] == (byte)id[1]
               && fourCc[2] == (byte)id[2]
               && fourCc[3] == (byte)id[3];
    }

    public static string ToIdString(ReadOnlySpan<byte> fourCc)
    {
        if (fourCc.Length < 4)
        {
            return Encoding.ASCII.GetString(fourCc);
        }

        return Encoding.ASCII.GetString(fourCc[..4]);
    }

    public static void WriteId(Span<byte> dest, string id)
    {
        if (id.Length != 4)
        {
            throw new ArgumentException("RIFF identifiers must be exactly 4 characters.", nameof(id));
        }

        dest[0] = (byte)id[0];
        dest[1] = (byte)id[1];
        dest[2] = (byte)id[2];
        dest[3] = (byte)id[3];
    }
}
