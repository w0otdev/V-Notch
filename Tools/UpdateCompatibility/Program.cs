using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Services;

string version = args[0];
string? assetsDirectory = args.Length > 1 ? args[1] : null;
using var testKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
string publicKey = assetsDirectory == null ? testKey.ExportSubjectPublicKeyInfoPem() : SignedUpdateManifest.ReadPinnedPublicKey();
var installers = new Dictionary<string, byte[]>(StringComparer.Ordinal);
var manifests = new Dictionary<string, byte[]>(StringComparer.Ordinal);
var signatures = new Dictionary<string, byte[]>(StringComparer.Ordinal);
foreach (string name in new[] { UpdateService.SetupName, UpdateService.SelfContainedSetupName })
{
    byte[] installer = assetsDirectory == null ? Encoding.UTF8.GetBytes("compatibility payload: " + name) : File.ReadAllBytes(Path.Combine(assetsDirectory, name));
    byte[] manifest = assetsDirectory == null
        ? JsonSerializer.SerializeToUtf8Bytes(new SignedUpdateManifest(1, version, name, installer.Length, Convert.ToHexString(SHA256.HashData(installer))))
        : File.ReadAllBytes(Path.Combine(assetsDirectory, name + SignedUpdateManifest.ManifestSuffix));
    byte[] signature = assetsDirectory == null
        ? testKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
        : File.ReadAllBytes(Path.Combine(assetsDirectory, name + SignedUpdateManifest.SignatureSuffix));
    if (assetsDirectory != null)
    {
        string checksum = File.ReadAllText(Path.Combine(assetsDirectory, name + ".sha256")).Trim();
        if (checksum != Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant() + "  " + name)
            throw new InvalidDataException("Legacy checksum sidecar does not match the signed installer.");
    }
    installers.Add(name, installer);
    manifests.Add(name, manifest);
    signatures.Add(name, signature);
}

foreach (bool bundled in new[] { false, true })
foreach (string scenario in new[] { "valid", "tampered-installer", "bad-signature", "missing-signature" })
{
    string selectedName = bundled ? UpdateService.SelfContainedSetupName : UpdateService.SetupName;
    var names = bundled ? new[] { UpdateService.SetupName, UpdateService.SelfContainedSetupName } : new[] { UpdateService.SetupName };
    string root = $"https://github.com/rainaku/V-Notch/releases/download/v{version}/";
    string json = JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, body = "compatibility fixture", published_at = DateTime.UtcNow,
        assets = names.SelectMany(name => new[] { name, name + ".sha256", name + SignedUpdateManifest.ManifestSuffix, name + SignedUpdateManifest.SignatureSuffix })
            .Select(name => new { name, browser_download_url = root + name })
    });
    bool installerRequested = false;
    using var client = new HttpClient(new Handler(request =>
    {
        if (request.RequestUri!.Host == "api.github.com")
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        string name = request.RequestUri.Segments[^1];
        if (name == selectedName + SignedUpdateManifest.ManifestSuffix)
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(manifests[selectedName]) };
        if (name == selectedName + SignedUpdateManifest.SignatureSuffix)
        {
            if (scenario == "missing-signature") return new(HttpStatusCode.NotFound);
            var signature = signatures[selectedName].ToArray();
            if (scenario == "bad-signature") signature[0] ^= 1;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(signature) };
        }
        if (name != selectedName) throw new InvalidDataException("Unexpected asset requested by the 1.9.3 updater.");
        installerRequested = true;
        var installer = installers[selectedName].ToArray();
        if (scenario == "tampered-installer") installer[0] ^= 1;
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) };
    }));
    // No launch API is called. This is the original discovery/download/verifier.
    var updater = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: publicKey);
    if (updater.CurrentVersion != "1.9.3") throw new InvalidDataException("Probe must run with the original client version.");
    var update = await updater.CheckForUpdatesAsync();
    if (update is not { IsNewerVersion: true } || update.InstallerName != selectedName)
        throw new InvalidDataException("The 1.9.3 client did not offer the expected newer installer.");
    string destination = Path.Combine(AppContext.BaseDirectory, "download-" + Guid.NewGuid().ToString("N"));
    try
    {
        bool rejected = false;
        try { await updater.DownloadAndVerifyInstallerAsync(update, destination, null, default); }
        catch (InvalidDataException) { rejected = true; }
        catch (HttpRequestException) { rejected = true; }
        if (rejected != (scenario != "valid")) throw new InvalidDataException("Unexpected compatibility verification result: " + scenario);
        if (scenario == "valid" && !File.ReadAllBytes(destination).SequenceEqual(installers[selectedName]))
            throw new InvalidDataException("Downloaded installer bytes changed.");
        if (scenario is "bad-signature" or "missing-signature" && installerRequested)
            throw new InvalidDataException("Untrusted metadata must be rejected before the installer is downloaded.");
        Console.WriteLine($"PASS 1.9.3 -> {version}: {selectedName}, {scenario}");
    }
    finally { if (File.Exists(destination)) File.Delete(destination); }
}
Console.WriteLine(assetsDirectory == null
    ? "Legacy protocol checks passed; pinned production key unchanged. Payloads were synthetic and never executed."
    : "Both release installers verified by the original 1.9.3 updater with its pinned production key. Nothing was executed.");

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => Task.FromResult(reply(request));
}
