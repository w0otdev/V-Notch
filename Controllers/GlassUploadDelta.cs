using System;

namespace VNotch.Controllers;

internal readonly record struct GlassDirtyRows(int Top, int Bottom)
{
    internal bool IsEmpty => Bottom <= Top;
    internal GlassDirtyRows Union(GlassDirtyRows other) => IsEmpty ? other : other.IsEmpty ? this :
        new(Math.Min(Top, other.Top), Math.Max(Bottom, other.Bottom));
}

internal static class GlassUploadDelta
{
    // Return the union of changed rows so CPU copies and GPU transfers both
    // follow actual changes. The caller accumulates this range until presented.
    internal static unsafe GlassDirtyRows CopyChangedRows(
        IntPtr source, int sourceStride, IntPtr destination, int destinationStride,
        int rowBytes, int height)
    {
        int top = height, bottom = 0;
        for (int y = 0; y < height; y++)
        {
            var src = new ReadOnlySpan<byte>((byte*)source + (long)y * sourceStride, rowBytes);
            var dst = new Span<byte>((byte*)destination + (long)y * destinationStride, rowBytes);
            if (src.SequenceEqual(dst)) continue;
            src.CopyTo(dst);
            top = Math.Min(top, y);
            bottom = y + 1;
        }
        return bottom == 0 ? default : new(top, bottom);
    }
}
