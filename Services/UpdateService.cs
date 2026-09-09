using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace VNotch.Services;

#pragma warning disable S1075 // Public GitHub Releases API endpoints
public class UpdateService : IUpdateService
{
    private const string LogCategory = "UPDATER";
    private static readonly Uri GithubLatestReleaseUri = new("https://api.github.com/repos/rainaku/V-Notch/releases/latest");
    private static readonly Uri GithubAllReleasesUri = new("https://api.github.com/repos/rainaku/V-Notch/releases");
    private const string UserAgent = "V-Notch-Updater";
    internal const string SetupName = "V-Notch-Setup.exe";
    internal const string SelfContainedSetupName = "V-Notch-Setup-SelfContained.exe";
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(45);
    private readonly HttpClient _httpClient;
    private readonly UpdateSecurityPolicy _securityPolicy;
    private readonly Func<string, (bool IsValid, string Reason)> _signatureValidator;
    private readonly string _manifestPublicKey;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private string? _latestReleaseEtag;
    private UpdateInfo? _cachedLatestRelease;
    private DateTime _lastCheckUtc = DateTime.MinValue;

    public UpdateService() : this(CreateHttpClient(), UpdateSecurityPolicy.FromEnvironment()) { }

    internal UpdateService(HttpClient httpClient, UpdateSecurityPolicy securityPolicy,
        Func<string, (bool IsValid, string Reason)>? signatureValidator = null,
        string? manifestPublicKey = null)
    {
        _httpClient = httpClient;
        _securityPolicy = securityPolicy;
        _manifestPublicKey = manifestPublicKey ?? SignedUpdateManifest.ReadPinnedPublicKey();
        _signatureValidator = signatureValidator ?? (path =>
        {
            var valid = _securityPolicy.IsTrustedSignature(path, out var reason);
            return (valid, reason);
        });
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public string CurrentVersion => GetAppVersion();

    private static string GetAppVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version == null)
            return "0.0.0";

        if (version.Revision > 0)
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

