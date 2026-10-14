using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VNotch.Services.Clipboard;

internal enum ClipboardSensitivityReason
{
    None, PasswordManager, CredentialFormat, CredentialLabel, VerificationCode,
    PaymentCard, StandalonePassword, Ambiguous, InspectionLimit
}

internal enum ClipboardSensitivityConfidence { None, Possible, Likely, High }

// Contains no source text, matched values, or offsets that could leak credentials into logs.
internal readonly record struct ClipboardSensitivityResult(
    ClipboardSensitivityReason Reason, ClipboardSensitivityConfidence Confidence)
{
    internal bool ShouldMask => Confidence is ClipboardSensitivityConfidence.Likely or ClipboardSensitivityConfidence.High;
}

/// <summary>Evidence-based clipboard classification, not a password-strength estimator.</summary>
internal static class ClipboardSensitiveData
{
    internal const int DetectorVersion = 2;
    internal const string Mask = "••••••••";
    private const int MaxInspectionCharacters = 1_000_000;
    internal static string CreateLayoutMask(string text) => Mask;

    private static Regex Pattern(string pattern) => new(pattern,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex[] CredentialFormats = new[]
    {
        // 1. Private Keys
        @"-----BEGIN (?:[A-Z0-9_-]+ )*PRIVATE KEY(?: BLOCK)?-----",

        // 3. Known Cloud & API Tokens (split to maintain small automata graphs)
        @"\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,})\b",
        @"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16})\b",
        @"\b(?:AIza[0-9A-Za-z_-]{35}|xox[baprs]-[0-9A-Za-z]{10,48})\b",
        @"\b(?:[rs]k_(?:live|test)_[0-9a-zA-Z]{24,}|SG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43})\b",

        // 4. Discord Bot Token
        @"\b[MN][A-Za-z0-9]{23,}\.[A-Za-z0-9_-]{6}\.[A-Za-z0-9_-]{27,}\b",

        // 5. Bearer tokens
        @"\bBearer\s+[A-Za-z0-9._~+/-]{20,}\b",

        @"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^/\s:@]*:[^/\s:@]+@[^/\s:@]+",
        @"WIFI:(?:[^;]*;)*P:[^;]+;",
        @"^[\w.+-]+@[\w-]+\.[\w.-]+:\S+$"
    }.Select(Pattern).ToArray();

    // Require a complete label and a value. Hyphens in documentation/slugs are not assignment operators.
    private const string LabelValue = """["']?\s*(?:=>|->|[:=]|\bis\b|\blà\b|\bla\b)\s*(?:"(?<value>[^"\r\n]+)"|'(?<value>[^'\r\n]+)'|(?<value>[^\s"';,}<>]+))""";
    private static readonly Regex[] Labels = new[]
    {
        @"\b(?:(?:client|db|mysql|postgres|wifi)[_ -]?)?(?:password|passwd|pwd|passphrase|passcode|pass|pin|secret)",
        @"\b(?:api[_ -]?key|auth[_ -]?token|access[_ -]?token|refresh[_ -]?token|token|otp|totp|2fa|verification[_ -]?code)",
        @"\b(?:mật[_ ]?khẩu|mat[_ ]?khau|mật[_ ]?mã|mat[_ ]?ma|mã[_ ]?pin|ma[_ ]?pin|mk)",
        @"\b(?:mã[_ ]?xác[_ ]?(?:nhận|thực)|ma[_ ]?xac[_ ]?(?:nhan|thuc)|mã[_ ]?otp|ma[_ ]?otp|mã[_ ]?bảo[_ ]?mật|ma[_ ]?bao[_ ]?mat)"
    }.Select(label => Pattern(label + LabelValue)).ToArray();
    private static readonly Regex Jwt = Pattern(@"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b");
    private static readonly Regex ContextualCode = Pattern(
        @"\b(?:otp|mã otp|ma otp|mã xác nhận|ma xac nhan|mã xác thực|ma xac thuc|verification code|security code)\b[^\r\n]{0,35}?\b[0-9]{4,8}\b");
    private static readonly Regex StandaloneCode = Pattern(@"^(?:[0-9]{6,8}|[0-9]{3}[ -][0-9]{3}|G-[0-9]{6})$");
    private static readonly Regex Cards = Pattern(@"\b[0-9](?:[ -]?[0-9]){12,18}\b");
    private static readonly Regex Email = Pattern(@"^[\w.+-]+@[\w-]+(?:\.[\w-]+)+$");
    private static readonly Regex Version = Pattern(@"^v?\d+(?:\.\d+){1,3}(?:[-+][\w.-]+)?$");
    private static readonly Regex Identifier = Pattern(@"^[\p{L}_][\p{L}\p{N}_]*$");
    private static readonly HashSet<string> NonValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "null", "undefined", "true", "false", "none", "required", "optional", "missing", "invalid",
        "incorrect", "expired", "string", "redacted",
        "your_password", "your_api_key"
    };
    private static readonly HashSet<string> PasswordManagers = new(StringComparer.OrdinalIgnoreCase)
    {
        "1password", "bitwarden", "keepass", "keepassxc", "lastpass", "enpass", "dashlane",
        "nordpass", "protonpass", "proton pass", "roboform", "authpass", "keeper", "safeincloud"
    };
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".json", ".png", ".jpg", ".jpeg", ".exe", ".dll", ".md", ".html", ".txt",
        ".ts", ".tsx", ".js", ".jsx", ".py", ".zip", ".pdf", ".docx", ".xlsx", ".csv", ".xml",
        ".yaml", ".yml", ".log", ".sql", ".css", ".svg", ".mp4", ".mp3"
    };

    internal static bool IsSensitive(string text) => Analyze(text).ShouldMask;
    internal static bool IsSensitive(string text, string sourceApp, string sourceExecutable) =>
        Analyze(text, sourceApp, sourceExecutable).ShouldMask;

    internal static ClipboardSensitivityResult Analyze(string text, string sourceApp = "", string sourceExecutable = "")
    {
        if (string.IsNullOrWhiteSpace(text)) return default;
        if (IsFromPasswordManager(sourceApp, sourceExecutable))
            return High(ClipboardSensitivityReason.PasswordManager);
        // Never sample/truncate and silently miss a secret beyond the inspected prefix.
        if (text.Length > MaxInspectionCharacters) return High(ClipboardSensitivityReason.InspectionLimit);
        try
        {
            string normalized = text.IsNormalized(NormalizationForm.FormC) ? text : text.Normalize(NormalizationForm.FormC);
            if (ContainsJwt(normalized)) return High(ClipboardSensitivityReason.CredentialFormat);
            foreach (var format in CredentialFormats)
                if (format.IsMatch(normalized)) return High(ClipboardSensitivityReason.CredentialFormat);
            foreach (var label in Labels)
                foreach (Match match in label.Matches(normalized))
                    if (IsCredentialValue(match.Groups["value"].Value)) return High(ClipboardSensitivityReason.CredentialLabel);
            if (ContextualCode.IsMatch(normalized)) return High(ClipboardSensitivityReason.VerificationCode);
            if (IsPaymentCard(normalized)) return High(ClipboardSensitivityReason.PaymentCard);
            string candidate = normalized.Trim();
            if (StandaloneCode.IsMatch(candidate))
                return new(ClipboardSensitivityReason.VerificationCode, ClipboardSensitivityConfidence.Likely);
            return AnalyzeStandalone(candidate);
        }
        catch (RegexMatchTimeoutException) { return High(ClipboardSensitivityReason.InspectionLimit); }
        catch (ArgumentException) { return High(ClipboardSensitivityReason.InspectionLimit); } // Invalid Unicode must not bypass inspection.
    }

    private static ClipboardSensitivityResult High(ClipboardSensitivityReason reason) => new(reason, ClipboardSensitivityConfidence.High);

    private static bool IsCredentialValue(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || NonValues.Contains(value)) return false;
        if (value.All(c => c is '*' or '•' or '●')) return false;
        if (value.StartsWith("${", StringComparison.Ordinal) || value.StartsWith("{{", StringComparison.Ordinal)
            || value.StartsWith("process.env.", StringComparison.Ordinal) || value.StartsWith("os.environ", StringComparison.Ordinal)
            || value.StartsWith("Environment.", StringComparison.Ordinal)) return false;
        return true;
    }

    private static bool IsFromPasswordManager(string app, string executable)
    {
        // An executable is stronger identity evidence than a friendly window/application label.
        if (!string.IsNullOrWhiteSpace(executable))
            return PasswordManagers.Contains(Path.GetFileNameWithoutExtension(executable));
        return PasswordManagers.Contains(app.Trim());
    }

    private static bool ContainsJwt(string text)
    {
        foreach (Match match in Jwt.Matches(text))
        {
            // Bound decoding allocations independently of the complete clipboard payload.
            if (match.Length > 16_384) continue;
            string[] parts = match.Value.Split('.');
            try
            {
                static byte[] Decode(string part) => Convert.FromBase64String(
                    part.Replace('-', '+').Replace('_', '/').PadRight((part.Length + 3) / 4 * 4, '='));
                using var header = JsonDocument.Parse(Decode(parts[0]), new JsonDocumentOptions { MaxDepth = 16 });
                using var payload = JsonDocument.Parse(Decode(parts[1]), new JsonDocumentOptions { MaxDepth = 16 });
                if (header.RootElement.ValueKind == JsonValueKind.Object && payload.RootElement.ValueKind == JsonValueKind.Object
                    && header.RootElement.TryGetProperty("alg", out var algorithm) && algorithm.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(algorithm.GetString()) && Decode(parts[2]).Length > 0) return true;
            }
            catch (Exception ex) when (ex is FormatException or JsonException) { }
        }
        return false;
    }

    private static bool IsPaymentCard(string text)
    {
        Span<int> digits = stackalloc int[19];
        foreach (Match match in Cards.Matches(text))
        {
            int count = 0;
            foreach (char c in match.ValueSpan) if (char.IsAsciiDigit(c)) digits[count++] = c - '0';
            bool repeated = true;
            int sum = 0;
            for (int i = count - 1, position = 0; i >= 0; i--, position++)
            {
                repeated &= digits[i] == digits[0];
                int n = digits[i];
                if (position % 2 == 1) { n *= 2; if (n > 9) n -= 9; }
                sum += n;
            }
            if (!repeated && sum % 10 == 0) return true;
        }
        return false;
    }

    private static ClipboardSensitivityResult AnalyzeStandalone(string text)
    {
        if (text.Length < 8 || text.Length > 256 || text.Any(char.IsWhiteSpace)) return default;
        // Exclusions apply only to weak standalone evidence, never to labelled/structured credentials.
        if (Guid.TryParse(text, out _) || Email.IsMatch(text) || Version.IsMatch(text)) return default;
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith('/') || text.StartsWith("\\", StringComparison.Ordinal)
            || text.StartsWith("~/", StringComparison.Ordinal) || text.StartsWith("./", StringComparison.Ordinal)
            || text.StartsWith("../", StringComparison.Ordinal)
            || (text.Length > 2 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/')) return default;
        if (FileExtensions.Contains(Path.GetExtension(text))) return default;
        if ((text.StartsWith('#') && text.Length is 4 or 7 or 9 && text.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") == false)
            || (text.Length is 32 or 40 or 64 && text.All(char.IsAsciiHexDigit))) return default;
        // Common code expressions are not passwords merely because they mix case, numbers and punctuation.
        if (text.Contains("=>", StringComparison.Ordinal) || text.Contains("==", StringComparison.Ordinal)
            || text.Contains("!=", StringComparison.Ordinal) || text.Contains("::", StringComparison.Ordinal)
            || (text.IndexOf('=') is int assignment && assignment >= 0 && assignment < text.Length - 2)
            || text.Contains('(') || text.Contains(')') || text.Contains('{') || text.Contains('}') || text.Contains(';'))
            return new(ClipboardSensitivityReason.Ambiguous, ClipboardSensitivityConfidence.Possible);

        int lower = 0, upper = 0, digits = 0, symbols = 0, transitions = 0, previous = -1;
        bool strongSymbol = false;
        var frequencies = new Dictionary<char, int>();
        foreach (char c in text)
        {
            int category;
            if (char.IsLower(c)) { lower++; category = 0; }
            else if (char.IsUpper(c)) { upper++; category = 1; }
            else if (char.IsDigit(c)) { digits++; category = 2; }
            else if (char.IsLetter(c)) { lower++; category = 0; }
            else { symbols++; category = 3; strongSymbol |= "@#$%!^&*?~+=".Contains(c); }
            if (previous >= 0 && previous != category) transitions++;
            previous = category;
            frequencies[c] = frequencies.GetValueOrDefault(c) + 1;
        }
        double entropy = 0;
        foreach (int count in frequencies.Values)
        {
            double probability = (double)count / text.Length;
            entropy -= probability * Math.Log2(probability);
        }
        // Mixed symbols plus letters/numbers are useful evidence even for weak human passwords.
        bool decorated = strongSymbol && lower + upper > 0 && (digits > 0 || lower > 0 && upper > 0);
        // Entropy alone also scores identifiers highly: require frequent class changes and multiple digits/cases.
        bool generated = symbols == 0 && lower >= 2 && upper >= 2 && digits >= 2 && text.Length >= 10
            && transitions >= 5 && (double)transitions / (text.Length - 1) >= 0.45 && entropy >= 3.0;
        if (decorated || generated)
            return new(ClipboardSensitivityReason.StandalonePassword, ClipboardSensitivityConfidence.Likely);
        if (symbols > 0 || digits > 0 || Identifier.IsMatch(text))
            return new(ClipboardSensitivityReason.Ambiguous, ClipboardSensitivityConfidence.Possible);
        return default;
    }
}
