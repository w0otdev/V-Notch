using System.Runtime.InteropServices;

namespace VNotch.Services;

internal readonly record struct ShelfViewportRange(int StartIndex, int EndIndexExclusive)
{
    public static ShelfViewportRange Empty => new(0, 0);
}

internal static partial class ShelfViewportVirtualizer
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnShelfViewportRange
    {
        public int StartIndex;
        public int EndIndexExclusive;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_shelf_viewport_calculate(
        int itemCount,
        int rowCount,
        double cellWidth,
        double horizontalOffset,
        double viewportWidth,
        int overscanColumns,
        out VnShelfViewportRange outRange);

    public static ShelfViewportRange Calculate(
        int itemCount,
        int rowCount,
        double cellWidth,
        double horizontalOffset,
        double viewportWidth,
        int overscanColumns = 1)
    {
        _ = vn_shelf_viewport_calculate(
            itemCount,
            rowCount,
            cellWidth,
            horizontalOffset,
            viewportWidth,
            overscanColumns,
            out VnShelfViewportRange range);
        return new ShelfViewportRange(range.StartIndex, range.EndIndexExclusive);
    }
}
