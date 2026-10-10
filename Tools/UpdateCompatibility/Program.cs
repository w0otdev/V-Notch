using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VNotch.Services;

string version = args[0];
bool prerelease = args.Contains("--prerelease");
string? installedArgument = args.FirstOrDefault(arg => arg.StartsWith("--installed-version=", StringComparison.Ordinal));
string installedVersion = installedArgument?.Split('=', 2)[1].Split('+')[0] ?? "1.9.3";
bool currentUpdater = prerelease || installedArgument != null;
if (installedArgument != null)
{
    var core = Version.Parse(installedVersion.Split('-')[0]);
    string fileVersion = FileVersionInfo.GetVersionInfo(typeof(UpdateService).Assembly.Location).FileVersion!;
    if (fileVersion != $"{core.Major}.{core.Minor}.65534.65534")
        throw new InvalidDataException("Probe assembly must carry the high Windows build version.");
}
string clientName = currentUpdater ? $"current updater ({installedVersion})" : "1.9.3";
string? assetsDirectory = args.Skip(1).FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
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

foreach (bool includePrereleases in installedArgument != null && !prerelease ? new[] { false, true } : new[] { prerelease })
foreach (bool bundled in new[] { false, true })
foreach (string scenario in new[] { "valid", "tampered-installer", "bad-signature", "missing-signature" })
{
    string selectedName = bundled ? UpdateService.SelfContainedSetupName : UpdateService.SetupName;
    var names = bundled ? new[] { UpdateService.SetupName, UpdateService.SelfContainedSetupName } : new[] { UpdateService.SetupName };
    string root = $"https://github.com/rainaku/V-Notch/releases/download/v{version}/";
    string json = JsonSerializer.Serialize(new
    {
        tag_name = "v" + version, prerelease, body = "compatibility fixture", published_at = DateTime.UtcNow,
        assets = names.SelectMany(name => new[] { name, name + ".sha256", name + SignedUpdateManifest.ManifestSuffix, name + SignedUpdateManifest.SignatureSuffix })
            .Select(name => new { name, browser_download_url = root + name })
    });
    // A beta published more recently must not hide the final release of the same core.
    string releaseList = "[" + json + "]";
    if (installedArgument != null && !prerelease)
    {
        using var prior = JsonDocument.Parse(json);
        var beta = prior.RootElement.Deserialize<Dictionary<string, JsonElement>>()!;
        beta["tag_name"] = JsonSerializer.SerializeToElement("v" + installedVersion);
        beta["prerelease"] = JsonSerializer.SerializeToElement(true);
        beta["published_at"] = JsonSerializer.SerializeToElement(DateTime.UtcNow.AddDays(1));
        releaseList = "[" + JsonSerializer.Serialize(beta) + "," + json + "]";
    }
    bool installerRequested = false;
    using var client = new HttpClient(new Handler(request =>
    {
        if (request.RequestUri!.Host == "api.github.com")
            return new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/latest") ? json : releaseList) };
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
        if (name != selectedName) throw new InvalidDataException($"Unexpected asset requested by the {clientName}.");
        installerRequested = true;
        var installer = installers[selectedName].ToArray();
        if (scenario == "tampered-installer") installer[0] ^= 1;
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) };
    }));
    // Exercise discovery/download/verification only; never launch an installer.
    var updater = new UpdateService(client, new UpdateSecurityPolicy(), manifestPublicKey: publicKey);
    if (updater.CurrentVersion != installedVersion) throw new InvalidDataException("Probe assembly must have the requested installed release version.");
    var update = currentUpdater
        ? await (Task<UpdateInfo?>)typeof(UpdateService).GetMethod(nameof(UpdateService.CheckForUpdatesAsync), [typeof(bool)])!.Invoke(updater, [includePrereleases])!
        : await updater.CheckForUpdatesAsync();
    if (update is not { IsNewerVersion: true } || update.InstallerName != selectedName || update.Version != version)
        throw new InvalidDataException($"The {clientName} client did not offer the expected newer installer.");
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
        Console.WriteLine($"PASS {clientName} -> {version}: beta opt-in={includePrereleases}, {selectedName}, {scenario}");
        if (prerelease && await updater.CheckForUpdatesAsync() is { IsNewerVersion: true })
            throw new InvalidDataException("A beta was offered without opting in.");
        if (installedArgument != null && !prerelease && scenario == "valid")
        {
            var core = Version.Parse(installedVersion.Split('-')[0]);
            if (core.Build > 0)
            {
                string olderVersion = $"{core.Major}.{core.Minor}.{core.Build - 1}";
                var older = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
                older["tag_name"] = JsonSerializer.SerializeToElement("v" + olderVersion);
                string olderJson = JsonSerializer.Serialize(older);
                using var olderClient = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
                    ? new(HttpStatusCode.OK) { Content = new StringContent(request.RequestUri.AbsolutePath.EndsWith("/latest") ? olderJson : "[" + olderJson + "]") }
                    : throw new InvalidOperationException("An older stable release must not download any assets.")));
                var olderUpdater = new UpdateService(olderClient, new UpdateSecurityPolicy(), manifestPublicKey: publicKey);
                var olderUpdate = await (Task<UpdateInfo?>)typeof(UpdateService).GetMethod(nameof(UpdateService.CheckForUpdatesAsync), [typeof(bool)])!
                    .Invoke(olderUpdater, [includePrereleases])!;
                if (olderUpdate is { IsNewerVersion: true })
                    throw new InvalidDataException("An older stable release was offered as a beta upgrade.");
                Console.WriteLine($"PASS {clientName} rejects downgrade to {olderVersion}: beta opt-in={includePrereleases}");
            }
        }
    }
    finally { if (File.Exists(destination)) File.Delete(destination); }
}
Console.WriteLine(assetsDirectory == null
    ? $"Protocol checks passed for the {clientName}; pinned production key unchanged. Payloads were synthetic and never executed."
    : $"Both release installers verified by the {clientName} with the pinned production key. Nothing was executed.");

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        => Task.FromResult(reply(request));
}
