using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace VNotch.Services;

public class UpdateService : IUpdateService
{
    private const string GithubApiUrl = "https://api.github.com/repos/rainaku/V-Notch/releases/latest";
    private const string UserAgent = "V-Notch-Updater";
    internal const string SetupName = "V-Notch-Setup.exe";
    internal const string SelfContainedSetupName = "V-Notch-Setup-SelfContained.exe";
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(45);
    private readonly HttpClient _httpClient;
    private readonly UpdateSecurityPolicy _securityPolicy;
    private readonly Func<string, (bool IsValid, string Reason)> _signatureValidator;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private string? _latestReleaseEtag;
    private UpdateInfo? _cachedLatestRelease;
    private DateTime _lastCheckUtc = DateTime.MinValue;

    public UpdateService() : this(CreateHttpClient(), UpdateSecurityPolicy.FromEnvironment()) { }

    internal UpdateService(HttpClient httpClient, UpdateSecurityPolicy securityPolicy,
        Func<string, (bool IsValid, string Reason)>? signatureValidator = null)
    {
        _httpClient = httpClient;
        _securityPolicy = securityPolicy;
        _signatureValidator = signatureValidator ?? (path =>
        {
            var valid = _securityPolicy.IsTrustedSignature(path, out var reason);
            return (valid, reason);
        });
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
    }

