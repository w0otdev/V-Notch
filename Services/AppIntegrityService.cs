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

    private static readonly HttpClient HttpClientInstance = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient(NetworkPrivacy.Handler(NetworkFeature.Updates, new NativeWinHttpHandler { AllowAutoRedirect = true }))
        {
            Timeout = TimeSpan.FromSeconds(15)
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
        await using var stream = File.OpenRead(filePath);
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
    /// Verifies the running binary's SHA-256 hash against official checksum assets on GitHub Releases.
    /// </summary>
    public static async Task<IntegrityCheckStatus> VerifyChecksumAsync(
        string filePath,
        string version,
        HttpClient? customClient = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return IntegrityCheckStatus.Skipped;

        var client = customClient ?? HttpClientInstance;

        try
        {
            string actualHash = await ComputeFileSha256Async(filePath, cancellationToken).ConfigureAwait(false);
            string tagWithoutV = version.TrimStart('v');
            string tagWithV = $"v{tagWithoutV}";

            HttpResponseMessage? response = null;
            foreach (var tag in new[] { tagWithoutV, tagWithV })
            {
                var apiUrl = $"https://api.github.com/repos/{OfficialRepoOwner}/{OfficialRepoName}/releases/tags/{tag}";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    response = resp;
                    break;
                }
                if (response == null || resp.StatusCode != HttpStatusCode.NotFound)
                {
                    response = resp;
                }
            }

            if (response == null || response.StatusCode == HttpStatusCode.NotFound)
            {
                // Version does not exist on GitHub Releases (unreleased version or private fork)
                return IntegrityCheckStatus.ReleaseNotFound;
            }

            if (!response.IsSuccessStatusCode)
            {
                return IntegrityCheckStatus.NetworkError;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(content);
            if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.GetArrayLength() == 0)
            {
                return IntegrityCheckStatus.Skipped;
            }

            string targetFileName = Path.GetFileName(filePath);
            bool isInstaller = targetFileName.Contains("Setup", StringComparison.OrdinalIgnoreCase);

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
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    foundAnyChecksumAsset = true;
                    try
                    {
                        var checksumData = await client.GetStringAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
                        readAnyChecksumAsset = true;
                        if (checksumData.Contains(actualHash, StringComparison.OrdinalIgnoreCase))
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
    public static async Task StartBackgroundCheckAsync(TimeSpan? delay = null)
    {
        if (IsDevOrTestEnvironment())
            return;

        try
        {
            await Task.Delay(delay ?? TimeSpan.FromSeconds(5));

            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
                return;

            // 1. Check MOTW (Zone.Identifier)
            var (isTrustedOrigin, untrustedUrl) = CheckDownloadOrigin(exePath);
            if (!isTrustedOrigin && !string.IsNullOrWhiteSpace(untrustedUrl))
            {
                RuntimeLog.Warn(LogCategory, $"Application was downloaded from untrusted origin: {untrustedUrl}");
                ShowIntegrityAlert(
                    Loc.Get("integrity.warningTitle"),
                    Loc.Get("integrity.untrustedSource", untrustedUrl));
                return;
            }

            // 2. Check GitHub Release Checksum
            string version = GetAppVersion();
            var status = await VerifyChecksumAsync(exePath, version);
            if (status == IntegrityCheckStatus.HashMismatch)
            {
                RuntimeLog.Warn(LogCategory, $"Application hash mismatch with official release v{version}");
                ShowIntegrityAlert(
                    Loc.Get("integrity.warningTitle"),
                    Loc.Get("integrity.hashMismatch", version));
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Background integrity verification encountered an error");
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
            Process.Start(new ProcessStartInfo(OfficialReleasesUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, "Failed to open official releases repository URL");
        }
    }

    public static string GetAppVersion() => AppVersion.Current;
}
