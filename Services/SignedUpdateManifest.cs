using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace VNotch.Services;

internal sealed record SignedUpdateManifest(int SchemaVersion, string Version, string InstallerName, long Size, string Sha256)
{
    internal const int MaximumManifestBytes = 16 * 1024;
    internal const string ManifestSuffix = ".manifest.json";
    internal const string SignatureSuffix = ".manifest.sig";

    internal static string ReadPinnedPublicKey()
    {
        using var stream = typeof(SignedUpdateManifest).Assembly
            .GetManifestResourceStream("VNotch.UpdatePublicKey.pem")
            ?? throw new InvalidOperationException("Update verification key is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static SignedUpdateManifest Verify(byte[] payload, byte[] signature, string publicKey,
        string expectedVersion, string expectedInstaller, string currentVersion)
    {
        if (payload.Length == 0 || payload.Length > MaximumManifestBytes || signature.Length != 64)
            throw new InvalidDataException("Invalid update manifest or signature size.");

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(publicKey);
        if (verifier.KeySize != 256 || !verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Update manifest signature is invalid.");

        var manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(payload)
            ?? throw new InvalidDataException("Update manifest is empty.");
        if (manifest.SchemaVersion != 1 ||
            manifest.InstallerName != expectedInstaller ||
            manifest.InstallerName is not (UpdateService.SetupName or UpdateService.SelfContainedSetupName) ||
            !UpdateService.TryParseReleaseVersion(manifest.Version, out _, out _) ||
            !UpdateService.TryParseReleaseVersion(expectedVersion, out _, out _) ||
            UpdateService.CompareVersions(manifest.Version, expectedVersion) != 0 ||
            !UpdateService.TryParseReleaseVersion(currentVersion, out _, out _) ||
            UpdateService.CompareVersions(manifest.Version, currentVersion) <= 0 ||
            manifest.Size <= 0 || manifest.Size > UpdateSecurityPolicy.MaximumInstallerBytes ||
            manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Update manifest does not match the requested newer release.");

        return manifest;
    }
}
