using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace VNotch.Services;

internal static partial class FileIconProvider
{
    private const string NativeDll = "VNotch.Native.dll";

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnFileIconExtensionFlags
    {
        public byte IsImage;
        public byte IsVideo;
        public byte IsSvg;
        public byte IsExeOrLnk;
        public byte IsMsc;
        public byte IsLnk;
    }

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_file_icon_provider_classify_extension(
        ReadOnlySpan<char> ext,
        int extLen,
        out VnFileIconExtensionFlags outFlags);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_file_icon_provider_parse_msc_icon_xml(
        ReadOnlySpan<char> xml,
        int xmlLen,
        out int outIndex,
        out int outFileStart,
        out int outFileLen,
        out byte outMatched);

    [LibraryImport(NativeDll)]
    private static partial int vn_file_icon_provider_compute_evict_count(
        int currentCount,
        int maxCacheSize,
        out int outRemoveCount);

    private sealed class CacheEntry
    {
        public ImageSource? Icon;
        public long LastAccess;
    }

    private static readonly ConcurrentDictionary<string, CacheEntry> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCacheSize = 64;
    private static long _accessCounter;
    private static readonly object _evictLock = new();

    public static void Invalidate(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        _iconCache.TryRemove(filePath, out _);
        _iconCache.TryRemove($"app:s:{filePath}", out _);
        _iconCache.TryRemove($"app:l:{filePath}", out _);
    }

    public static void ClearCache() => _iconCache.Clear();

    private static void DeleteObject(IntPtr handle) => _ = Win32Interop.DeleteObject(handle);

    private static void DestroyIcon(IntPtr handle) => _ = Win32Interop.DestroyIcon(handle);

    private const uint SHGFI_LARGEICON = Win32Interop.SHGFI_LARGEICON;
    private const uint SHGFI_SMALLICON = Win32Interop.SHGFI_SMALLICON;
    private const uint SHGFI_USEFILEATTRIBUTES = Win32Interop.SHGFI_USEFILEATTRIBUTES;
    private const uint FILE_ATTRIBUTE_NORMAL = Win32Interop.FILE_ATTRIBUTE_NORMAL;

