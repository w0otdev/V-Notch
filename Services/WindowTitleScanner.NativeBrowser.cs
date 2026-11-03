using System.Buffers;
using System.Runtime.InteropServices;

namespace VNotch.Services;

public sealed partial class WindowTitleScanner
{
    private static bool IsMediaUrl(string url)
        => vn_wtscanner_is_media_url(MemoryMarshal.Cast<char, ushort>(url.AsSpan()), url.Length, out byte media) == 0 && media != 0;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeBrowserTab { public int UrlOffset, UrlLength; public byte Spotify; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct NativeBrowserChrome
    {
        public int UrlOffset, UrlLength, AddressOffset, AddressLength, ToolbarCount, TabCount;
        public byte Spotify;
    }
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_wtscanner_scan_browser(nint hwnd, byte firefox, out NativeBrowserChrome result,
        Span<NativeBrowserTab> tabs, int tabCapacity, Span<char> text, int capacity, out int required);

    private static (string? Url, bool Spotify) ScanNativeBrowser(nint hwnd, string processName)
    {
        Span<char> text = stackalloc char[8192];
        byte firefox = processName.Contains("firefox", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
        int status = vn_wtscanner_scan_browser(hwnd, firefox, out var result, default, 0, text, text.Length, out int required);
        char[]? rented = null;
        try
        {
            if (status == -2 && required > 0)
            {
                rented = ArrayPool<char>.Shared.Rent(required);
                text = rented;
                status = vn_wtscanner_scan_browser(hwnd, firefox, out result, default, 0, text, text.Length, out _);
            }
            return status == 0 ? (result.UrlLength == 0 ? null : new string(text.Slice(result.UrlOffset, result.UrlLength)), result.Spotify != 0) : default;
        }
        finally { if (rented != null) ArrayPool<char>.Shared.Return(rented); }
    }

    internal sealed record BrowserTabSnapshot(string? Url, bool ReferencesSpotify);
    internal sealed record BrowserChromeSnapshot(int ToolbarCount, IReadOnlyList<BrowserTabSnapshot> Tabs, string? AddressUrl);

    // Detached values also let provider-lifetime regression tests exercise the native cache.
    internal static BrowserChromeSnapshot ReadBrowserChromeSnapshot(nint hwnd)
    {
        Span<NativeBrowserTab> tabs = stackalloc NativeBrowserTab[256];
        Span<char> text = stackalloc char[16384];
        int status = vn_wtscanner_scan_browser(hwnd, 0, out var result, tabs, tabs.Length, text, text.Length, out int required);
        if (status == -2 && required > 0)
        {
            text = new char[required];
            status = vn_wtscanner_scan_browser(hwnd, 0, out result, tabs, tabs.Length, text, text.Length, out _);
        }
        if (status != 0) throw new InvalidOperationException($"Native browser snapshot failed ({status}).");
        var snapshots = new BrowserTabSnapshot[result.TabCount];
        for (int i = 0; i < snapshots.Length; i++)
            snapshots[i] = new BrowserTabSnapshot(tabs[i].UrlLength == 0 ? null : new string(text.Slice(tabs[i].UrlOffset, tabs[i].UrlLength)), tabs[i].Spotify != 0);
        return new BrowserChromeSnapshot(result.ToolbarCount, snapshots,
            result.AddressLength == 0 ? null : new string(text.Slice(result.AddressOffset, result.AddressLength)));
    }
}
