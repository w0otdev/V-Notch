using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public static partial class SafeLauncher
{
    private const string LogCategory = "SAFE-LAUNCHER";

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_safe_launcher_classify_url_prefix(
        ReadOnlySpan<char> urlPtr,
        int urlLen,
        out int outClass);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_safe_launcher_is_allowed_scheme(
        ReadOnlySpan<char> schemePtr,
        int schemeLen,
        ReadOnlySpan<char> origPtr,
        int origLen,
        out byte outAllowed);

    public static bool IsSafeUrl(string? url, out Uri? uri)
    {
        uri = null;
        if (url == null)
            return false;

        ReadOnlySpan<char> urlSpan = url.AsSpan();
        _ = vn_safe_launcher_classify_url_prefix(urlSpan, urlSpan.Length, out int cls);
        if (cls == 0)
            return false;
        if (cls == 1)
            return Uri.TryCreate(url, UriKind.Absolute, out uri);

        if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            return false;

        ReadOnlySpan<char> schemeSpan = uri.Scheme.AsSpan();
        _ = vn_safe_launcher_is_allowed_scheme(schemeSpan, schemeSpan.Length, ReadOnlySpan<char>.Empty, 0, out byte allowed);
        return allowed != 0;
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

    internal static bool IsSafeUri(Uri? uri)
    {
        if (uri is not { IsAbsoluteUri: true }) return false;
        ReadOnlySpan<char> schemeSpan = uri.Scheme.AsSpan();
        ReadOnlySpan<char> origSpan = uri.OriginalString.AsSpan();
        return vn_safe_launcher_is_allowed_scheme(schemeSpan, schemeSpan.Length, origSpan, origSpan.Length, out byte allowed) == 0 && allowed != 0;
    }

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
            return psi.UseShellExecute || process != null;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error(LogCategory, ex, $"Failed to launch URL: {uri}");
            return false;
        }
    }
}
