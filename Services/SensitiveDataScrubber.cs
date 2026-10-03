using System.Text.RegularExpressions;

namespace VNotch.Services;

public static partial class SensitiveDataScrubber
{
    private const int MatchTimeoutMilliseconds = 100;

    // Match whole container values before tokens embedded inside them.
    // The boundary prevents quadratic retries over long API-key identifiers.
    [GeneratedRegex(
        """
        (?<prefix>(?i:sp_dc=))[^\s;,\r\n"]+
        |(?<prefix>(?i:\b(?:[a-z0-9_-]*api[_-]?key|x-goog-api-key)["']?\s*[:=]\s*["']?))[^&\s,"';}\]]+
        |(?<prefix>(?i:[?&](?:key|apikey|api_key|token|access_token|secret|client_secret)=))[^&\s"']+
        |(?<prefix>(?i:Bearer\s+))[A-Za-z0-9\-._~+/]+=*
        |(?<prefix>(?i:(?:password|passwd|pwd)\s*[:=]\s*))[^\s,;"]+
        |\bsk-[A-Za-z0-9_-]{8,}
        |(?<prefix>AIza)[0-9A-Za-z\-_]{16,40}
        |(?<prefix>enc:)[A-Za-z0-9+/=]{16,}
        """,
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace,
        MatchTimeoutMilliseconds)]
    private static partial Regex SecretRegex();

    public static string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        try
        {
            return SecretRegex().Replace(input, "${prefix}[REDACTED]");
        }
        catch (RegexMatchTimeoutException)
        {
            // Never return raw input or log the exception (it contains Input).
            return "[REDACTED: log entry exceeded scrubbing time limit]";
        }
    }
}
