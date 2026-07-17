namespace SIP2Agent.UserAgentService;

public static class PasswordFile
{
    public static string Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A password file path is required.", nameof(path));
        }

        try
        {
            string password = File.ReadAllText(path);
            return password.EndsWith("\r\n", StringComparison.Ordinal)
                ? password[..^2]
                : password.EndsWith('\n')
                    ? password[..^1]
                    : password;
        }
        catch (Exception excp) when (excp is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException($"Password file '{path}' could not be read.", nameof(path), excp);
        }
    }
}
