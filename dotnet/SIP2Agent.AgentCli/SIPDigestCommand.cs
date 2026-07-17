using SIP2Agent.UserAgentService;

namespace SIP2Agent.AgentCli;

internal static class SIPDigestCommand
{
    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string? username = null;
        string? realm = null;
        string? passwd = null;
        string? passwordFile = null;
        string? outPath = null;

        try
        {
            for (int index = 0; index < args.Length; index++)
            {
                string arg = args[index];
                switch (arg)
                {
                    case "--username":
                        username = ReadValue(args, ref index, arg);
                        break;
                    case "--realm":
                        realm = ReadValue(args, ref index, arg);
                        break;
                    case "--passwd":
                        passwd = ReadValue(args, ref index, arg);
                        break;
                    case "--password-file":
                        passwordFile = ReadValue(args, ref index, arg);
                        break;
                    case "--out":
                        outPath = ReadValue(args, ref index, arg);
                        break;
                    case "--help":
                    case "-h":
                        PrintUsage(output);
                        return 0;
                    default:
                        throw new ArgumentException($"Unknown digest option '{arg}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(username))
            {
                throw new ArgumentException("Option '--username' is required.");
            }

            if (string.IsNullOrWhiteSpace(realm))
            {
                throw new ArgumentException("Option '--realm' is required.");
            }

            if (string.IsNullOrWhiteSpace(outPath))
            {
                throw new ArgumentException("Option '--out' is required.");
            }

            if ((passwd == null) == (passwordFile == null))
            {
                throw new ArgumentException("Exactly one of '--passwd' or '--password-file' is required.");
            }

            string password = passwordFile != null ? PasswordFile.Read(passwordFile) : passwd!;
            SIPDigestStore.WriteToFile(outPath, username, realm, password);
            output.WriteLine($"Wrote SIP digest store '{outPath}'.");
            return 0;
        }
        catch (Exception excp) when (excp is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error.WriteLine(excp.Message);
            return 1;
        }
    }

    internal static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("Usage: (Windows '.exe' example)");
        writer.WriteLine("  SIP2Agent.AgentCli.exe digest --username <value> --realm <value> (--passwd <value> | --password-file <path>) --out <path>");
        writer.WriteLine();
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }

        return args[++index];
    }
}
