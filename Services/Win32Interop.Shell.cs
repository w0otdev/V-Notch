using System.Runtime.InteropServices;

namespace VNotch.Services;

// Shell icon and thumbnail handles. Native side: Native/Services/ShellIcons.cpp.
// Callers convert the handle to a WPF bitmap and release it with DestroyIcon / DeleteObject.
internal static unsafe partial class Win32Interop
{
    public const uint SHGFI_LARGEICON = 0x000000000;
    public const uint SHGFI_SMALLICON = 0x000000001;
    public const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x000000080;
    public const uint SIIGBF_BIGGERSIZEOK = 0x00000001;

    [LibraryImport(NativeDll, EntryPoint = "vn_shell_get_file_info_icon", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeShellGetFileInfoIcon(string path, int pathLength, uint fileAttributes, uint flags, out long icon);

    [LibraryImport(NativeDll, EntryPoint = "vn_shell_get_base_icon", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeShellGetBaseIcon(string path, int pathLength, byte smallIcon, out long icon);

    [LibraryImport(NativeDll, EntryPoint = "vn_shell_extract_icon", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeShellExtractIcon(string path, int pathLength, int iconIndex, byte smallIcon, out long icon);

    [LibraryImport(NativeDll, EntryPoint = "vn_shell_get_thumbnail_bitmap", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int NativeShellGetThumbnailBitmap(string path, int pathLength, int size, uint flags, out long bitmap);

    /// <summary>SHGetFileInfo icon handle (zero when unavailable). <paramref name="flags"/> excludes SHGFI_ICON.</summary>
    internal static IntPtr GetShellFileInfoIcon(string path, uint fileAttributes, uint flags) =>
        NativeShellGetFileInfoIcon(path, path.Length, fileAttributes, flags, out long icon) == 0 ? (IntPtr)icon : IntPtr.Zero;

    /// <summary>System image list icon without the shortcut overlay, or zero.</summary>
    internal static IntPtr GetShellBaseIcon(string path, bool smallIcon)
    {
        byte smallFlag = smallIcon ? (byte)1 : (byte)0;
        return NativeShellGetBaseIcon(path, path.Length, smallFlag, out long icon) == 0 ? (IntPtr)icon : IntPtr.Zero;
    }

    /// <summary>Icon <paramref name="index"/> embedded in a file, or zero.</summary>
    internal static IntPtr ExtractShellIcon(string path, int index, bool smallIcon)
    {
        byte smallFlag = smallIcon ? (byte)1 : (byte)0;
        return NativeShellExtractIcon(path, path.Length, index, smallFlag, out long icon) == 0 ? (IntPtr)icon : IntPtr.Zero;
    }

    /// <summary>Shell thumbnail HBITMAP of the requested square size, or zero.</summary>
    internal static IntPtr GetShellThumbnailBitmap(string path, int size, uint flags) =>
        NativeShellGetThumbnailBitmap(path, path.Length, size, flags, out long bitmap) == 0 ? (IntPtr)bitmap : IntPtr.Zero;
}
