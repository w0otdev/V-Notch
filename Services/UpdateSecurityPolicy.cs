using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace VNotch.Services;

/// <summary>Security configuration for release installers. Values are deliberately
/// allowlists: an unrecognised signing certificate is never accepted.</summary>
public sealed class UpdateSecurityPolicy
{
    public const long MaximumInstallerBytes = 500L * 1024 * 1024;

    public IReadOnlySet<string> AllowedPublisherNames { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> AllowedCertificateThumbprints { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static UpdateSecurityPolicy FromEnvironment() => new()
    {
        AllowedPublisherNames = Parse("VNOTCH_UPDATER_ALLOWED_PUBLISHERS"),
        AllowedCertificateThumbprints = Parse("VNOTCH_UPDATER_ALLOWED_THUMBPRINTS")
    };

    public bool IsTrustedSignature(string installerPath, out string reason)
    {
        if (AllowedPublisherNames.Count == 0 && AllowedCertificateThumbprints.Count == 0)
        {
            reason = "No Authenticode allowlist configured. Skipping signature validation.";
            return true;
        }

        if (AllowedPublisherNames.Count == 0 || AllowedCertificateThumbprints.Count == 0)
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
            var thumbprint = Normalize(certificate.Thumbprint);
            if (!AllowedCertificateThumbprints.Contains(thumbprint))
            {
                reason = "Installer certificate thumbprint is not allowlisted.";
                return false;
            }

            var publisher = certificate.GetNameInfo(X509NameType.SimpleName, false);
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

    private static IReadOnlySet<string> Parse(string variable) =>
        Environment.GetEnvironmentVariable(variable)?
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
        ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string? value) => (value ?? string.Empty).Replace(" ", string.Empty).ToUpperInvariant();

    private static bool HasValidAuthenticodeSignature(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        using var file = new WinTrustFileInfo(filePath);
        using var data = new WinTrustData(file);
        return WinVerifyTrust(IntPtr.Zero, action, data) == 0;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, WinTrustData data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        public uint cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>();
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile = IntPtr.Zero;
        public IntPtr pgKnownSubject = IntPtr.Zero;
        public WinTrustFileInfo(string path) => pcwszFilePath = path;
        public void Dispose() { /* WinTrustData owns allocated native memory. */ }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustData : IDisposable
    {
        public uint cbStruct = (uint)Marshal.SizeOf<WinTrustData>();
        public IntPtr pPolicyCallbackData = IntPtr.Zero;
        public IntPtr pSIPClientData = IntPtr.Zero;
        public uint dwUIChoice = 2; // WTD_UI_NONE
        public uint fdwRevocationChecks = 0;
        public uint dwUnionChoice = 1; // WTD_CHOICE_FILE
        public IntPtr pFile;
        public uint dwStateAction = 0; // WTD_STATEACTION_IGNORE
        public IntPtr hWVTStateData = IntPtr.Zero;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pwszURLReference = null;
        public uint dwProvFlags = 0;
        public uint dwUIContext = 0;
        public IntPtr pSignatureSettings = IntPtr.Zero;
        public WinTrustData(WinTrustFileInfo file)
        {
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, pFile, false);
        }
        public void Dispose() { if (pFile != IntPtr.Zero) { Marshal.DestroyStructure<WinTrustFileInfo>(pFile); Marshal.FreeHGlobal(pFile); } }
    }
}
