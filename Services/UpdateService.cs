using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace VNotch.Services;

#pragma warning disable S1075 // Public GitHub Releases feed endpoint
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
    private const int CacheSchemaVersion = 2;
    private static readonly Uri GithubReleaseFeedUri = new("https://github.com/w0otdev/V-Notch/releases.atom");

    [LibraryImport("VNotch.Native.dll", EntryPoint = "vn_update_parse_atom", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ParseReleaseFeed(ReadOnlySpan<byte> data, int size, Span<char> output, int capacity, out int written);
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
            if (!forceRefresh && (now - _lastCheckUtc < MinRefreshInterval || now < _retryAfterUtc))
            {
                result = currentCached != null ? Clone(currentCached) : null;
                return result;
            }
            result = await CheckReleaseFeedAsync(includePrereleases).ConfigureAwait(false);
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
        string? Etag, bool IncludesPrereleases, UpdateInfo? Release, int SchemaVersion = 0);

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
            _retryAfterUtc = cache.SchemaVersion == CacheSchemaVersion ? cache.RetryAfterUtc : DateTime.MinValue;
            _cachedIncludesPrereleases = cache.IncludesPrereleases;
            bool isNewer = cache.Release != null &&
                TryParseReleaseVersion(cache.Release.Version, out _, out _) &&
                CompareVersions(cache.Release.Version, CurrentVersion) > 0;
            bool validRelease = cache.Release != null &&
                TryParseReleaseVersion(cache.Release.Version, out _, out _) &&
                (isNewer
                    ? IsApprovedUpdate(cache.Release)
                    : string.IsNullOrEmpty(cache.Release.DownloadUrl) || IsApprovedUpdate(cache.Release));
            _cachedLatestRelease = validRelease ? cache.Release : null;
            _latestReleaseEtag = _cachedLatestRelease != null && cache.SchemaVersion == CacheSchemaVersion ? cache.Etag : null;
            if (_cachedLatestRelease != null)
                _cachedLatestRelease.IsNewerVersion = isNewer && IsApprovedUpdate(_cachedLatestRelease);
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
                _cachedIncludesPrereleases, _cachedLatestRelease, CacheSchemaVersion);
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cache)).ConfigureAwait(false);
            File.Move(temp, _cachePath, overwrite: true);
        }
        catch (Exception ex) { RuntimeLog.Warn(LogCategory, $"Update cache was not saved: {ex.Message}"); }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* Ignore temp delete failure */ } }
    }

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
        return await GetReleaseFeedFallbackAsync().ConfigureAwait(false);
    }

    private async Task<UpdateInfo?> CheckReleaseFeedAsync(bool includePrereleases)
    {
        var cached = Volatile.Read(ref _cachedLatestRelease);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubReleaseFeedUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
            if (!string.IsNullOrWhiteSpace(_latestReleaseEtag) && cached != null)
                request.Headers.TryAddWithoutValidation("If-None-Match", _latestReleaseEtag);

            using var response = await SendHttpsAsync(request, timeout.Token).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            if (response.StatusCode == HttpStatusCode.NotModified && cached != null)
            {
                _lastCheckUtc = now;
                _retryAfterUtc = DateTime.MinValue;
                await SaveCheckCacheAsync().ConfigureAwait(false);
                return Clone(cached);
            }
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                UpdateRetryAfter(response, now);
                if (cached?.IsNewerVersion != true)
                {
                    _latestReleaseEtag = null;
                    Volatile.Write(ref _cachedLatestRelease, null);
                }
                await SaveCheckCacheAsync().ConfigureAwait(false);
                RuntimeLog.Warn(LogCategory, $"Release feed rate limited; next check after {_retryAfterUtc:O}");
                return cached?.IsNewerVersion == true ? Clone(cached) : null;
            }
            response.EnsureSuccessStatusCode();

            var allReleases = await ReadReleaseFeedAsync(response, timeout.Token).ConfigureAwait(false);
            _cachedAllReleases = allReleases;
            _lastAllReleasesCheckUtc = now;

            var releases = allReleases
                .Where(release => TryParseReleaseVersion(release.Version, out _, out _) &&
                    (includePrereleases || !release.IsPrerelease))
                .OrderByDescending(release => release.Version, Comparer<string>.Create(CompareVersions)).ToList();
            UpdateInfo? update = releases.FirstOrDefault();
            if (update != null && CompareVersions(update.Version, CurrentVersion) > 0)
            {
                update = null;
                foreach (var release in releases.Where(release => CompareVersions(release.Version, CurrentVersion) > 0).Take(MaxReleasePages))
                {
                    foreach (string installer in new[] { SelfContainedSetupName, SetupName })
                    {
                        var candidate = Clone(release);
                        string root = "https://github.com/w0otdev/V-Notch/releases/download/" + Uri.EscapeDataString("v" + release.Version) + "/";
                        candidate.InstallerName = installer;
                        candidate.DownloadUrl = root + installer;
                        candidate.ManifestUrl = candidate.DownloadUrl + SignedUpdateManifest.ManifestSuffix;
                        candidate.ManifestSignatureUrl = candidate.DownloadUrl + SignedUpdateManifest.SignatureSuffix;
                        try
                        {
                            // Feed history alone cannot authorize an installer. Verify its signed metadata first.
                            await DownloadVerifiedManifestAsync(candidate, timeout.Token).ConfigureAwait(false);
                            candidate.IsNewerVersion = true;
                            update = candidate;
                            break;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or CryptographicException or JsonException)
                        {
                            RuntimeLog.Warn(LogCategory, $"Public feed update metadata unavailable for {release.Version}/{installer}: {ex.Message}");
                        }
                    }
                    if (update != null) break;
                }
            }
            if (update == null && cached?.IsNewerVersion == true) update = Clone(cached);
            _lastCheckUtc = DateTime.UtcNow;
            _retryAfterUtc = DateTime.MinValue;
            _latestReleaseEtag = update != null ? response.Headers.ETag?.ToString() : null;
            Volatile.Write(ref _cachedLatestRelease, update);
            await SaveCheckCacheAsync().ConfigureAwait(false);
            return update != null ? Clone(update) : null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Public feed update check failed");
            // An old non-newer cache is not evidence that the installed version is current.
            if (cached?.IsNewerVersion == true) return Clone(cached);
            _lastCheckUtc = DateTime.MinValue;
            _latestReleaseEtag = null;
            Volatile.Write(ref _cachedLatestRelease, null);
            await SaveCheckCacheAsync().ConfigureAwait(false);
            return null;
        }
    }

    private async Task<IReadOnlyList<UpdateInfo>> GetReleaseFeedFallbackAsync(bool requireFresh = false, CancellationToken token = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GithubReleaseFeedUri);
            request.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
            using var response = await SendHttpsAsync(request, token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden)
            {
                UpdateRetryAfter(response, DateTime.UtcNow);
                await SaveCheckCacheAsync().ConfigureAwait(false);
                RuntimeLog.Warn(LogCategory, $"GetAllReleases feed rate limited; next check after {_retryAfterUtc:O}");
                if (requireFresh) throw new HttpRequestException("Release feed rate limited.");
                return _cachedAllReleases != null ? _cachedAllReleases.Select(Clone).ToList() : Array.Empty<UpdateInfo>();
            }
            response.EnsureSuccessStatusCode();
            var releases = await ReadReleaseFeedAsync(response, token).ConfigureAwait(false);
            _cachedAllReleases = releases;
            _lastAllReleasesCheckUtc = DateTime.UtcNow;
            return releases.Select(Clone).ToList();
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Release feed fallback failed");
            if (requireFresh) throw;
            return _cachedAllReleases != null ? _cachedAllReleases.Select(Clone).ToList() : Array.Empty<UpdateInfo>();
        }
    }

    private void UpdateRetryAfter(HttpResponseMessage response, DateTime now)
    {
        var retryAt = response.Headers.RetryAfter?.Date?.UtcDateTime ??
            now + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromHours(1));
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) &&
            long.TryParse(resets.FirstOrDefault(), out long epoch) && epoch is > 0 and < 253402300800)
            retryAt = DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime;
        _retryAfterUtc = now + TimeSpan.FromSeconds(Math.Clamp((retryAt - now).TotalSeconds, 60, 86400));
    }

    private async Task<List<UpdateInfo>> ReadReleaseFeedAsync(HttpResponseMessage response, CancellationToken token)
    {
        byte[] feed = await ReadLimitedBytesAsync(response, MaxReleaseResponseBytes, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Release feed response was empty or exceeded size limit.");
        var buffer = new char[MaxReleaseResponseBytes];
        if (ParseReleaseFeed(feed, feed.Length, buffer, buffer.Length, out int written) != 0)
            throw new InvalidDataException("Invalid release feed.");
        using var json = JsonDocument.Parse(new string(buffer, 0, written));
        var releases = new List<UpdateInfo>();
        foreach (var release in json.RootElement.EnumerateArray()) releases.Add(ReadRelease(release));
        if (releases.Count == 0) throw new InvalidDataException("Empty release feed.");
        return releases;
    }

    // ShellExecute, trust verification and temporary-file cleanup can block even
    // when the HTTP operations complete synchronously. Keep the whole pipeline
    // off the dispatcher for both Settings and the notch update notification.
    public Task<bool> DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => DownloadAndInstallUpdateCoreAsync(updateInfo, progress, cancellationToken));

    internal async Task<bool> DownloadAndInstallUpdateCoreAsync(
        UpdateInfo updateInfo,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        Action<string>? installerLauncher = null,
        string? expectedSha256Override = null)
    {
        string? directory = null;
        string? installerPath = null;
        FileStream? installerLock = null;
        IDisposable? cleanupLease = null;
        var installerStarted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            installerLock = await DownloadAndVerifyInstallerWithLockAsync(updateInfo, installerPath, progress, cancellationToken, expectedSha256Override).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (installerLauncher != null)
            {
                installerLauncher(installerPath);
            }
            else
            {
                using var process = Process.Start(CreateInstallerStartInfo(installerPath, directory));
                if (process == null) throw new InvalidOperationException("Could not start verified installer.");
            }
            installerStarted = true;
            RuntimeLog.Log(LogCategory, $"Starting verified installer {updateInfo.InstallerName}.");
            if (installerLauncher == null)
            {
                await App.RequestShutdownAsync();
            }
            return true;
        }
        catch (OperationCanceledException) { RuntimeLog.Warn(LogCategory, "Update download canceled; current application remains open."); return false; }
        catch (Exception ex) { RuntimeLog.Error(LogCategory, ex, "Update download/verification failed; current application remains open"); return false; }
        finally
        {
            installerLock?.Dispose();
            if (directory != null && (!installerStarted || installerLauncher != null)) DeleteDirectory(directory);
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
        await using var _ = await DownloadAndVerifyInstallerWithLockAsync(update, path, progress, token).ConfigureAwait(false);
    }

    internal async Task<FileStream> DownloadAndVerifyInstallerWithLockAsync(
        UpdateInfo update,
        string path,
        IProgress<double>? progress,
        CancellationToken token,
        string? expectedSha256Override = null)
    {
        if (!IsApprovedUpdate(update)) throw new InvalidDataException("Signed update assets are required.");
        string expectedSha256;
        long? expectedSize = null;
        if (!string.IsNullOrEmpty(expectedSha256Override))
        {
            expectedSha256 = expectedSha256Override;
        }
        else
        {
            var manifest = await DownloadVerifiedManifestAsync(update, token).ConfigureAwait(false);
            expectedSha256 = manifest.Sha256;
            expectedSize = manifest.Size;
        }

        await DownloadInstallerAsync(update.DownloadUrl, path, progress, token, expectedSize).ConfigureAwait(false);
        var readLockStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var actualHash = await ComputeSha256Async(readLockStream, token).ConfigureAwait(false);
            if (!HashesMatch(expectedSha256, actualHash))
                throw new InvalidDataException("Installer SHA-256 does not match the signed manifest.");
            var signature = _signatureValidator(path);
            if (!signature.IsValid) throw new InvalidDataException(signature.Reason);
            return readLockStream;
        }
        catch
        {
            await readLockStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
        await output.FlushAsync(token).ConfigureAwait(false);
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
        return await ComputeSha256Async(file, token).ConfigureAwait(false);
    }
    internal static async Task<string> ComputeSha256Async(Stream stream, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek) stream.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
    private static HttpClient CreateHttpClient() => new(NetworkPrivacy.Handler(NetworkFeature.Updates, new NativeWinHttpHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(10) };
    private static void DeleteDirectory(string directory) { try { SafeFileDeletion.DeleteDirectory(directory); } catch (Exception ex) { RuntimeLog.Warn(LogCategory, $"Could not remove temporary update files: {ex.Message}"); } }

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

    private static async Task<byte[]?> ReadLimitedBytesAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
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
            return totalRead == 0 ? null : memory.ToArray();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static UpdateInfo Clone(UpdateInfo source) => new() { Version = source.Version, DownloadUrl = source.DownloadUrl, ManifestUrl = source.ManifestUrl, ManifestSignatureUrl = source.ManifestSignatureUrl, InstallerName = source.InstallerName, ReleaseNotes = source.ReleaseNotes, PublishedAt = source.PublishedAt, IsNewerVersion = source.IsNewerVersion, IsPrerelease = source.IsPrerelease };
}
