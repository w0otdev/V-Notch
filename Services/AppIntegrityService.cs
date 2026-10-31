using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VNotch.Models;
using VNotch.Windows;

namespace VNotch.Services;

public enum IntegrityCheckStatus
{
    Verified,
    HashMismatch,
    ReleaseNotFound,
    NetworkError,
    Skipped
}

public static partial class AppIntegrityService
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_integrity_parse_zone_identifier(
        ReadOnlySpan<char> contentPtr,
        int contentLen,
        out int outHostStart,
        out int outHostLen,
        out int outRefStart,
        out int outRefLen);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_integrity_is_trusted_download_uri(
        byte isAbsolute,
        ReadOnlySpan<char> schemePtr,
        int schemeLen,
        int port,
        ReadOnlySpan<char> userInfoPtr,
        int userInfoLen,
        ReadOnlySpan<char> hostPtr,
        int hostLen,
        ReadOnlySpan<char> absPathPtr,
        int absPathLen,
        out byte outTrusted);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_integrity_is_official_release_url(
        byte isAbsolute,
        ReadOnlySpan<char> schemePtr,
        int schemeLen,
        int port,
        ReadOnlySpan<char> userInfoPtr,
        int userInfoLen,
        ReadOnlySpan<char> hostPtr,
        int hostLen,
        ReadOnlySpan<char> absPathPtr,
        int absPathLen,
        out byte outOfficial);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_integrity_is_release_asset_host(
        ReadOnlySpan<char> hostPtr,
        int hostLen,
        out byte outAssetHost);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_integrity_is_trusted_release_redirect(
        byte isAbsolute,
        ReadOnlySpan<char> schemePtr,
        int schemeLen,
        int port,
        ReadOnlySpan<char> userInfoPtr,
        int userInfoLen,
        ReadOnlySpan<char> hostPtr,
        int hostLen,
        ReadOnlySpan<char> absPathPtr,
        int absPathLen,
        out byte outTrusted);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_app_integrity_is_applicable_checksum_asset(
        ReadOnlySpan<char> assetNamePtr,
        int assetNameLen,
        byte isInstaller,
        out byte outApplicable);

    private const string LogCategory = "INTEGRITY";
    private const string OfficialRepoOwner = "w0otdev";
    private const string OfficialRepoName = "V-Notch";
#pragma warning disable S1075 // Official project repository releases URL
    public const string OfficialReleasesUrl = "https://github.com/w0otdev/V-Notch/releases";
