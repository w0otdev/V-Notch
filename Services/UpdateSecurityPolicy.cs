using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace VNotch.Services;

public sealed partial class UpdateSecurityPolicy
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_update_security_policy_evaluate_allowlist(
        int publisherCount,
        int thumbprintCount,
        out int outState);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_update_security_policy_normalize_thumbprint(
        ReadOnlySpan<char> value,
        int valueLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    public const long MaximumInstallerBytes = 500L * 1024 * 1024;

    private readonly IReadOnlySet<string> _allowedPublisherNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlySet<string> _allowedCertificateThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> AllowedPublisherNames
    {
        get => _allowedPublisherNames;
        init => _allowedPublisherNames = value?
            .Select(s => s?.Trim() ?? string.Empty)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> AllowedCertificateThumbprints
    {
        get => _allowedCertificateThumbprints;
        init => _allowedCertificateThumbprints = value?
            .Select(NormalizeThumbprint)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    public static UpdateSecurityPolicy FromEnvironment() => new()
    {
        AllowedPublisherNames = ParsePublishers("VNOTCH_UPDATER_ALLOWED_PUBLISHERS"),
        AllowedCertificateThumbprints = ParseThumbprints("VNOTCH_UPDATER_ALLOWED_THUMBPRINTS")
    };

    public bool IsTrustedSignature(string installerPath, out string reason)
    {
        int state = vn_update_security_policy_evaluate_allowlist(
            AllowedPublisherNames.Count,
            AllowedCertificateThumbprints.Count,
            out int outState) == 0
            ? outState
            : ResolveFallbackPolicyState();

        int ResolveFallbackPolicyState()
        {
            if (AllowedPublisherNames.Count == 0 && AllowedCertificateThumbprints.Count == 0) return 0;
            return AllowedPublisherNames.Count == 0 || AllowedCertificateThumbprints.Count == 0 ? 1 : 2;
        }

        if (state == 0)
        {
            reason = "No additional Authenticode policy configured; signed manifest verification is still required.";
            return true;
        }

        if (state == 1)
        {
            reason = "Authenticode allowlist is incomplete (needs both publisher and thumbprint).";
            return false;
        }

        try
        {
#pragma warning disable SYSLIB0057 // Required API: extracts the embedded Authenticode signer certificate.
            if (!HasValidAuthenticodeSignature(installerPath))
            {
                reason = "Installer Authenticode signature is invalid.";
                return false;
            }
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(installerPath));
#pragma warning restore SYSLIB0057
            var thumbprint = NormalizeThumbprint(certificate.Thumbprint);
            if (!AllowedCertificateThumbprints.Contains(thumbprint))
            {
                reason = "Installer certificate thumbprint is not allowlisted.";
                return false;
            }

            var publisher = certificate
                .GetNameInfo(X509NameType.SimpleName, false)?
                .Trim() ?? string.Empty;
            if (!AllowedPublisherNames.Contains(publisher))
            {
                reason = "Installer certificate publisher is not allowlisted.";
                return false;
            }

            using var chain = new X509Chain();
            if (!chain.Build(certificate))
            {
                reason = "Installer Authenticode certificate chain is invalid.";
                return false;
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            reason = $"Installer has no valid Authenticode signature: {ex.Message}";
            return false;
        }
    }

    private static IReadOnlySet<string> ParsePublishers(string variable) =>
        Environment.GetEnvironmentVariable(variable)?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlySet<string> ParseThumbprints(string variable) =>
        Environment.GetEnvironmentVariable(variable)?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeThumbprint)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeThumbprint(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        ReadOnlySpan<char> span = value.AsSpan();
        if (span.Length <= 256)
        {
            Span<char> buf = stackalloc char[256];
            if (vn_update_security_policy_normalize_thumbprint(span, span.Length, buf, buf.Length, out int outLen) == 0)
            {
                return new string(buf[..outLen]);
            }
        }
        return value.Replace(" ", string.Empty).ToUpperInvariant();
    }

    private static bool HasValidAuthenticodeSignature(string filePath) =>
        OperatingSystem.IsWindows() && Win32Interop.HasValidAuthenticodeSignature(filePath);
}
