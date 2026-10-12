using System.IO;
using System.Text.RegularExpressions;

namespace VNotch.Services.Clipboard;

/// <summary>Local heuristics to identify passwords, secrets, tokens, and credentials for safe masking in File Tray.</summary>
internal static class ClipboardSensitiveData
{
    internal const string Mask = "••••••••";

    private static readonly Regex[] Secrets = new[]
    {
        // 1. Private Keys
        @"-----BEGIN (?:[A-Z0-9_-]+ )*PRIVATE KEY(?: BLOCK)?-----",

        // 2. JSON Web Tokens (JWT)
        @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}",

        // 3. Known Cloud & API Tokens (split to maintain small automata graphs)
        @"\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,})\b",
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16})\b",
        @"\b(?:AIza[0-9A-Za-z_-]{35}|xox[baprs]-[0-9A-Za-z]{10,48})\b",
        @"\b(?:[rs]k_(?:live|test)_[0-9a-zA-Z]{24,}|SG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43})\b",

        // 4. Discord Bot Token
        @"\b[MN][A-Za-z0-9]{23,}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,}\b",

        // 5. Bearer tokens
        @"\bBearer\s+[A-Za-z0-9._~+/-]{20,}\b",

        // 6. English password / credential labels
        """\b(?:password|passwd|pwd|passphrase|passcode|pass|pin|secret|api[_ -]?key|auth[_ -]?token|access[_ -]?token|token|wifi[_ -]?(?:password|pass|key)|db[_ -]?(?:password|pass)|mysql[_ -]?pwd|postgres[_ -]?password|otp|totp|2fa|verification[_ -]?code)["']?\s*(?:[:=]|->|=>|–|-|\bis\b)\s*["']?[^\s"']{3,}""",

        // 7. Vietnamese password / credential labels
        """\b(?:mật[_ ]?khẩu|mat[_ ]?khau|mật[_ ]?mã|mat[_ ]?ma|mã[_ ]?pin|ma[_ ]?pin|mã[_ ]?xác[_ ]?(?:nhận|thực)|ma[_ ]?xac[_ ]?(?:nhan|thuc)|mã[_ ]?otp|ma[_ ]?otp|mã[_ ]?bảo[_ ]?mật|ma[_ ]?bao[_ ]?mat|mk)["']?\s*(?:[:=]|->|=>|–|-|\blà\b|\bla\b)\s*["']?[^\s"']{3,}""",

        // 8. Credentials inside URLs (e.g. postgres://user:pass@host, mysql://..., redis://:pass@host)
        @"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^/\s:@]*:[^/\s:@]+@[^/\s:@]+",

        // 9. Connection strings (e.g. Server=...;Pwd=...; or Password=...)
        @"\b(?:Password|Pwd)\s*=\s*[^;\r\n\s]{3,}",

        // 10. Wi-Fi QR code strings with password
        @"WIFI:(?:[^;]*;)*P:[^;]{3,};",

        // 11. Standalone OTP / 2FA digits & codes
        @"^\s*[0-9]{4,8}\s*$",
        @"^\s*[0-9]{3}[ -][0-9]{3}\s*$",
        @"^\s*[Gg]-[0-9]{6}\s*$",

        // 12. Verification code in notification or SMS sentences
        @"\b(?:otp|mã otp|ma otp|mã xác nhận|ma xac nhan|mã xác thực|ma xac thuc|verification code|security code)\b[^\r\n]{0,35}?\b[0-9]{4,8}\b",

        // 13. Passphrase / Diceware (3 to 6 words joined by hyphens/underscores)
        @"^\s*[a-zA-Z]{3,12}(?:[-_][a-zA-Z0-9]{3,12}){3,6}\s*$",

        // 14. Email:password format
        @"^[\w.+-]+@[\w-]+\.[\w.-]+:\S{6,}$"
    }.Select(pattern => new Regex(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100))).ToArray();

    private static readonly Regex Cards = new(@"\b[0-9](?:[ -]?[0-9]){12,18}\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    private static readonly string[] PasswordManagerProcesses =
    [
        "1password", "bitwarden", "keepass", "keepassxc", "lastpass",
        "enpass", "dashlane", "nordpass", "protonpass", "proton pass",
        "roboform", "authpass", "keeper", "safeincloud"
    ];

    internal static bool IsSensitive(string text) => IsSensitive(text, string.Empty, string.Empty);

    internal static bool IsSensitive(string text, string sourceApp, string sourceExecutable)
    {
        if (string.IsNullOrEmpty(text)) return false;

        // 1. If text was copied directly from a password manager, it is sensitive.
        if (IsFromPasswordManager(sourceApp, sourceExecutable) && !string.IsNullOrWhiteSpace(text) && text.Trim().Length <= 2048)
            return true;

        try
        {
            // 2. Match known secret patterns (tokens, keys, labels, connection strings, OTPs).
            if (Secrets.Any(pattern => pattern.IsMatch(text))) return true;

            // 3. Standalone / unlabelled password heuristics.
            if (IsStandalonePassword(text)) return true;

            // 4. Credit card Luhn validation.
            foreach (Match match in Cards.Matches(text))
            {
                var digits = match.Value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
                if (digits.All(d => d == digits[0])) continue;
                int sum = 0;
                for (int i = digits.Length - 1, position = 0; i >= 0; i--, position++)
                {
                    int n = digits[i];
                    if (position % 2 == 1) { n *= 2; if (n > 9) n -= 9; }
                    sum += n;
                }
                if (sum % 10 == 0) return true;
            }

            return false;
        }
        catch (RegexMatchTimeoutException) { return true; } // Never expose text we could not inspect.
    }

    private static bool IsFromPasswordManager(string sourceApp, string sourceExecutable)
    {
        if (!string.IsNullOrEmpty(sourceApp))
        {
            foreach (var pm in PasswordManagerProcesses)
            {
                if (sourceApp.Contains(pm, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        if (!string.IsNullOrEmpty(sourceExecutable))
        {
            string fileName = Path.GetFileNameWithoutExtension(sourceExecutable);
            foreach (var pm in PasswordManagerProcesses)
            {
                if (fileName.Contains(pm, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private static bool IsStandalonePassword(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length < 8 || trimmed.Length > 128) return false;
        if (trimmed.Contains('\r') || trimmed.Contains('\n') || trimmed.Contains('\t') || trimmed.Contains(' ')) return false;

        // Reject common non-password patterns:
        // URLs
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("://", StringComparison.Ordinal))
            return false;

        // Paths and files
        if (trimmed.StartsWith("C:\\", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("D:\\", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("\\\\", StringComparison.Ordinal) ||
            trimmed.StartsWith('/') ||
            trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return false;

        // GUID / UUID
        if (Guid.TryParse(trimmed, out _)) return false;

        // Color hex codes (#FFFFFF, #101014)
        if (trimmed.StartsWith('#') && (trimmed.Length == 4 || trimmed.Length == 7 || trimmed.Length == 9))
        {
            bool allHex = true;
            for (int i = 1; i < trimmed.Length; i++)
            {
                if (!char.IsAsciiHexDigit(trimmed[i])) { allHex = false; break; }
            }
            if (allHex) return false;
        }

        bool hasLower = false;
        bool hasUpper = false;
        bool hasDigit = false;
        bool hasSpecial = false;
        int nonAsciiCount = 0;

        foreach (char c in trimmed)
        {
            if (char.IsAsciiLetterLower(c)) hasLower = true;
            else if (char.IsAsciiLetterUpper(c)) hasUpper = true;
            else if (char.IsAsciiDigit(c)) hasDigit = true;
            else if (IsPasswordSpecialChar(c)) hasSpecial = true;
            else if (!char.IsAscii(c)) nonAsciiCount++;
        }

        if (nonAsciiCount > 0) return false;

        int classCount = (hasLower ? 1 : 0) + (hasUpper ? 1 : 0) + (hasDigit ? 1 : 0) + (hasSpecial ? 1 : 0);

        // Rule 1: All 4 character classes present (Lowercase + Uppercase + Digit + Special Symbol).
        // e.g. "P@ssw0rd123!", "Admin@2024", "kP9#vL2!xQ@1"
        if (classCount == 4) return true;

        // Rule 2: 3 character classes present, including strong special symbols (@, #, $, %, !, ^, &, *, ?, ~, +, =)
        // e.g. "admin#123456", "secret@pass", "MY_PASS!123"
        if (hasSpecial && classCount >= 3)
        {
            foreach (char c in trimmed)
            {
                if (c is '@' or '#' or '$' or '%' or '^' or '&' or '*' or '!' or '?' or '~' or '+' or '=')
                    return true;
            }
        }

        // Rule 3: High-entropy alphanumeric string (e.g. generated passwords without symbols like "8xK9mP2aQ0zL")
        if (hasLower && hasUpper && hasDigit && !hasSpecial && trimmed.Length >= 10)
        {
            bool isHex = true;
            foreach (char c in trimmed)
            {
                if (!char.IsAsciiHexDigit(c)) { isHex = false; break; }
            }
            if (isHex && (trimmed.Length == 32 || trimmed.Length == 40 || trimmed.Length == 64)) return false;

            int transitions = 0;
            int prevCategory = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                int cat = char.IsAsciiLetterLower(c) ? 0 : char.IsAsciiLetterUpper(c) ? 1 : 2;
                if (prevCategory != -1 && prevCategory != cat) transitions++;
                prevCategory = cat;
            }

            if (transitions >= 4 && CalculateShannonEntropy(trimmed) >= 2.9)
                return true;
        }

        return false;
    }

    private static bool IsPasswordSpecialChar(char c) =>
        c is '!' or '@' or '#' or '$' or '%' or '^' or '&' or '*' or '(' or ')'
          or '_' or '+' or '-' or '=' or '[' or ']' or '{' or '}' or '|' or '\\'
          or ';' or ':' or '\'' or '"' or ',' or '<' or '.' or '>' or '/' or '?'
          or '~' or '`';

    private static double CalculateShannonEntropy(string s)
    {
        Span<int> freq = stackalloc int[128];
        foreach (char c in s)
        {
            if (c < 128) freq[c]++;
        }
        double entropy = 0;
        double len = s.Length;
        for (int i = 0; i < 128; i++)
        {
            if (freq[i] > 0)
            {
                double p = freq[i] / len;
                entropy -= p * Math.Log2(p);
            }
        }
        return entropy;
    }
}