#pragma warning restore S1075
    private const string UserAgent = "V-Notch-Integrity-Checker";
    internal const int MaxResponseBytes = 1024 * 1024;

    private static readonly HttpClient HttpClientInstance = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(NetworkPrivacy.Handler(NetworkFeature.Updates, new NativeWinHttpHandler { AllowAutoRedirect = true }))
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        return client;
    }

    /// <summary>
    /// Checks whether the binary was downloaded from a trusted official domain by reading the Zone.Identifier NTFS stream.
    /// Returns (IsTrusted, UntrustedUrl).
    /// </summary>
    public static (bool IsTrusted, string? UntrustedUrl) CheckDownloadOrigin(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return (true, null);

        try
        {
            string zoneIdentifierPath = $"{filePath}:Zone.Identifier";
            if (!File.Exists(zoneIdentifierPath))
            {
                // No Mark of the Web stream present (e.g. unblocked, local build, or non-NTFS).
                return (true, null);
            }

            string content = File.ReadAllText(zoneIdentifierPath);
            var (hostUrl, referrerUrl) = ParseZoneIdentifier(content);

            if (!string.IsNullOrWhiteSpace(hostUrl) && !IsTrustedDownloadDomain(hostUrl))
            {
                return (false, hostUrl);
            }

            if (!string.IsNullOrWhiteSpace(referrerUrl) && !IsTrustedDownloadDomain(referrerUrl))
            {
                return (false, referrerUrl);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"Could not read Zone.Identifier: {ex.Message}");
            return (true, null);
        }
    }

    /// <summary>
    /// Parses Zone.Identifier stream content to extract HostUrl and ReferrerUrl.
    /// </summary>
    public static (string? HostUrl, string? ReferrerUrl) ParseZoneIdentifier(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return (null, null);

        ReadOnlySpan<char> span = content.AsSpan();
        vn_app_integrity_parse_zone_identifier(
            span, span.Length,
            out int hostStart, out int hostLen,
            out int refStart, out int refLen);

        string? hostUrl = hostStart >= 0 ? content.Substring(hostStart, hostLen) : null;
        string? referrerUrl = refStart >= 0 ? content.Substring(refStart, refLen) : null;
        return (hostUrl, referrerUrl);
    }

    /// <summary>
    /// Validates whether a URL belongs to official GitHub releases or trusted CDNs.
    /// </summary>
    public static bool IsTrustedDownloadDomain(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        // "about:internet" is a generic placeholder assigned by older IE/Windows components
        if (url.Equals("about:internet", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        ReadOnlySpan<char> scheme = uri.Scheme.AsSpan();
        ReadOnlySpan<char> userInfo = uri.UserInfo.AsSpan();
        ReadOnlySpan<char> host = uri.Host.AsSpan();
        ReadOnlySpan<char> absPath = uri.AbsolutePath.AsSpan();
        return vn_app_integrity_is_trusted_download_uri(
            uri.IsAbsoluteUri ? (byte)1 : (byte)0,
            scheme, scheme.Length,
            uri.Port,
            userInfo, userInfo.Length,
            host, host.Length,
            absPath, absPath.Length,
            out byte trusted) == 0 && trusted != 0;
    }

    internal static bool IsOfficialReleaseUrl(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;
        ReadOnlySpan<char> scheme = uri.Scheme.AsSpan();
        ReadOnlySpan<char> userInfo = uri.UserInfo.AsSpan();
        ReadOnlySpan<char> host = uri.Host.AsSpan();
        ReadOnlySpan<char> absPath = uri.AbsolutePath.AsSpan();
        return vn_app_integrity_is_official_release_url(
            1,
            scheme, scheme.Length,
            uri.Port,
            userInfo, userInfo.Length,
            host, host.Length,
            absPath, absPath.Length,
            out byte official) == 0 && official != 0;
    }

    internal static bool IsReleaseAssetHost(string host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        ReadOnlySpan<char> span = host.AsSpan();
        return vn_app_integrity_is_release_asset_host(span, span.Length, out byte assetHost) == 0 && assetHost != 0;
    }

    internal static bool IsTrustedReleaseRedirect(Uri uri)
    {
        if (!uri.IsAbsoluteUri) return false;
        ReadOnlySpan<char> scheme = uri.Scheme.AsSpan();
        ReadOnlySpan<char> userInfo = uri.UserInfo.AsSpan();
        ReadOnlySpan<char> host = uri.Host.AsSpan();
        ReadOnlySpan<char> absPath = uri.AbsolutePath.AsSpan();
        return vn_app_integrity_is_trusted_release_redirect(
            1,
            scheme, scheme.Length,
            uri.Port,
            userInfo, userInfo.Length,
            host, host.Length,
            absPath, absPath.Length,
            out byte trusted) == 0 && trusted != 0;
    }

    /// <summary>
    /// Determines whether a release asset is an applicable checksum source for the target binary.
    /// </summary>
    public static bool IsApplicableChecksumAsset(string assetName, bool isInstaller)
    {
        if (string.IsNullOrWhiteSpace(assetName))
            return false;

        ReadOnlySpan<char> span = assetName.AsSpan();
        return vn_app_integrity_is_applicable_checksum_asset(
            span, span.Length, isInstaller ? (byte)1 : (byte)0, out byte applicable) == 0 && applicable != 0;
    }

    /// <summary>
    /// Computes the SHA-256 hash of a file as a lowercase hex string.
    /// </summary>
    public static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeFileSha256Async(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a stream as a lowercase hex string, resetting stream position to 0 when seekable.
    /// </summary>
    public static async Task<string> ComputeFileSha256Async(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
            stream.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Checks whether the running environment is development or test, in which case checks are skipped.
    /// </summary>
    public static bool IsDevOrTestEnvironment()
    {
#if DEBUG
        return true;
#else
        if (Environment.GetEnvironmentVariable("VNOTCH_SKIP_INTEGRITY_CHECK") == "1")
            return true;

        return false;
#endif
    }

    /// <summary>
    /// Builds candidate GitHub release tags for a version string, ordering "v{version}" before "{version}".
    /// </summary>
    public static string[] BuildCandidateReleaseTags(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return [];

        string cleanVersion = version.Trim().TrimStart('v', 'V');
        if (string.IsNullOrEmpty(cleanVersion) || !IsValidReleaseTag(cleanVersion))
            return [];

        return [$"v{cleanVersion}", cleanVersion];
    }

    internal static bool IsValidReleaseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        if (tag.Contains('/') || tag.Contains('\\') || tag.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (char c in tag)
        {
            if (char.IsControl(c) || char.IsWhiteSpace(c) || c is '?' or '#' or '%')
                return false;
        }
        return true;
    }

    internal static bool MatchesChecksumManifest(string checksumData, string actualHash, string targetFileName = "V-Notch.exe")
    {
        if (string.IsNullOrWhiteSpace(checksumData) || string.IsNullOrWhiteSpace(actualHash))
            return false;

        ReadOnlySpan<char> expectedHash = actualHash.AsSpan().Trim();
        if (!IsHex64(expectedHash))
            return false;

        ReadOnlySpan<char> normalizedTarget = NormalizeManifestFileName(targetFileName.AsSpan());
        if (normalizedTarget.IsEmpty)
            normalizedTarget = "V-Notch.exe".AsSpan();

        foreach (ReadOnlySpan<char> rawLine in checksumData.AsSpan().EnumerateLines())
        {
            ReadOnlySpan<char> line = rawLine.Trim();
            if (line.IsEmpty || line[0] == '#' || line[0] == ';')
                continue;

            int commentIdx = line.IndexOf('#');
            if (commentIdx >= 0)
            {
                line = line[..commentIdx].Trim();
                if (line.IsEmpty)
                    continue;
            }

            bool foundMatchingHash = false;
            int fileTokenCount = 0;
            bool allFileTokensMatch = true;

            int i = 0;
            while (i < line.Length)
            {
                while (i < line.Length && (char.IsWhiteSpace(line[i]) || line[i] == ':'))
                    i++;
                if (i >= line.Length)
                    break;

                int start = i;
                while (i < line.Length && !char.IsWhiteSpace(line[i]) && line[i] != ':')
                    i++;

                ReadOnlySpan<char> token = line[start..i];
                if (IsHex64(token) && token.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    foundMatchingHash = true;
                }
                else
                {
                    fileTokenCount++;
                    ReadOnlySpan<char> normalizedFile = NormalizeManifestFileName(token);
                    if (normalizedFile.IsEmpty || !normalizedFile.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        allFileTokensMatch = false;
                    }
                }
            }

            if (foundMatchingHash && (fileTokenCount == 0 || (fileTokenCount == 1 && allFileTokensMatch)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHex64(ReadOnlySpan<char> value)
    {
        if (value.Length != 64)
            return false;
        for (int i = 0; i < 64; i++)
        {
            if (!Uri.IsHexDigit(value[i]))
                return false;
        }
        return true;
    }

    private static ReadOnlySpan<char> NormalizeManifestFileName(ReadOnlySpan<char> rawToken)
    {
        ReadOnlySpan<char> token = rawToken.Trim().Trim(':').Trim("\"'()"u8.Length > 0 ? "\"'()".AsSpan() : default).TrimStart('*').Trim();
        int lastSlash = token.LastIndexOfAny('/', '\\');
        if (lastSlash >= 0)
            token = token[(lastSlash + 1)..];
        return token.Trim();
    }

    private static async Task<string> ReadBoundedStringAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 0 and var contentLength && contentLength > maxBytes)
            throw new InvalidDataException($"Response content length {contentLength} exceeds maximum allowed {maxBytes} bytes.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var ms = new MemoryStream();
        byte[] buffer = new byte[8192];
        int totalRead = 0;
        while (true)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
            if (totalRead > maxBytes)
                throw new InvalidDataException($"Response exceeded maximum allowed {maxBytes} bytes.");
            ms.Write(buffer, 0, read);
        }

        ms.Position = 0;
        using var reader = new StreamReader(ms, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether automatic background integrity network checks are permitted under current settings.
    /// </summary>
    public static bool IsBackgroundCheckAllowed(NotchSettings? settings = null, bool autoCheckUpdates = true)
    {
        if (!autoCheckUpdates)
            return false;

        if (settings is not null)
            return settings.AutoCheckUpdates && !settings.EnableLocalOnlyMode;

        return NetworkPrivacy.Current.IsAllowed(NetworkFeature.Updates);
    }

    /// <summary>
    /// Verifies the running binary's SHA-256 hash against official checksum assets on GitHub Releases.
    /// </summary>
    public static async Task<IntegrityCheckStatus> VerifyChecksumAsync(
        string filePath,
        string version,
        HttpClient? customClient = null,
        CancellationToken cancellationToken = default,
        NotchSettings? settings = null,
        bool autoCheckUpdates = true)
    {
        if (!autoCheckUpdates || (settings is not null && (!settings.AutoCheckUpdates || settings.EnableLocalOnlyMode)))
            return IntegrityCheckStatus.Skipped;

        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return IntegrityCheckStatus.Skipped;

        var client = customClient ?? HttpClientInstance;

        try
        {
            string actualHash = await ComputeFileSha256Async(filePath, cancellationToken).ConfigureAwait(false);

            HttpResponseMessage? response = null;
            foreach (var tag in BuildCandidateReleaseTags(version))
            {
                if (tag.Contains('/') || tag.Contains('\\') || tag.Contains("..", StringComparison.Ordinal))
                    continue;

                var releaseUrl = $"https://api.github.com/repos/{OfficialRepoOwner}/{OfficialRepoName}/releases/tags/{Uri.EscapeDataString(tag)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, releaseUrl);
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    response?.Dispose();
                    response = resp;
                    break;
                }
                if (response != null && resp.StatusCode == HttpStatusCode.NotFound) resp.Dispose();
                else if (response == null || resp.StatusCode != HttpStatusCode.NotFound)
                {
                    response?.Dispose();
                    response = resp;
                }
            }

            if (response == null || response.StatusCode == HttpStatusCode.NotFound)
            {
                // Version does not exist on GitHub Releases (unreleased version or private fork)
                response?.Dispose();
                return IntegrityCheckStatus.ReleaseNotFound;
            }

            if (!response.IsSuccessStatusCode)
            {
                response?.Dispose();
                return IntegrityCheckStatus.NetworkError;
            }

            string content;
            try
            {
                content = await ReadBoundedStringAsync(response, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
                response = null;
            }
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0)
            {
                return IntegrityCheckStatus.Skipped;
            }

            string targetFileName = Path.GetFileName(filePath);
            bool isInstaller = targetFileName.Contains("Setup", StringComparison.OrdinalIgnoreCase);
            string manifestTargetName = targetFileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                ? "V-Notch.exe"
                : targetFileName;

            bool foundAnyChecksumAsset = false;
            bool readAnyChecksumAsset = false;
            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString() ?? "";
                if (!IsApplicableChecksumAsset(assetName, isInstaller))
                {
                    continue;
                }

                var downloadUrl = asset.GetProperty("browser_download_url").GetString();
                if (!string.IsNullOrEmpty(downloadUrl) && Uri.TryCreate(downloadUrl, UriKind.Absolute, out var assetUri) && assetUri.Scheme == Uri.UriSchemeHttps && (customClient != null || IsOfficialReleaseUrl(assetUri)))
                {
                    foundAnyChecksumAsset = true;
                    try
                    {
                        using var checksumResp = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                        checksumResp.EnsureSuccessStatusCode();
                        var checksumData = await ReadBoundedStringAsync(checksumResp, MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                        readAnyChecksumAsset = true;
                        if (MatchesChecksumManifest(checksumData, actualHash, manifestTargetName) ||
                            (!isInstaller && !string.Equals(manifestTargetName, "V-Notch.exe", StringComparison.OrdinalIgnoreCase) &&
                             MatchesChecksumManifest(checksumData, actualHash, "V-Notch.exe")))
                        {
                            return IntegrityCheckStatus.Verified;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RuntimeLog.Warn(LogCategory, $"Failed to download checksum asset {assetName}: {ex.Message}");
                    }
                }
            }

            if (!foundAnyChecksumAsset)
            {
                return IntegrityCheckStatus.Skipped;
            }

            if (!readAnyChecksumAsset)
            {
                response?.Dispose();
                return IntegrityCheckStatus.NetworkError;
            }

            // Checksum files were found and examined, but none matched the running binary's hash
            return IntegrityCheckStatus.HashMismatch;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn(LogCategory, $"Integrity check network request failed: {ex.Message}");
            return IntegrityCheckStatus.NetworkError;
        }
    }

    /// <summary>
    /// Executes the integrity check in the background after startup.
    /// </summary>
    public static async Task<IntegrityCheckStatus> StartBackgroundCheckAsync(
        TimeSpan? delay = null,
        NotchSettings? settings = null,
        bool autoCheckUpdates = true,
        HttpClient? customClient = null,
        string? executablePath = null,
        bool ignoreDevEnvironment = false,
        CancellationToken cancellationToken = default)
    {
        if (!IsBackgroundCheckAllowed(settings, autoCheckUpdates))
            return IntegrityCheckStatus.Skipped;

        if (!ignoreDevEnvironment && IsDevOrTestEnvironment())
            return IntegrityCheckStatus.Skipped;

        try
        {
            await Task.Delay(delay ?? TimeSpan.FromSeconds(5), cancellationToken);

            if (!IsBackgroundCheckAllowed(settings, autoCheckUpdates) ||
                (customClient is null && !NetworkPrivacy.Current.IsAllowed(NetworkFeature.Updates)))
            {
                return IntegrityCheckStatus.Skipped;
            }

            string? exePath = executablePath ?? Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return IntegrityCheckStatus.Skipped;

            // 1. Check MOTW (Zone.Identifier)
            var (isTrustedOrigin, untrustedUrl) = CheckDownloadOrigin(exePath);
            if (!isTrustedOrigin && !string.IsNullOrWhiteSpace(untrustedUrl))
            {
                RuntimeLog.Warn(LogCategory, $"Application was downloaded from untrusted origin: {untrustedUrl}");
                ShowIntegrityAlert(
                    Loc.Get("integrity.warningTitle"),
                    Loc.Get("integrity.untrustedSource", untrustedUrl));
                return IntegrityCheckStatus.Skipped;
            }

            // 2. Check GitHub Release Checksum
            string version = GetAppVersion();
            var status = await VerifyChecksumAsync(exePath, version, customClient, cancellationToken, settings, autoCheckUpdates);
            if (status == IntegrityCheckStatus.HashMismatch)
            {
                RuntimeLog.Warn(LogCategory, $"Application hash mismatch with official release v{version}");
                ShowIntegrityAlert(
                    Loc.Get("integrity.warningTitle"),
                    Loc.Get("integrity.hashMismatch", version));
            }

            return status;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return IntegrityCheckStatus.Skipped;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Background integrity verification encountered an error");
            return IntegrityCheckStatus.NetworkError;
        }
    }

    /// <summary>
    /// Displays a user-facing security alert with an option to open the official releases repository.
    /// </summary>
    public static void ShowIntegrityAlert(string title, string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            try
            {
                var options = new ConfirmationDialog.DialogOptions(
                    Title: title,
                    ConfirmText: Loc.Get("integrity.openOfficialRepo"),
                    CancelText: Loc.Get("integrity.ignore"),
                    Icon: ConfirmationDialog.DialogIcon.Warning,
                    Style: ConfirmationDialog.DialogStyle.Danger);

                bool openRepo = ConfirmationDialog.Show(Application.Current.MainWindow, message, options);
                if (openRepo)
                {
                    OpenOfficialRepo();
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Error(LogCategory, ex, "Failed to show ConfirmationDialog, falling back to MessageBox");
                var result = MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (result == MessageBoxResult.OK)
                {
                    OpenOfficialRepo();
                }
            }
        });
    }

    private static void OpenOfficialRepo()
    {
        try
        {
            SafeLauncher.TryOpenUrl(OfficialReleasesUrl);
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Failed to open official releases repository URL");
        }
    }

    public static string GetAppVersion() => AppVersion.Current;
}
