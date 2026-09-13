using System;

namespace VNotch.Controllers;

internal static class GlassFrameFingerprint
{
    // Preserve the existing sampling grid. Four independent accumulators remove
    // the serial multiply dependency without reading fewer backdrop samples.
    internal static unsafe ulong Compute(IntPtr pixels, int width, int height)
    {
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0) return 0;
        const ulong prime = 1099511628211UL;
        const ulong seed = 14695981039346656037UL;
        ulong a = seed, b = seed, c = seed, d = seed;
        int rowBytes = checked(width * 4);
        int words = rowBytes >> 3;
        int step = Math.Max(1, words / 96);
        int rowStep = Math.Max(1, height / 96);
        for (int y = 0; y < height; y += rowStep)
        {
            ulong* row = (ulong*)((byte*)pixels + (long)y * rowBytes);
            int i = 0;
            for (; i + 3 * step < words; i += 4 * step)
            {
                a = (a ^ row[i]) * prime;
                b = (b ^ row[i + step]) * prime;
                c = (c ^ row[i + 2 * step]) * prime;
                d = (d ^ row[i + 3 * step]) * prime;
            }
            for (; i < words; i += step)
                a = (a ^ row[i]) * prime;
            // Odd-width frames have a final pixel outside the qword loop.
            if ((width & 1) != 0)
                d = (d ^ ((uint*)row)[width - 1]) * prime;
        }
        return (((((a * prime) ^ b) * prime) ^ c) * prime) ^ d;
    }
}
