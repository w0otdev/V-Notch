using System;
using System.Text.RegularExpressions;

namespace VNotch.Services;

public static class SensitiveDataScrubber
{
    private static readonly Regex SpDcRegex = new(
        @"(?i)(sp_dc=)[^\s;,\r\n""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GoogleApiKeyRegex = new(
        @"AIza[0-9A-Za-z\-_]{16,40}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DpapiRegex = new(
        @"enc:[A-Za-z0-9+/=]{16,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UrlSecretParamRegex = new(
        @"(?i)([?&](?:key|apikey|api_key|token|access_token|secret|client_secret)=)[^&\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BearerTokenRegex = new(
        @"(?i)(Bearer\s+)[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PasswordRegex = new(
        @"(?i)((?:password|passwd|pwd)\s*[:=]\s*)[^\s,;""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Scrub(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        string scrubbed = SpDcRegex.Replace(input, "$1[REDACTED]");
        scrubbed = GoogleApiKeyRegex.Replace(scrubbed, "AIza[REDACTED]");
        scrubbed = DpapiRegex.Replace(scrubbed, "enc:[REDACTED]");
        scrubbed = UrlSecretParamRegex.Replace(scrubbed, "$1[REDACTED]");
        scrubbed = BearerTokenRegex.Replace(scrubbed, "$1[REDACTED]");
        scrubbed = PasswordRegex.Replace(scrubbed, "$1[REDACTED]");

        return scrubbed;
    }
}
