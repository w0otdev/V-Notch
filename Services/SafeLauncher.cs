using System;
using System.Diagnostics;

namespace VNotch.Services;

public static class SafeLauncher
{
    private const string LogCategory = "SAFE-LAUNCHER";

    public static bool IsSafeUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            if (url.Equals("ms-settings:batterysaver", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out uri))
                    return true;
            }
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            return false;

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static bool TryOpenUrl(string? url)
    {
        if (!IsSafeUrl(url, out var uri) || uri == null)
        {
            RuntimeLog.Warn(LogCategory, $"Blocked attempt to launch unsafe or invalid URL: {url}");
            return false;
        }

        return LaunchValidatedUri(uri);
    }

    public static bool TryOpenUrl(Uri? uri)
    {
        if (!IsSafeUri(uri))
        {
            RuntimeLog.Warn(LogCategory, $"Blocked attempt to launch unsafe URI: {uri}");
            return false;
        }

        return LaunchValidatedUri(uri!);
    }

    internal static bool IsSafeUri(Uri? uri) =>
        uri is { IsAbsoluteUri: true } &&
        (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase) ||
         uri.OriginalString.Equals("ms-settings:batterysaver", StringComparison.OrdinalIgnoreCase));

    private static bool LaunchValidatedUri(Uri uri)
    {
        if (!uri.Scheme.Equals("ms-settings", StringComparison.OrdinalIgnoreCase) &&
            !NetworkPrivacy.Current.IsAllowed(NetworkFeature.ExternalLinks)) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            };
            using var process = Process.Start(psi);
            return process != null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, $"Failed to launch URL: {uri}");
            return false;
        }
    }
}