        return $"{version.Major}.{version.Minor}.{version.Build}";
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        await _checkLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (_cachedLatestRelease != null && now - _lastCheckUtc < MinRefreshInterval) return Clone(_cachedLatestRelease);
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubLatestReleaseUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(_latestReleaseEtag)) request.Headers.TryAddWithoutValidation("If-None-Match", _latestReleaseEtag);
            using var response = await SendHttpsAsync(request, CancellationToken.None);
            _lastCheckUtc = now;
            if (response.StatusCode == HttpStatusCode.NotModified && _cachedLatestRelease != null) return Clone(_cachedLatestRelease);
            response.EnsureSuccessStatusCode();
            _latestReleaseEtag = response.Headers.ETag?.ToString();
            using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = jsonDoc.RootElement;
            var (installer, checksum) = SelectReleaseAssets(root);
            var info = new UpdateInfo
            {
                Version = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? string.Empty,
                DownloadUrl = installer?.Url ?? string.Empty,
                ChecksumUrl = checksum?.Url ?? string.Empty,
                ManifestUrl = FindAssetUrl(root, installer?.Name + SignedUpdateManifest.ManifestSuffix),
                ManifestSignatureUrl = FindAssetUrl(root, installer?.Name + SignedUpdateManifest.SignatureSuffix),
                InstallerName = installer?.Name ?? string.Empty,
                ReleaseNotes = root.GetProperty("body").GetString() ?? string.Empty,
                PublishedAt = root.GetProperty("published_at").GetDateTime()
            };
            info.IsNewerVersion = IsApprovedUpdate(info) && CompareVersions(info.Version, CurrentVersion) > 0;
            if (!IsApprovedUpdate(info)) RuntimeLog.Warn(LogCategory, "Latest release is missing signed update assets; update is unavailable.");
            _cachedLatestRelease = info;
            return Clone(info);
        }
        catch (Exception ex) { RuntimeLog.Error(LogCategory, ex, "Update check failed"); return _cachedLatestRelease != null ? Clone(_cachedLatestRelease) : null; }
        finally { _checkLock.Release(); }
    }

    public async Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubAllReleasesUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await SendHttpsAsync(request, CancellationToken.None);
            response.EnsureSuccessStatusCode();

            using var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var releases = new List<UpdateInfo>();

            foreach (var release in jsonDoc.RootElement.EnumerateArray())
            {
                var (installer, checksum) = SelectReleaseAssets(release);
                var info = new UpdateInfo
                {
                    Version = release.GetProperty("tag_name").GetString()?.TrimStart('v') ?? string.Empty,
                    DownloadUrl = installer?.Url ?? string.Empty,
                    ChecksumUrl = checksum?.Url ?? string.Empty,
                    ManifestUrl = FindAssetUrl(release, installer?.Name + SignedUpdateManifest.ManifestSuffix),
                    ManifestSignatureUrl = FindAssetUrl(release, installer?.Name + SignedUpdateManifest.SignatureSuffix),
                    InstallerName = installer?.Name ?? string.Empty,
                    ReleaseNotes = release.GetProperty("body").GetString() ?? string.Empty,
                    PublishedAt = release.GetProperty("published_at").GetDateTime()
                };
                info.IsNewerVersion = IsApprovedUpdate(info) && CompareVersions(info.Version, CurrentVersion) > 0;
                releases.Add(info);
            }
            return releases;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Get all releases failed");
            return Array.Empty<UpdateInfo>();
        }
    }

    public async Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        string? directory = null;
        string? installerPath = null;
        var installerStarted = false;
        try
        {
            if (!IsApprovedUpdate(updateInfo)) throw new InvalidOperationException("Update does not reference an approved installer and signed manifest.");
            directory = Path.Combine(Path.GetTempPath(), "V-Notch", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            installerPath = Path.Combine(directory, updateInfo.InstallerName);
            await DownloadAndVerifyInstallerAsync(updateInfo, installerPath, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true, WorkingDirectory = directory });
            if (process == null) throw new InvalidOperationException("Could not start verified installer.");
            installerStarted = true;
            RuntimeLog.Log(LogCategory, $"Starting verified installer {updateInfo.InstallerName}.");
            Application.Current?.Shutdown();
            return true;
        }
        catch (OperationCanceledException) { RuntimeLog.Warn(LogCategory, "Update download canceled; current application remains open."); return false; }
        catch (Exception ex) { RuntimeLog.Error(LogCategory, ex, "Update download/verification failed; current application remains open"); return false; }
        finally
        {
            if (directory != null && !installerStarted) DeleteDirectory(directory);
        }
    }

    internal async Task DownloadAndVerifyInstallerAsync(UpdateInfo update, string path, IProgress<double>? progress, CancellationToken token)
    {
        if (!IsApprovedUpdate(update)) throw new InvalidDataException("Signed update assets are required.");
        var manifest = await DownloadVerifiedManifestAsync(update, token);
        await DownloadInstallerAsync(update.DownloadUrl, path, progress, token, manifest.Size);
        var actualHash = await ComputeSha256Async(path, token);
        if (!HashesMatch(manifest.Sha256, actualHash))
            throw new InvalidDataException("Installer SHA-256 does not match the signed manifest.");
        var signature = _signatureValidator(path);
        if (!signature.IsValid) throw new InvalidDataException(signature.Reason);
    }

    internal async Task DownloadInstallerAsync(string url, string path, IProgress<double>? progress, CancellationToken token, long? expectedSize = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendHttpsAsync(request, token);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is > UpdateSecurityPolicy.MaximumInstallerBytes) throw new InvalidDataException("Installer exceeds 500 MB limit.");
        if (expectedSize.HasValue && length.HasValue && length != expectedSize)
            throw new InvalidDataException("Installer length does not match the signed manifest.");
        progress?.Report(length is > 0 ? 0 : -1);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920]; long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), token); if (count == 0) break;
            received += count; if (received > UpdateSecurityPolicy.MaximumInstallerBytes) throw new InvalidDataException("Installer exceeds 500 MB limit.");
            if (expectedSize.HasValue && received > expectedSize.Value)
                throw new InvalidDataException("Installer exceeds the signed size.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (length is > 0) progress?.Report(received * 100d / length.Value);
        }
        if (expectedSize.HasValue && received != expectedSize.Value)
            throw new InvalidDataException("Installer is truncated.");
        progress?.Report(100);
    }

    internal async Task<SignedUpdateManifest> DownloadVerifiedManifestAsync(UpdateInfo update, CancellationToken token)
    {
        var payload = await DownloadSmallAssetAsync(update.ManifestUrl, SignedUpdateManifest.MaximumManifestBytes, token);
        var signature = await DownloadSmallAssetAsync(update.ManifestSignatureUrl, 64, token);
        return SignedUpdateManifest.Verify(payload, signature, _manifestPublicKey,
            update.Version, update.InstallerName, CurrentVersion);
    }

    private async Task<byte[]> DownloadSmallAssetAsync(string url, int limit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendHttpsAsync(request, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Update metadata exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            if (output.Length + count > limit)
                throw new InvalidDataException("Update metadata exceeds its size limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal async Task<HttpResponseMessage> SendHttpsAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (!IsHttps(request.RequestUri)) throw new InvalidOperationException("Only HTTPS update URLs are accepted.");
        const int maxRedirects = 5;
        for (var redirects = 0; redirects < maxRedirects; redirects++)
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!IsRedirect(response.StatusCode))
            {
                if (!IsHttps(response.RequestMessage?.RequestUri ?? request.RequestUri))
                {
                    response.Dispose();
                    throw new InvalidOperationException("Final update URL is not HTTPS.");
                }
                return response;
            }
            if (response.Headers.Location == null)
            {
                response.Dispose();
                throw new InvalidOperationException("Invalid or excessive update redirect.");
            }
            var target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(request.RequestUri!, response.Headers.Location);
            response.Dispose();
            if (!IsHttps(target)) throw new InvalidOperationException("Update redirect target is not HTTPS.");
            request = new HttpRequestMessage(HttpMethod.Get, target);
        }

        throw new InvalidOperationException("Invalid or excessive update redirect.");
    }

    internal static (ReleaseAsset? Installer, ReleaseAsset? Checksum) SelectReleaseAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return default;
        var all = assets.EnumerateArray().Select(a => new ReleaseAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? "")).ToArray();
        var installer = all.FirstOrDefault(a => a.Name.Equals(SelfContainedSetupName, StringComparison.OrdinalIgnoreCase))
                     ?? all.FirstOrDefault(a => a.Name.Equals(SetupName, StringComparison.OrdinalIgnoreCase));
        return (installer, installer == null ? null : all.FirstOrDefault(a => a.Name.Equals(installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase)));
    }
    internal sealed record ReleaseAsset(string Name, string Url);
    private static string FindAssetUrl(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets)) return "";
        foreach (var asset in assets.EnumerateArray())
            if (asset.GetProperty("name").GetString() == name)
                return asset.GetProperty("browser_download_url").GetString() ?? "";
        return "";
    }
    internal static bool IsApprovedUpdate(UpdateInfo update) =>
        (update.InstallerName is SetupName or SelfContainedSetupName) &&
        IsHttpsUrl(update.DownloadUrl) && IsHttpsUrl(update.ManifestUrl) && IsHttpsUrl(update.ManifestSignatureUrl);
    private static bool IsHttpsUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && IsHttps(uri);
    internal static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    internal static bool HashesMatch(string expected, string actual) =>
        expected.Length == 64 && actual.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static async Task<string> ComputeSha256Async(string path, CancellationToken token) { await using var file = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(file, token)); }
    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private static void DeleteDirectory(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (Exception ex) { RuntimeLog.Warn(LogCategory, $"Could not remove temporary update files: {ex.Message}"); } }
    internal static int CompareVersions(string left, string right) => Version.TryParse(left, out var a) && Version.TryParse(right, out var b) ? a.CompareTo(b) : 0;
    private static UpdateInfo Clone(UpdateInfo source) => new() { Version = source.Version, DownloadUrl = source.DownloadUrl, ChecksumUrl = source.ChecksumUrl, ManifestUrl = source.ManifestUrl, ManifestSignatureUrl = source.ManifestSignatureUrl, InstallerName = source.InstallerName, ReleaseNotes = source.ReleaseNotes, PublishedAt = source.PublishedAt, IsNewerVersion = source.IsNewerVersion };
}