    // The Shell associates every .msc with a generic document icon. Each MMC
    // console declares its real artwork in its XML (<Icon Index File>).
    private static bool CanProbeLocalFilesystemPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) ||
            trimmed.StartsWith("//", StringComparison.Ordinal) ||
            LocalPathPolicy.IsUncPath(trimmed))
        {
            return false;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && (!uri.IsFile || uri.IsUnc))
            return false;

        return Path.IsPathFullyQualified(trimmed) &&
               trimmed.Length >= 3 &&
               char.IsAsciiLetter(trimmed[0]) &&
               trimmed[1] == ':' &&
               (trimmed[2] == '\\' || trimmed[2] == '/');
    }

    internal static ImageSource? TryGetMscIcon(string mscPath, bool small = false)
    {
        if (!CanProbeLocalFilesystemPath(mscPath))
            return null;

        ReadOnlySpan<char> extSpan = Path.GetExtension(mscPath.AsSpan());
        if (vn_file_icon_provider_classify_extension(extSpan, extSpan.Length, out var extFlags) != 0 || extFlags.IsMsc == 0)
            return null;
        try
        {
            string? file = null;
            int index = 0;
            var mscInfo = new FileInfo(mscPath);
            if (!mscInfo.Exists || mscInfo.Length <= 0 || mscInfo.Length > 1024 * 1024)
                return null;

            using var fs = new FileStream(mscPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = XmlReader.Create(fs, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_000_000
            });
            var doc = XDocument.Load(reader);
            foreach (var iconElement in doc.Descendants())
            {
                if (!string.Equals(iconElement.Name.LocalName, "Icon", StringComparison.OrdinalIgnoreCase))
                    continue;

                string? attrFile = iconElement.Attributes()
                    .FirstOrDefault(a => string.Equals(a.Name.LocalName, "File", StringComparison.OrdinalIgnoreCase))
                    ?.Value;
                if (attrFile != null)
                {
                    string expandedAttr = Environment.ExpandEnvironmentVariables(attrFile.Trim());
                    if (!CanProbeLocalFilesystemPath(expandedAttr))
                        return null;
                }
            }

            string xml = doc.ToString(SaveOptions.DisableFormatting);
            if (true)
            {
                // The Icon element sits near the top; avoid reading large embedded binaries.


                try
                {
                    ReadOnlySpan<char> xmlSpan = xml.AsSpan();
                    // xmlSpan initialized above
                    if (vn_file_icon_provider_parse_msc_icon_xml(xmlSpan, xmlSpan.Length, out int parsedIdx, out int fileStart, out int fileLen, out byte matched) == 0 && matched != 0)
                    {
                        index = parsedIdx;
                        file = Environment.ExpandEnvironmentVariables(new string(xmlSpan.Slice(fileStart, fileLen)).Trim());
                        if (!CanProbeLocalFilesystemPath(file))
                            return null;
                        // Some consoles ship a malformed path (e.g. %SystemRoot%\Windows\system32).
                        if (!File.Exists(file))
                        {
                            string sysPath = Path.Combine(Environment.SystemDirectory, Path.GetFileName(file));
                            if (!CanProbeLocalFilesystemPath(sysPath))
                                return null;
                            file = sysPath;
                        }
                        // Some consoles ship a malformed path (e.g. %SystemRoot%\Windows\system32).
                        // System32 fallback handled above
                    }
                }
                finally
                {

                }
            }
            if (file == null || !File.Exists(file))
            {
                string mmcPath = Path.Combine(Environment.SystemDirectory, "mmc.exe");
                if (!CanProbeLocalFilesystemPath(mmcPath) || !File.Exists(mmcPath))
                    return null;
                file = mmcPath;
                index = 0;
            }
            IntPtr handle = Win32Interop.ExtractShellIcon(file, index, small);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally { DestroyIcon(handle); }
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ImageSource? GetAppIcon(string exePath, bool small = true)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        string key = $"app:{(small ? "s" : "l")}:{exePath}";
        if (_iconCache.TryGetValue(key, out var cached) && cached.Icon != null)
        {
            cached.LastAccess = Interlocked.Increment(ref _accessCounter);
            return cached.Icon;
        }

        try
        {
            if (!File.Exists(exePath)) return null;

            var icon = TryGetMscIcon(exePath, small) ?? TryGetShellFileIcon(exePath, small) ?? TryGetAssociatedIcon(exePath);
            if (icon != null)
            {
                if (_iconCache.Count >= MaxCacheSize) EvictOldest();
                _iconCache[key] = new CacheEntry
                {
                    Icon = icon,
                    LastAccess = Interlocked.Increment(ref _accessCounter)
                };
            }
            return icon;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ImageSource? GetExtensionIcon(string extension, bool small = false)
    {
        if (string.IsNullOrWhiteSpace(extension)) return null;
        if (!extension.StartsWith('.')) extension = "." + extension;
        string key = $"ext:{(small ? "s" : "l")}:{extension}";
        if (_iconCache.TryGetValue(key, out var cached) && cached.Icon != null)
        {
            cached.LastAccess = Interlocked.Increment(ref _accessCounter);
            return cached.Icon;
        }

        try
        {
            var icon = TryGetShellFileIcon(extension, small);
            if (icon != null)
            {
                if (_iconCache.Count >= MaxCacheSize) EvictOldest();
                _iconCache[key] = new CacheEntry
                {
                    Icon = icon,
                    LastAccess = Interlocked.Increment(ref _accessCounter)
                };
            }
            return icon;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ImageSource? GetFileIcon(string filePath)
    {
        if (_iconCache.TryGetValue(filePath, out var cached) && cached.Icon != null)
        {
            cached.LastAccess = Interlocked.Increment(ref _accessCounter);
            return cached.Icon;
        }

        try
        {
            if (!File.Exists(filePath) && !Directory.Exists(filePath)) return null;

            ReadOnlySpan<char> extSpan = Path.GetExtension(filePath.AsSpan());
            _ = vn_file_icon_provider_classify_extension(extSpan, extSpan.Length, out var flags);
            bool isImage = flags.IsImage != 0;
            bool isVideo = flags.IsVideo != 0;

            ImageSource? result = null;

            if (isImage || isVideo)
            {
                result ??= TryGetShellThumbnail(filePath, 64);

                if (result == null && isImage && flags.IsSvg == 0)
                {
                    result = TryGetDirectImage(filePath);
                }
            }

            if (flags.IsExeOrLnk != 0)
            {
                result ??= TryGetShellFileIcon(filePath, small: false);
            }

            result ??= TryGetMscIcon(filePath, small: false);
            result ??= TryGetAssociatedIcon(filePath);
            result ??= TryGetShellFileIcon(filePath, small: false);

            if (result != null)
            {
                if (_iconCache.Count >= MaxCacheSize)
                    EvictOldest();

                _iconCache[filePath] = new CacheEntry
                {
                    Icon = result,
                    LastAccess = Interlocked.Increment(ref _accessCounter)
                };
            }
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void EvictOldest()
    {
        lock (_evictLock)
        {
            if (vn_file_icon_provider_compute_evict_count(_iconCache.Count, MaxCacheSize, out int removeCount) != 0 || removeCount <= 0)
                return;

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
            IntPtr hBitmap = Win32Interop.GetShellThumbnailBitmap(filePath, size, Win32Interop.SIIGBF_BIGGERSIZEOK);
            if (hBitmap == IntPtr.Zero) return null;

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
        catch (Exception)
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
        catch (Exception)
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

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static ImageSource? TryGetShellFileIcon(string filePath, bool small = false)
    {
        try
        {
            uint flags = small ? SHGFI_SMALLICON : SHGFI_LARGEICON;
            // Extract the base image without the Shell's tiny shortcut overlay;
            // Spotlight draws its own larger vector arrow above the artwork.
            ReadOnlySpan<char> lnkExtSpan = Path.GetExtension(filePath.AsSpan());
            if (vn_file_icon_provider_classify_extension(lnkExtSpan, lnkExtSpan.Length, out var lnkFlags) == 0 && lnkFlags.IsLnk != 0)
            {
                IntPtr baseIcon = Win32Interop.GetShellBaseIcon(filePath, small);
                if (baseIcon != IntPtr.Zero)
                {
                    try
                    {
                        var baseSource = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                            baseIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        baseSource.Freeze();
                        return baseSource;
                    }
                    finally { DestroyIcon(baseIcon); }
                }
            }
            IntPtr hIcon = Win32Interop.GetShellFileInfoIcon(filePath, 0, flags);

            if (hIcon == IntPtr.Zero)
            {
                string ext = Path.GetExtension(filePath);
                if (string.IsNullOrEmpty(ext)) return null;

                hIcon = Win32Interop.GetShellFileInfoIcon(ext, FILE_ATTRIBUTE_NORMAL, flags | SHGFI_USEFILEATTRIBUTES);
            }

            if (hIcon == IntPtr.Zero) return null;

            try
            {
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    hIcon,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }
}
