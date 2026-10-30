using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace VNotch.Services;

#pragma warning disable S1075 // Public GitHub Releases API endpoints
public sealed partial class UpdateService : IUpdateService, IDisposable
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_update_service_try_parse_release_version(
        ReadOnlySpan<char> valuePtr,
        int valueLen,
        out int outMajor,
        out int outMinor,
        out int outBuild,
        out int outRevision,
        out int outPreStart,
        out int outPreLen,
        out byte outOk);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_update_service_compare_versions(
        ReadOnlySpan<char> leftPtr,
        int leftLen,
        ReadOnlySpan<char> rightPtr,
        int rightLen,
        out int outCmp);

    private const string LogCategory = "UPDATER";
    private static readonly Uri GithubLatestReleaseUri = new("https://api.github.com/repos/w0otdev/V-Notch/releases/latest");
    private static readonly Uri GithubAllReleasesUri = new("https://api.github.com/repos/w0otdev/V-Notch/releases");
    private const string UserAgent = "V-Notch-Updater";
    internal const string SetupName = "V-Notch-Setup.exe";
    internal const string SelfContainedSetupName = "V-Notch-Setup-SelfContained.exe";
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromHours(1);
    private readonly HttpClient _httpClient;
    private readonly UpdateSecurityPolicy _securityPolicy;
    private readonly Func<string, (bool IsValid, string Reason)> _signatureValidator;
    private readonly string _manifestPublicKey;
    private const int MaxReleaseResponseBytes = 2 * 1024 * 1024;
    private const int MaxReleasePages = 5;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private string? _latestReleaseEtag;
    private UpdateInfo? _cachedLatestRelease;
    private IReadOnlyList<UpdateInfo>? _cachedAllReleases;
    private DateTime _lastAllReleasesCheckUtc = DateTime.MinValue;
    private DateTime _lastCheckUtc = DateTime.MinValue;
    private DateTime _retryAfterUtc = DateTime.MinValue;
    private readonly string? _cachePath;
    private bool _cachedIncludesPrereleases;
    private volatile bool _disposed;

    private readonly bool _ownsHttpClient;

    public UpdateService() : this(CreateHttpClient(), UpdateSecurityPolicy.FromEnvironment(),
        cachePath: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V-Notch", "update-cache.json"))
    { _ownsHttpClient = true; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    internal UpdateService(HttpClient httpClient, UpdateSecurityPolicy securityPolicy,
        Func<string, (bool IsValid, string Reason)>? signatureValidator = null,
        string? manifestPublicKey = null, string? cachePath = null)
    {
        _httpClient = httpClient;
        _securityPolicy = securityPolicy;
        _cachePath = cachePath;
        _manifestPublicKey = manifestPublicKey ?? SignedUpdateManifest.ReadPinnedPublicKey();
        _signatureValidator = signatureValidator ?? (path =>
        {
            var valid = _securityPolicy.IsTrustedSignature(path, out var reason);
            return (valid, reason);
        });
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        LoadCheckCache();
    }

    public string CurrentVersion => GetAppVersion();
    public event EventHandler<UpdateInfo?>? UpdateCheckCompleted;
    public UpdateInfo? LatestUpdateInfo => Volatile.Read(ref _cachedLatestRelease) is { } cached ? Clone(cached) : null;

    private static string GetAppVersion() => AppVersion.Current;

    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool includePrereleases = false, bool forceRefresh = false)
    {
        if (_disposed) return null;
        UpdateInfo? result = null;
        await _checkLock.WaitAsync();
        try
        {
            if (_cachedIncludesPrereleases != includePrereleases)
            {
                _cachedIncludesPrereleases = includePrereleases;
                Volatile.Write(ref _cachedLatestRelease, null);
                _latestReleaseEtag = null;
                _lastCheckUtc = DateTime.MinValue;
            }
            var now = DateTime.UtcNow;
            var currentCached = Volatile.Read(ref _cachedLatestRelease);
            if (!forceRefresh && (now < _retryAfterUtc || now - _lastCheckUtc < MinRefreshInterval))
            {
                result = currentCached != null ? Clone(currentCached) : null;
                return result;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, includePrereleases
                ? new Uri(GithubAllReleasesUri + "?per_page=100&page=1") : GithubLatestReleaseUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            if (!string.IsNullOrWhiteSpace(_latestReleaseEtag)) request.Headers.TryAddWithoutValidation("If-None-Match", _latestReleaseEtag);
            using var response = await SendHttpsAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && currentCached != null)
            {
                _lastCheckUtc = now;
                _retryAfterUtc = DateTime.MinValue;
                await SaveCheckCacheAsync().ConfigureAwait(false);
                result = Clone(currentCached);
                return result;
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                var retryAt = response.Headers.RetryAfter?.Date?.UtcDateTime ??
                    now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromHours(1));
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
                    long.TryParse(resets.FirstOrDefault(), out long epoch) && epoch is > 0 and < 253402300800)
                    retryAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                _retryAfterUtc = now + TimeSpan.FromSeconds(Math.Clamp((retryAt - now).TotalSeconds, 60, 86400));
                await SaveCheckCacheAsync().ConfigureAwait(false);
                RuntimeLog.Warn(LogCategory, $"Update API rate limited; next check after {_retryAfterUtc:O}");
                result = currentCached != null ? Clone(currentCached) : null;
                return result;
            }
            response.EnsureSuccessStatusCode();
            using var jsonDoc = await ReadLimitedJsonAsync(response, MaxReleaseResponseBytes, CancellationToken.None).ConfigureAwait(false)
                ?? throw new InvalidDataException("Release metadata response was empty or exceeded size limit.");
            UpdateInfo? info;
            if (includePrereleases)
            {
                var firstPageReleases = new List<UpdateInfo>();
                foreach (var release in jsonDoc.RootElement.EnumerateArray())
                {
                    if (!IsDraft(release)) firstPageReleases.Add(ReadRelease(release));
                }
                _cachedAllReleases = firstPageReleases;
                _lastAllReleasesCheckUtc = now;

                info = SelectLatestRelease(jsonDoc.RootElement, null);
                int count = jsonDoc.RootElement.GetArrayLength();
                for (int page = 2; count == 100 && page <= MaxReleasePages; page++)
                {
                    using var pageRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(GithubAllReleasesUri + $"?per_page=100&page={page}"));
                    pageRequest.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    using var pageResponse = await SendHttpsAsync(pageRequest, CancellationToken.None).ConfigureAwait(false);
                    pageResponse.EnsureSuccessStatusCode();
                    using var pageJson = await ReadLimitedJsonAsync(pageResponse, MaxReleaseResponseBytes, CancellationToken.None).ConfigureAwait(false);
                    if (pageJson == null) break;
                    info = SelectLatestRelease(pageJson.RootElement, info);
                    count = pageJson.RootElement.GetArrayLength();
                }
            }
            else
            {
                var root = jsonDoc.RootElement;
                info = IsDraft(root) ? null : ReadRelease(root);
                if (info?.IsPrerelease == true) info = null;
            }
            if (info != null && !IsApprovedUpdate(info)) RuntimeLog.Warn(LogCategory, "Latest release is missing signed update assets; update is unavailable.");
            _lastCheckUtc = now;
            _retryAfterUtc = DateTime.MinValue;
            _latestReleaseEtag = info != null ? response.Headers.ETag?.ToString() : null;
            Volatile.Write(ref _cachedLatestRelease, info);
            await SaveCheckCacheAsync().ConfigureAwait(false);
            result = info != null ? Clone(info) : null;
            return result;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Update check failed");
            var fallback = Volatile.Read(ref _cachedLatestRelease);
            result = fallback != null ? Clone(fallback) : null;
            return result;
        }
        finally
        {
            _checkLock.Release();
            var handlers = UpdateCheckCompleted?.GetInvocationList();
            if (handlers != null) { var arg = result != null ? Clone(result) : null; foreach (var d in handlers) { try { ((EventHandler<UpdateInfo?>)d)(this, arg); } catch (Exception ex) { RuntimeLog.Error(LogCategory, ex, "UpdateCheckCompleted handler threw"); } } }

        }
    }

    private sealed record CheckCache(DateTime LastCheckUtc, DateTime RetryAfterUtc,
        string? Etag, bool IncludesPrereleases, UpdateInfo? Release);

    private void LoadCheckCache()
    {
        if (_cachePath == null) return;
        try
        {
            if (!File.Exists(_cachePath) || new FileInfo(_cachePath).Length > MaxReleaseResponseBytes) return;
            var cache = JsonSerializer.Deserialize<CheckCache>(File.ReadAllText(_cachePath));
            var now = DateTime.UtcNow;
            if (cache == null || cache.LastCheckUtc > now || cache.RetryAfterUtc > now.AddDays(1)) return;
            _lastCheckUtc = cache.LastCheckUtc;
            _retryAfterUtc = cache.RetryAfterUtc;
            _cachedIncludesPrereleases = cache.IncludesPrereleases;
            _cachedLatestRelease = cache.Release != null && IsApprovedUpdate(cache.Release) &&
                TryParseReleaseVersion(cache.Release.Version, out _, out _) ? cache.Release : null;
            _latestReleaseEtag = _cachedLatestRelease != null ? cache.Etag : null;
            if (_cachedLatestRelease != null)
                _cachedLatestRelease.IsNewerVersion = CompareVersions(_cachedLatestRelease.Version, CurrentVersion) > 0;
        }
        catch (Exception ex) { RuntimeLog.Warn(LogCategory, $"Update cache ignored: {ex.Message}"); }
    }

    private async Task SaveCheckCacheAsync()
    {
        if (_cachePath == null) return;
        string temp = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath))!);
            var cache = new CheckCache(_lastCheckUtc, _retryAfterUtc, _latestReleaseEtag,
                _cachedIncludesPrereleases, _cachedLatestRelease);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cache)).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) { RuntimeLog.Warn(LogCategory, $"Update cache was not saved: {ex.Message}"); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* Ignore temp delete failure */ } }
    }

    private UpdateInfo? SelectLatestRelease(JsonElement releases, UpdateInfo? latest)
    {
        foreach (var release in releases.EnumerateArray())
        {
            if (IsDraft(release)) continue;
            var candidate = ReadRelease(release);
            if (!TryParseReleaseVersion(candidate.Version, out _, out _) || !IsApprovedUpdate(candidate)) continue;
            if (latest == null || CompareVersions(candidate.Version, latest.Version) > 0) latest = candidate;
        }
        return latest;
    }

    private static bool IsDraft(JsonElement release) => release.TryGetProperty("draft", out var draft) && draft.GetBoolean();

    private UpdateInfo ReadRelease(JsonElement release)
    {
        var (installer, _) = SelectReleaseAssets(release);
        var info = new UpdateInfo
        {
            Version = release.GetProperty("tag_name").GetString()?.TrimStart('v', 'V') ?? string.Empty,
            DownloadUrl = installer?.Url ?? string.Empty,
            ManifestUrl = FindAssetUrl(release, installer?.Name + SignedUpdateManifest.ManifestSuffix),
            ManifestSignatureUrl = FindAssetUrl(release, installer?.Name + SignedUpdateManifest.SignatureSuffix),
            InstallerName = installer?.Name ?? string.Empty,
            ReleaseNotes = release.GetProperty("body").GetString() ?? string.Empty,
            PublishedAt = release.GetProperty("published_at").GetDateTime(),
            IsPrerelease = (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) ||
                (TryParseReleaseVersion(release.GetProperty("tag_name").GetString() ?? "", out _, out var suffix) && suffix.Length > 0)
        };
        info.IsNewerVersion = IsApprovedUpdate(info) && CompareVersions(info.Version, CurrentVersion) > 0;
        return info;
    }

    public async Task<IReadOnlyList<UpdateInfo>> GetAllReleasesAsync()
    {
        if (_disposed) return Array.Empty<UpdateInfo>();
        var now = DateTime.UtcNow;
        if (_cachedAllReleases != null && (now < _retryAfterUtc || now - _lastAllReleasesCheckUtc < MinRefreshInterval))
        {
            return _cachedAllReleases.Select(Clone).ToList();
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubAllReleasesUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            using var response = await SendHttpsAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                var retryAt = response.Headers.RetryAfter?.Date?.UtcDateTime ??
                    now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromHours(1));
                if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
                    long.TryParse(resets.FirstOrDefault(), out long epoch) && epoch is > 0 and < 253402300800)
                    retryAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
                _retryAfterUtc = now + TimeSpan.FromSeconds(Math.Clamp((retryAt - now).TotalSeconds, 60, 86400));
                RuntimeLog.Warn(LogCategory, $"GetAllReleases API rate limited; next check after {_retryAfterUtc:O}");
                return _cachedAllReleases != null ? _cachedAllReleases.Select(Clone).ToList() : Array.Empty<UpdateInfo>();
            }
            response.EnsureSuccessStatusCode();

            using var jsonDoc = await ReadLimitedJsonAsync(response, MaxReleaseResponseBytes, CancellationToken.None).ConfigureAwait(false);
            if (jsonDoc == null) return Array.Empty<UpdateInfo>();
            var releases = new List<UpdateInfo>();

            foreach (var release in jsonDoc.RootElement.EnumerateArray())
            {
                if (!IsDraft(release)) releases.Add(ReadRelease(release));
            }
            _cachedAllReleases = releases;
            _lastAllReleasesCheckUtc = now;
            return releases.Select(Clone).ToList();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Get all releases failed");
            return _cachedAllReleases != null ? _cachedAllReleases.Select(Clone).ToList() : Array.Empty<UpdateInfo>();
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
            using var process = Process.Start(CreateInstallerStartInfo(installerPath, directory));
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

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath, string directory) => new()
    {
        FileName = installerPath,
        Arguments = "--update",
        UseShellExecute = true,
        WorkingDirectory = directory
    };

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
        foreach (var asset in assets.EnumerateArray())
        {
            if (string.Equals(asset.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                return asset.GetProperty("browser_download_url").GetString() ?? "";
        }
        return "";
    }
    internal static bool IsApprovedUpdate(UpdateInfo update) =>
        (string.Equals(update.InstallerName, SetupName, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(update.InstallerName, SelfContainedSetupName, StringComparison.OrdinalIgnoreCase)) &&
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
    private static HttpClient CreateHttpClient() => new(NetworkPrivacy.Handler(NetworkFeature.Updates, new NativeWinHttpHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(10) };
    private static void DeleteDirectory(string directory) { try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch (Exception ex) { RuntimeLog.Warn(LogCategory, $"Could not remove temporary update files: {ex.Message}"); } }

    internal static int CompareVersions(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return 0;
        ReadOnlySpan<char> lSpan = left.AsSpan();
        ReadOnlySpan<char> rSpan = right.AsSpan();
        return vn_update_service_compare_versions(lSpan, lSpan.Length, rSpan, rSpan.Length, out int cmp) == 0 ? cmp : 0;
    }

    internal static bool TryParseReleaseVersion(string value, out Version version, out string[] prerelease)
    {
        version = new Version(0, 0, 0, 0);
        prerelease = [];
        if (string.IsNullOrWhiteSpace(value)) return false;

        ReadOnlySpan<char> span = value.AsSpan();
        if (vn_update_service_try_parse_release_version(
                span, span.Length,
                out int major, out int minor, out int build, out int revision,
                out int preStart, out int preLen, out byte ok) != 0 || ok == 0)
        {
            return false;
        }

        version = new Version(major, minor, build, revision);
        if (preStart >= 0 && preLen > 0)
        {
            prerelease = value.Substring(preStart, preLen).Split('.');
        }
        return true;
    }

    private static async Task<JsonDocument?> ReadLimitedJsonAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        var length = response.Content.Headers.ContentLength;
        if (length.HasValue && length.Value > maxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(length.HasValue ? (int)length.Value : 4096);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            int totalRead = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes - totalRead + 1)), token).ConfigureAwait(false);
                if (read == 0) break;
                totalRead += read;
                if (totalRead > maxBytes) return null;
                await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            }
            return totalRead == 0 ? null : JsonDocument.Parse(memory.GetBuffer().AsMemory(0, totalRead));
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static UpdateInfo Clone(UpdateInfo source) => new() { Version = source.Version, DownloadUrl = source.DownloadUrl, ManifestUrl = source.ManifestUrl, ManifestSignatureUrl = source.ManifestSignatureUrl, InstallerName = source.InstallerName, ReleaseNotes = source.ReleaseNotes, PublishedAt = source.PublishedAt, IsNewerVersion = source.IsNewerVersion, IsPrerelease = source.IsPrerelease };
}
