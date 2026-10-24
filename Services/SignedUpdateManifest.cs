using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace VNotch.Services;

internal sealed partial record SignedUpdateManifest(int SchemaVersion, string Version, string InstallerName, long Size, string Sha256)
{
    private const string NativeDll = "VNotch.Native.dll";
    internal const int MaximumManifestBytes = 16 * 1024;
    internal const string ManifestSuffix = ".manifest.json";
    internal const string SignatureSuffix = ".manifest.sig";

    [LibraryImport(NativeDll, EntryPoint = "vn_signed_update_manifest_validate_sizes")]
    private static partial int NativeValidateSizes(int payloadLength, int signatureLength, out byte outValid);

    [LibraryImport(NativeDll, EntryPoint = "vn_signed_update_manifest_validate_metadata", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeValidateMetadata(
        int schemaVersion,
        long size,
        long maxInstallerBytes,
        ReadOnlySpan<char> sha256,
        int sha256Len,
        out byte outValid);

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
        NativeValidateSizes(payload.Length, signature.Length, out byte sizesValid);
        if (sizesValid == 0)
            throw new InvalidDataException("Invalid update manifest or signature size.");

        using var verifier = ECDsa.Create();
        verifier.ImportFromPem(publicKey);
        if (verifier.KeySize != 256 || !verifier.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw new InvalidDataException("Update manifest signature is invalid.");

        var manifest = JsonSerializer.Deserialize<SignedUpdateManifest>(payload)
            ?? throw new InvalidDataException("Update manifest is empty.");
        ReadOnlySpan<char> sha256Span = manifest.Sha256 != null ? manifest.Sha256.AsSpan() : ReadOnlySpan<char>.Empty;
        NativeValidateMetadata(
            manifest.SchemaVersion,
            manifest.Size,
            UpdateSecurityPolicy.MaximumInstallerBytes,
            sha256Span,
            sha256Span.Length,
            out byte metadataValid);
        if (metadataValid == 0 ||
            manifest.InstallerName != expectedInstaller ||
            manifest.InstallerName is not (UpdateService.SetupName or UpdateService.SelfContainedSetupName) ||
            !UpdateService.TryParseReleaseVersion(manifest.Version, out _, out _) ||
            !UpdateService.TryParseReleaseVersion(expectedVersion, out _, out _) ||
            UpdateService.CompareVersions(manifest.Version, expectedVersion) != 0 ||
            !UpdateService.TryParseReleaseVersion(currentVersion, out _, out _) ||
            UpdateService.CompareVersions(manifest.Version, currentVersion) <= 0)
            throw new InvalidDataException("Update manifest does not match the requested newer release.");

        return manifest;
    }
}