    public string CurrentVersion => System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v
        ? (v.Revision > 0 ? $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}" : $"{v.Major}.{v.Minor}.{v.Build}") : "1.7.0";

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        await _checkLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (_cachedLatestRelease != null && now - _lastCheckUtc < MinRefreshInterval) return Clone(_cachedLatestRelease);
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubApiUrl);
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
                InstallerName = installer?.Name ?? string.Empty,
                ReleaseNotes = root.GetProperty("body").GetString() ?? string.Empty,
                PublishedAt = root.GetProperty("published_at").GetDateTime()
            };
            info.IsNewerVersion = installer != null && checksum != null && CompareVersions(info.Version, CurrentVersion) > 0;
            if (installer == null || checksum == null) RuntimeLog.Warn("UPDATER", "Latest release has no approved installer and matching SHA-256 asset; update is unavailable.");
            _cachedLatestRelease = info;
            return Clone(info);
        }
        catch (Exception ex) { RuntimeLog.Error("UPDATER", ex, "Update check failed"); return _cachedLatestRelease != null ? Clone(_cachedLatestRelease) : null; }
        finally { _checkLock.Release(); }
    }

    public async Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync()
    {
        try
        {
            var url = "https://api.github.com/repos/rainaku/V-Notch/releases";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
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
                    InstallerName = installer?.Name ?? string.Empty,
                    ReleaseNotes = release.GetProperty("body").GetString() ?? string.Empty,
                    PublishedAt = release.GetProperty("published_at").GetDateTime()
                };
                info.IsNewerVersion = installer != null && checksum != null && CompareVersions(info.Version, CurrentVersion) > 0;
                releases.Add(info);
            }
            return releases;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("UPDATER", ex, "Get all releases failed");
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
            if (!IsApprovedUpdate(updateInfo)) throw new InvalidOperationException("Update does not reference an approved installer and checksum.");
            directory = Path.Combine(Path.GetTempPath(), "V-Notch", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            installerPath = Path.Combine(directory, updateInfo.InstallerName);
            var expectedHash = await DownloadChecksumAsync(updateInfo.ChecksumUrl, cancellationToken);
            await DownloadInstallerAsync(updateInfo.DownloadUrl, installerPath, progress, cancellationToken);
            var actualHash = await ComputeSha256Async(installerPath, cancellationToken);
            if (!HashesMatch(expectedHash, actualHash))
                throw new InvalidDataException("Installer SHA-256 does not match the release checksum.");
            var signature = _signatureValidator(installerPath);
            if (!signature.IsValid) throw new InvalidDataException(signature.Reason);

            var process = Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true, WorkingDirectory = directory });
            if (process == null) throw new InvalidOperationException("Could not start verified installer.");
            installerStarted = true;
            RuntimeLog.Log("UPDATER", $"Starting verified installer {updateInfo.InstallerName}.");
            Application.Current?.Shutdown();
            return true;
        }
        catch (OperationCanceledException) { RuntimeLog.Warn("UPDATER", "Update download canceled; current application remains open."); return false; }
        catch (Exception ex) { RuntimeLog.Error("UPDATER", ex, "Update download/verification failed; current application remains open"); return false; }
        finally
        {
            if (directory != null && !installerStarted) DeleteDirectory(directory);
        }
    }

    internal async Task DownloadInstallerAsync(string url, string path, IProgress<double>? progress, CancellationToken token)
    {
        using var response = await SendHttpsAsync(new HttpRequestMessage(HttpMethod.Get, url), token);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is > UpdateSecurityPolicy.MaximumInstallerBytes) throw new InvalidDataException("Installer exceeds 500 MB limit.");
        progress?.Report(length is > 0 ? 0 : -1);
        await using var input = await response.Content.ReadAsStreamAsync(token);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920]; long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), token); if (count == 0) break;
            received += count; if (received > UpdateSecurityPolicy.MaximumInstallerBytes) throw new InvalidDataException("Installer exceeds 500 MB limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            if (length is > 0) progress?.Report(received * 100d / length.Value);
        }
        progress?.Report(100);
    }

    private async Task<string> DownloadChecksumAsync(string url, CancellationToken token)
    {
        using var response = await SendHttpsAsync(new HttpRequestMessage(HttpMethod.Get, url), token);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync(token);
        var hash = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (hash == null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Release checksum is not a SHA-256 value.");
        return hash.ToUpperInvariant();
    }

    internal async Task<HttpResponseMessage> SendHttpsAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (!IsHttps(request.RequestUri)) throw new InvalidOperationException("Only HTTPS update URLs are accepted.");
        for (var redirects = 0; ; redirects++)
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!IsRedirect(response.StatusCode)) { if (!IsHttps(response.RequestMessage?.RequestUri ?? request.RequestUri)) { response.Dispose(); throw new InvalidOperationException("Final update URL is not HTTPS."); } return response; }
            if (redirects >= 5 || response.Headers.Location == null) { response.Dispose(); throw new InvalidOperationException("Invalid or excessive update redirect."); }
            var target = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(request.RequestUri!, response.Headers.Location);
            response.Dispose(); if (!IsHttps(target)) throw new InvalidOperationException("Update redirect target is not HTTPS.");
            request = new HttpRequestMessage(HttpMethod.Get, target);
        }
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
    internal static bool IsApprovedUpdate(UpdateInfo update) =>
        (update.InstallerName.Equals(SetupName, StringComparison.OrdinalIgnoreCase) || update.InstallerName.Equals(SelfContainedSetupName, StringComparison.OrdinalIgnoreCase)) &&
        update.ChecksumUrl.Length > 0 && update.DownloadUrl.Length > 0 && IsHttps(new Uri(update.DownloadUrl)) && IsHttps(new Uri(update.ChecksumUrl));
    internal static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    internal static bool HashesMatch(string expected, string actual) =>
        expected.Length == 64 && actual.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static async Task<string> ComputeSha256Async(string path, CancellationToken token) { await using var file = File.OpenRead(path); return Convert.ToHexString(await SHA256.HashDataAsync(file, token)); }
    private static HttpClient CreateHttpClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
    private static void DeleteDirectory(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (Exception ex) { RuntimeLog.Warn("UPDATER", $"Could not remove temporary update files: {ex.Message}"); } }
    internal static int CompareVersions(string left, string right) => Version.TryParse(left, out var a) && Version.TryParse(right, out var b) ? a.CompareTo(b) : 0;
    private static UpdateInfo Clone(UpdateInfo source) => new() { Version = source.Version, DownloadUrl = source.DownloadUrl, ChecksumUrl = source.ChecksumUrl, InstallerName = source.InstallerName, ReleaseNotes = source.ReleaseNotes, PublishedAt = source.PublishedAt, IsNewerVersion = source.IsNewerVersion };
}
