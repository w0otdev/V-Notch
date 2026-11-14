using System;
using System.Runtime.InteropServices;

namespace VNotch.Controllers;

internal readonly record struct GlassDirtyRows(int Top, int Bottom)
{
    internal bool IsEmpty => Bottom <= Top;
    internal GlassDirtyRows Union(GlassDirtyRows other)
    {
        if (IsEmpty) return other;
        return other.IsEmpty ? this :
        new(Math.Min(Top, other.Top), Math.Max(Bottom, other.Bottom));
    }
}

internal static partial class GlassUploadDelta
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_glass_upload_delta_copy_changed_rows(
        IntPtr source, int sourceStride, IntPtr destination, int destinationStride,
        int rowBytes, int height, out int outTop, out int outBottom);

    internal static GlassDirtyRows CopyChangedRows(
        IntPtr source, int sourceStride, IntPtr destination, int destinationStride,
        int rowBytes, int height)
    {
        _ = vn_glass_upload_delta_copy_changed_rows(
            source, sourceStride, destination, destinationStride,
            rowBytes, height, out int top, out int bottom);
        return bottom == 0 ? default : new(top, bottom);
    }
}
