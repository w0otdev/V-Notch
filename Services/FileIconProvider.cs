using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace VNotch.Services;

internal static class FileIconProvider
{
    private sealed class CacheEntry
    {
        public ImageSource? Icon;
        public long LastAccess;
    }

    private static readonly ConcurrentDictionary<string, CacheEntry> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheSize = 64;
    private static long _accessCounter;
    private static readonly object _evictLock = new();

    public static void Invalidate(string filePath) => _iconCache.TryRemove(filePath, out _);

    public static void ClearCache() => _iconCache.Clear();

    #region Native interop

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [In, MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        [In] IntPtr pbc,
        [In, MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8746c1f01a3b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage([In, MarshalAs(UnmanagedType.Struct)] SIZE size, [In] int flags, [Out] out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x000000080;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    private static readonly Guid _shellItemImageFactoryIid = new Guid("bcc18b79-ba16-442f-80c4-8746c1f01a3b");

    #endregion

    public static ImageSource? GetFileIcon(string filePath)
    {
        if (_iconCache.TryGetValue(filePath, out var cached))
        {
            cached.LastAccess = Interlocked.Increment(ref _accessCounter);
            return cached.Icon;
        }

        try
        {
            if (!File.Exists(filePath) && !Directory.Exists(filePath)) return null;

            if (_iconCache.Count >= MaxCacheSize)
                EvictOldest();

            string ext = Path.GetExtension(filePath).ToLowerInvariant();
            bool isImage = ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".svg" or ".webp" or ".ico";
            bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv" or ".webm";

            ImageSource? result = null;

            if (isImage || isVideo)
            {
                result ??= TryGetShellThumbnail(filePath, 64);

                if (result == null && isImage && ext is not ".svg")
                {
                    result = TryGetDirectImage(filePath);
                }
            }

            result ??= TryGetAssociatedIcon(filePath);
            result ??= TryGetShellFileIcon(filePath);

            _iconCache[filePath] = new CacheEntry
            {
                Icon = result,
                LastAccess = Interlocked.Increment(ref _accessCounter)
            };
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void EvictOldest()
    {
        lock (_evictLock)
        {
            if (_iconCache.Count < MaxCacheSize) return;

            int removeCount = Math.Max(1, MaxCacheSize / 4);
            var oldest = _iconCache
                .OrderBy(kvp => kvp.Value.LastAccess)
                .Take(removeCount)
                .Select(kvp => kvp.Key)
                .ToArray();

            foreach (var key in oldest)
                _iconCache.TryRemove(key, out _);
        }
    }

    private static ImageSource? TryGetShellThumbnail(string filePath, int size)
    {
        try
        {
            SHCreateItemFromParsingName(filePath, IntPtr.Zero, _shellItemImageFactoryIid, out var factory);
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, 0x01, out IntPtr hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero) return null;

            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetDirectImage(string filePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(filePath);
            bitmap.DecodePixelWidth = 48;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetAssociatedIcon(string filePath)
    {
        try
        {
            if (Directory.Exists(filePath)) return TryGetShellThumbnail(filePath, 128);
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(filePath);
            if (icon == null) return null;

            using var bmp = icon.ToBitmap();
            var hBitmap = bmp.GetHbitmap();
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    private static ImageSource? TryGetShellFileIcon(string filePath)
    {
        try
        {
            var shinfo = new SHFILEINFO();
            IntPtr res = SHGetFileInfo(
                filePath,
                0,
                ref shinfo,
                (uint)Marshal.SizeOf(shinfo),
                SHGFI_ICON | SHGFI_LARGEICON);

            if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
            {
                string ext = Path.GetExtension(filePath);
                if (string.IsNullOrEmpty(ext)) return null;

                res = SHGetFileInfo(
                    ext,
                    FILE_ATTRIBUTE_NORMAL,
                    ref shinfo,
                    (uint)Marshal.SizeOf(shinfo),
                    SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES);
            }

            if (shinfo.hIcon == IntPtr.Zero) return null;

            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    shinfo.hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(shinfo.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
