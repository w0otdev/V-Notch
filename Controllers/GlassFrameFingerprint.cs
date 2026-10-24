using System;
using System.Runtime.InteropServices;

namespace VNotch.Controllers;

internal static partial class GlassFrameFingerprint
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_glass_frame_fingerprint_compute(
        IntPtr pixels, int width, int height, out ulong outHash);

    internal static ulong Compute(IntPtr pixels, int width, int height)
    {
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0) return 0;
        _ = checked(width * 4);
        _ = vn_glass_frame_fingerprint_compute(pixels, width, height, out ulong hash);
        return hash;
    }
}
