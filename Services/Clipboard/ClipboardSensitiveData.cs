using System;
using System.Linq;
using System.Runtime.InteropServices;
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
internal static partial class ClipboardSensitiveData
{
    private const string NativeDll = "VNotch.Native.dll";
    internal const int DetectorVersion = 2;
    internal const string Mask = "••••••••";
    private const int MaxInspectionCharacters = 1_000_000;
    internal static string CreateLayoutMask(string text) => Mask;

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_sensitive_is_from_password_manager(
        ReadOnlySpan<char> appPtr,
        int appLen,
        ReadOnlySpan<char> exePtr,
        int exeLen,
        out byte outIsPm);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_sensitive_is_credential_value(
        ReadOnlySpan<char> valPtr,
        int valLen,
        out byte outIsCred);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_sensitive_luhn_check(
        ReadOnlySpan<char> matchPtr,
        int matchLen,
        out byte outValid);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_sensitive_analyze_standalone(
        ReadOnlySpan<char> textPtr,
        int textLen,
        byte isGuidOrEmailOrVersion,
        byte isIdentifier,
        out int outReason,
        out int outConfidence);

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
        ReadOnlySpan<char> span = (value ?? string.Empty).AsSpan();
        vn_clipboard_sensitive_is_credential_value(span, span.Length, out byte isCred);
        return isCred != 0;
    }

    private static bool IsFromPasswordManager(string app, string executable)
    {
        ReadOnlySpan<char> a = (app ?? string.Empty).AsSpan();
        ReadOnlySpan<char> e = (executable ?? string.Empty).AsSpan();
        vn_clipboard_sensitive_is_from_password_manager(a, a.Length, e, e.Length, out byte isPm);
        return isPm != 0;
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
            catch (Exception ex) when (ex is FormatException or JsonException) { /* Non-JWT candidate token; continue scanning. */ }
        }
        return false;
    }

    private static bool IsPaymentCard(string text)
    {
        foreach (Match match in Cards.Matches(text))
        {
            ReadOnlySpan<char> span = match.ValueSpan;
            vn_clipboard_sensitive_luhn_check(span, span.Length, out byte valid);
            if (valid != 0) return true;
        }
        return false;
    }

    private static ClipboardSensitivityResult AnalyzeStandalone(string text)
    {
        if (text.Length < 8 || text.Length > 256) return default;
        bool isGuidEmailVer = Guid.TryParse(text, out _) || Email.IsMatch(text) || Version.IsMatch(text);
        bool isIdent = !isGuidEmailVer && Identifier.IsMatch(text);
        ReadOnlySpan<char> span = text.AsSpan();
        vn_clipboard_sensitive_analyze_standalone(
            span,
            span.Length,
            isGuidEmailVer ? (byte)1 : (byte)0,
            isIdent ? (byte)1 : (byte)0,
            out int reason,
            out int confidence);
        return new((ClipboardSensitivityReason)reason, (ClipboardSensitivityConfidence)confidence);
    }
}
