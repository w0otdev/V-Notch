using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace VNotch.Services;

#pragma warning disable S1075 // Public GitHub Releases API endpoints
public class UpdateService : IUpdateService, IDisposable
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

    private readonly bool _ownsHttpClient;

    public UpdateService() : this(CreateHttpClient(), UpdateSecurityPolicy.FromEnvironment()) { _ownsHttpClient = true; }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
    }

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
    public event EventHandler<UpdateInfo?>? UpdateCheckCompleted;
    public UpdateInfo? LatestUpdateInfo => _cachedLatestRelease != null ? Clone(_cachedLatestRelease) : null;

    private static string GetAppVersion()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (version == null)
            return "0.0.0";

        if (version.Revision > 0)
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        UpdateInfo? result = null;
        await _checkLock.WaitAsync();
        try
        {
            var now = DateTime.UtcNow;
            if (_cachedLatestRelease != null && now - _lastCheckUtc < MinRefreshInterval)
            {
                result = Clone(_cachedLatestRelease);
                return result;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubLatestReleaseUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(_latestReleaseEtag)) request.Headers.TryAddWithoutValidation("If-None-Match", _latestReleaseEtag);
            using var response = await SendHttpsAsync(request, CancellationToken.None).ConfigureAwait(false);
            _lastCheckUtc = now;
            if (response.StatusCode == HttpStatusCode.NotModified && _cachedLatestRelease != null)
            {
                result = Clone(_cachedLatestRelease);
                return result;
            }
            response.EnsureSuccessStatusCode();
            _latestReleaseEtag = response.Headers.ETag?.ToString();
            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var jsonDoc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
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
            result = Clone(info);
            return result;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Update check failed");
            result = _cachedLatestRelease != null ? Clone(_cachedLatestRelease) : null;
            return result;
        }
        finally
        {
            _checkLock.Release();
            try { UpdateCheckCompleted?.Invoke(this, result != null ? Clone(result) : null); }
            catch (Exception ex) { RuntimeLog.Error(LogCategory, ex, "UpdateCheckCompleted handler threw"); }
        }
    }

    public async Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubAllReleasesUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await SendHttpsAsync(request, CancellationToken.None).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var jsonDoc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
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
        IDisposable? cleanupLease = null;
        var installerStarted = false;
        try
        {
            if (!IsApprovedUpdate(updateInfo)) throw new InvalidOperationException("Update does not reference an approved installer and signed manifest.");
            var safeInstallerName = Path.GetFileName(updateInfo.InstallerName);
            if (!string.Equals(safeInstallerName, updateInfo.InstallerName, StringComparison.Ordinal) ||
                safeInstallerName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                safeInstallerName.Contains(".."))
            {
                throw new InvalidOperationException("Invalid installer file name.");
            }
            directory = Path.Combine(Path.GetTempPath(), "V-Notch", Guid.NewGuid().ToString("N"));
            cleanupLease = AppFileCleanupService.Protect(directory);
            Directory.CreateDirectory(directory);
            installerPath = Path.Combine(directory, safeInstallerName);
            await DownloadAndVerifyInstallerAsync(updateInfo, installerPath, progress, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(new ProcessStartInfo { FileName = installerPath, UseShellExecute = true, WorkingDirectory = directory });
            if (process == null) throw new InvalidOperationException("Could not start verified installer.");
            installerStarted = true;
            RuntimeLog.Log(LogCategory, $"Starting verified installer {updateInfo.InstallerName}.");
            await App.RequestShutdownAsync();
            return true;
        }
        catch (OperationCanceledException) { RuntimeLog.Warn(LogCategory, "Update download canceled; current application remains open."); return false; }
        catch (Exception ex) { RuntimeLog.Error(LogCategory, ex, "Update download/verification failed; current application remains open"); return false; }
        finally
        {
            if (directory != null && !installerStarted) DeleteDirectory(directory);
            cleanupLease?.Dispose();
        }
    }

    internal async Task DownloadAndVerifyInstallerAsync(UpdateInfo update, string path, IProgress<double>? progress, CancellationToken token)
    {
        if (!IsApprovedUpdate(update)) throw new InvalidDataException("Signed update assets are required.");
        var manifest = await DownloadVerifiedManifestAsync(update, token).ConfigureAwait(false);
        await DownloadInstallerAsync(update.DownloadUrl, path, progress, token, manifest.Size).ConfigureAwait(false);
        var actualHash = await ComputeSha256Async(path, token).ConfigureAwait(false);
        if (!HashesMatch(manifest.Sha256, actualHash))
            throw new InvalidDataException("Installer SHA-256 does not match the signed manifest.");
        var signature = _signatureValidator(path);
        if (!signature.IsValid) throw new InvalidDataException(signature.Reason);
    }

    internal async Task DownloadInstallerAsync(string url, string path, IProgress<double>? progress, CancellationToken token, long? expectedSize = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendHttpsAsync(request, token, requireReleaseOrigin: true).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        if (length is > UpdateSecurityPolicy.MaximumInstallerBytes) throw new InvalidDataException("Installer exceeds 500 MB limit.");
        if (expectedSize.HasValue && length.HasValue && length != expectedSize)
            throw new InvalidDataException("Installer length does not match the signed manifest.");
        progress?.Report(length is > 0 ? 0 : -1);
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920]; long received = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false); if (count == 0) break;
            received += count; if (received > UpdateSecurityPolicy.MaximumInstallerBytes) throw new InvalidDataException("Installer exceeds 500 MB limit.");
            if (expectedSize.HasValue && received > expectedSize.Value)
                throw new InvalidDataException("Installer exceeds the signed size.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (length is > 0) progress?.Report(received * 100d / length.Value);
        }
        if (expectedSize.HasValue && received != expectedSize.Value)
            throw new InvalidDataException("Installer is truncated.");
        progress?.Report(100);
    }

    internal async Task<SignedUpdateManifest> DownloadVerifiedManifestAsync(UpdateInfo update, CancellationToken token)
    {
        var payload = await DownloadSmallAssetAsync(update.ManifestUrl, SignedUpdateManifest.MaximumManifestBytes, token).ConfigureAwait(false);
        var signature = await DownloadSmallAssetAsync(update.ManifestSignatureUrl, 64, token).ConfigureAwait(false);
        return SignedUpdateManifest.Verify(payload, signature, _manifestPublicKey,
            update.Version, update.InstallerName, CurrentVersion);
    }

    private async Task<byte[]> DownloadSmallAssetAsync(string url, int limit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendHttpsAsync(request, token, requireReleaseOrigin: true).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit)
            throw new InvalidDataException("Update metadata exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await input.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (count == 0) break;
            if (output.Length + count > limit)
                throw new InvalidDataException("Update metadata exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    internal async Task<HttpResponseMessage> SendHttpsAsync(HttpRequestMessage request, CancellationToken token,
        bool requireReleaseOrigin = false)
    {
        if (!IsHttps(request.RequestUri)) throw new InvalidOperationException("Only HTTPS update URLs are accepted.");
        if (requireReleaseOrigin && !AppIntegrityService.IsOfficialReleaseUrl(request.RequestUri!))
            throw new InvalidOperationException("Update assets must originate from official GitHub releases.");
        const int maxRedirects = 5;
        HttpRequestMessage? ownedRequest = null; // tracks redirect requests we created and must dispose
        try
        {
            for (var redirects = 0; redirects < maxRedirects; redirects++)
            {
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    var finalUri = response.RequestMessage?.RequestUri ?? request.RequestUri;
                    if (!IsHttps(finalUri) || (requireReleaseOrigin && !AppIntegrityService.IsTrustedReleaseRedirect(finalUri!)))
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
                if (requireReleaseOrigin && !AppIntegrityService.IsTrustedReleaseRedirect(target))
                    throw new InvalidOperationException("Update redirect target is not a trusted release host.");
                var redirectedRequest = new HttpRequestMessage(HttpMethod.Get, target);
                // Only forward the updater's non-sensitive representation/cache headers.
                // Credentials, cookies and an explicit Host must not follow redirects.
                foreach (string header in new[] { "Accept", "If-None-Match" })
                    if (request.Headers.TryGetValues(header, out var values))
                        redirectedRequest.Headers.TryAddWithoutValidation(header, values);
                ownedRequest?.Dispose();
                ownedRequest = redirectedRequest;
                request = ownedRequest;
            }
        }
        finally
        {
            ownedRequest?.Dispose();
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
        foreach (var asset in assets.EnumerateArray().Where(a => a.GetProperty("name").GetString() == name))
        {
            return asset.GetProperty("browser_download_url").GetString() ?? "";
        }
        return "";
    }
    internal static bool IsApprovedUpdate(UpdateInfo update) =>
        (update.InstallerName is SetupName or SelfContainedSetupName) &&
        IsTrustedUpdateUrl(update.DownloadUrl) &&
        IsTrustedUpdateUrl(update.ManifestUrl) &&
        IsTrustedUpdateUrl(update.ManifestSignatureUrl);

    private static bool IsTrustedUpdateUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        AppIntegrityService.IsOfficialReleaseUrl(uri);
    internal static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    internal static bool HashesMatch(string expected, string actual) =>
        expected.Length == 64 && actual.Length == 64 &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false));
    }
    private static HttpClient CreateHttpClient() => new(NetworkPrivacy.Handler(NetworkFeature.Updates, new HttpClientHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(10) };
    private static void DeleteDirectory(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (Exception ex) { RuntimeLog.Warn(LogCategory, $"Could not remove temporary update files: {ex.Message}"); } }
    internal static int CompareVersions(string left, string right)
    {
        if (!TryParseReleaseVersion(left, out var a, out var aPre) ||
            !TryParseReleaseVersion(right, out var b, out var bPre))
            return 0;
        int comparison = a.CompareTo(b);
        if (comparison != 0) return comparison;
        if (aPre.Length == 0 || bPre.Length == 0)
            return aPre.Length == bPre.Length ? 0 : aPre.Length == 0 ? 1 : -1;
        for (int i = 0; i < Math.Min(aPre.Length, bPre.Length); i++)
        {
            bool aNumeric = aPre[i].All(char.IsAsciiDigit);
            bool bNumeric = bPre[i].All(char.IsAsciiDigit);
            if (aNumeric && bNumeric)
            {
                comparison = aPre[i].Length.CompareTo(bPre[i].Length);
                if (comparison == 0) comparison = string.CompareOrdinal(aPre[i], bPre[i]);
            }
            else if (aNumeric != bNumeric) comparison = aNumeric ? -1 : 1;
            else comparison = string.CompareOrdinal(aPre[i], bPre[i]);
            if (comparison != 0) return comparison;
        }
        return aPre.Length.CompareTo(bPre.Length);
    }

    private static bool TryParseReleaseVersion(string value, out Version version, out string[] prerelease)
    {
        version = new Version(0, 0, 0, 0);
        prerelease = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        int metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            if (!ValidIdentifiers(value[(metadata + 1)..], numericLeadingZerosAllowed: true)) return false;
            value = value[..metadata];
        }
        int suffix = value.IndexOf('-');
        if (suffix >= 0)
        {
            string pre = value[(suffix + 1)..];
            if (!ValidIdentifiers(pre, numericLeadingZerosAllowed: false)) return false;
            prerelease = pre.Split('.');
            value = value[..suffix];
        }
        if (!Version.TryParse(value, out var parsed)) return false;
        version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
        return true;
    }

    private static bool ValidIdentifiers(string value, bool numericLeadingZerosAllowed) => value.Split('.').All(part =>
        part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        (numericLeadingZerosAllowed || part.Length == 1 || part[0] != '0' || !part.All(char.IsAsciiDigit)));
    private static UpdateInfo Clone(UpdateInfo source) => new() { Version = source.Version, DownloadUrl = source.DownloadUrl, ChecksumUrl = source.ChecksumUrl, ManifestUrl = source.ManifestUrl, ManifestSignatureUrl = source.ManifestSignatureUrl, InstallerName = source.InstallerName, ReleaseNotes = source.ReleaseNotes, PublishedAt = source.PublishedAt, IsNewerVersion = source.IsNewerVersion };
}
