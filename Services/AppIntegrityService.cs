using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;

namespace VNotch.Services;

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

    private const string LogCategory = "INTEGRITY";
#pragma warning disable S1075 // Official project repository releases URL
    public const string OfficialReleasesUrl = "https://github.com/w0otdev/V-Notch/releases";
#pragma warning restore S1075

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
            string content;
            try { content = File.ReadAllText(zoneIdentifierPath); }
            catch (FileNotFoundException) { return (true, null); }
            var (hostUrl, referrerUrl) = ParseZoneIdentifier(content);

            bool repositoryReferrer = Uri.TryCreate(referrerUrl, UriKind.Absolute, out var referrer) &&
                referrer.Scheme == Uri.UriSchemeHttps && IsTrustedDownloadDomain(referrerUrl!);
            bool knownHost = Uri.TryCreate(hostUrl, UriKind.Absolute, out var host) &&
                IsTrustedDownloadDomain(hostUrl!) && host.Scheme == Uri.UriSchemeHttps;
            if (!string.IsNullOrWhiteSpace(hostUrl) && !IsTrustedDownloadDomain(hostUrl) &&
                !(repositoryReferrer && host != null && IsTrustedReleaseRedirect(host)))
                return (false, hostUrl);
            if (!string.IsNullOrWhiteSpace(referrerUrl) && !IsTrustedDownloadDomain(referrerUrl) &&
                !(knownHost && referrer == new Uri("https://github.com/")))
                return (false, referrerUrl);
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
    /// Optional UI prompt hook registered by the shell so integrity alerts can render a styled dialog
    /// without coupling <see cref="AppIntegrityService"/> to WPF window types.
    /// </summary>
    public static Func<string, string, bool>? IntegrityAlertPrompt { get; set; }

    /// <summary>
    /// Displays a user-facing security alert with an option to open the official releases repository.
    /// </summary>
    public static void ShowIntegrityAlert(string title, string message)
    {
        Action show = () =>
        {
            var prompt = IntegrityAlertPrompt;
            if (prompt != null)
            {
                try
                {
                    if (prompt(title, message))
                    {
                        OpenOfficialRepo();
                    }
                    return;
                }
                catch (Exception ex)
                {
                    RuntimeLog.Error(LogCategory, ex, "Failed to show IntegrityAlertPrompt, falling back to MessageBox");
                }
            }

            var result = MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (result == MessageBoxResult.OK)
            {
                OpenOfficialRepo();
            }
        };

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null) dispatcher.Invoke(show);
        else if (IntegrityAlertPrompt != null) show();
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
