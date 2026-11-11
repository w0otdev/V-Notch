using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

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

    private static readonly FrozenSet<string> s_secretExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".kdbx", ".env", ".ppk", ".ovpn"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> s_secretFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "id_rsa", "id_ed25519", "id_ecdsa", "id_dsa", ".npmrc", ".netrc", "credentials", ".env"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static string CreateLayoutMask(string text) => string.IsNullOrEmpty(text) ? string.Empty : Mask;

    internal static bool IsSensitiveFileName(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return false;
        string fileName = Path.GetFileName(pathOrName);
        if (fileName.Length == 0) return false;
        return s_secretFileNames.Contains(fileName) || s_secretExtensions.Contains(Path.GetExtension(fileName));
    }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_sensitive_analyze(
        ReadOnlySpan<char> textPtr,
        int textLen,
        ReadOnlySpan<char> appPtr,
        int appLen,
        ReadOnlySpan<char> exePtr,
        int exeLen,
        out int outReason,
        out int outConfidence);

    internal static bool IsSensitive(string text) => Analyze(text).ShouldMask;
    internal static bool IsSensitive(string text, string sourceApp, string sourceExecutable) =>
        Analyze(text, sourceApp, sourceExecutable).ShouldMask;

    internal static ClipboardSensitivityResult Analyze(string text, string sourceApp = "", string sourceExecutable = "")
    {
        if (text == null && string.IsNullOrEmpty(sourceApp) && string.IsNullOrEmpty(sourceExecutable)) return default;
        ReadOnlySpan<char> t = (text ?? string.Empty).AsSpan();
        ReadOnlySpan<char> a = (sourceApp ?? string.Empty).AsSpan();
        ReadOnlySpan<char> e = (sourceExecutable ?? string.Empty).AsSpan();
        if (vn_clipboard_sensitive_analyze(t, t.Length, a, a.Length, e, e.Length, out int reason, out int confidence) != 0)
            return new(ClipboardSensitivityReason.InspectionLimit, ClipboardSensitivityConfidence.High);
        return new((ClipboardSensitivityReason)reason, (ClipboardSensitivityConfidence)confidence);
    }
}
