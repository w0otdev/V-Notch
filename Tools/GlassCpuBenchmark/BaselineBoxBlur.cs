// Frozen pre-optimization kernels from commit 1434db9e4760e5ffa97d2e5ccbf34afe503abd03
namespace VNotch.Benchmarks;
internal static class BaselineBoxBlur
{
    internal static void BoxBlurHorizontal(byte[] src, byte[] dst, int w, int radius, int y0, int y1)
    {
        int window = 2 * radius + 1;
        for (int y = y0; y < y1; y++)
        {
            int rowBase = y * w * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = Math.Clamp(dx, 0, w - 1);
                int o = rowBase + nx * 4;
                sumB += src[o]; sumG += src[o + 1]; sumR += src[o + 2]; sumA += src[o + 3];
            }
            for (int x = 0; x < w; x++)
            {
                int t = rowBase + x * 4;
                dst[t] = (byte)(sumB / window);
                dst[t + 1] = (byte)(sumG / window);
                dst[t + 2] = (byte)(sumR / window);
                dst[t + 3] = (byte)(sumA / window);

                int outX = Math.Max(0, x - radius);
                int inX = Math.Min(w - 1, x + 1 + radius);
                int oOut = rowBase + outX * 4;
                int oIn = rowBase + inX * 4;
                sumB += src[oIn] - src[oOut];
                sumG += src[oIn + 1] - src[oOut + 1];
                sumR += src[oIn + 2] - src[oOut + 2];
                sumA += src[oIn + 3] - src[oOut + 3];
            }
        }
    }

    internal static void BoxBlurVertical(byte[] src, byte[] dst, int w, int h, int radius, int x0, int x1)
    {
        int window = 2 * radius + 1;
        int rowStride = w * 4;
        for (int x = x0; x < x1; x++)
        {
            int col = x * 4;
            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dy = -radius; dy <= radius; dy++)
            {
                int ny = Math.Clamp(dy, 0, h - 1);
                int o = ny * rowStride + col;
                sumB += src[o]; sumG += src[o + 1]; sumR += src[o + 2]; sumA += src[o + 3];
            }
            for (int y = 0; y < h; y++)
            {
                int t = y * rowStride + col;
                dst[t] = (byte)(sumB / window);
                dst[t + 1] = (byte)(sumG / window);
                dst[t + 2] = (byte)(sumR / window);
                dst[t + 3] = (byte)(sumA / window);

                int outY = Math.Max(0, y - radius);
                int inY = Math.Min(h - 1, y + 1 + radius);
                int oOut = outY * rowStride + col;
                int oIn = inY * rowStride + col;
                sumB += src[oIn] - src[oOut];
                sumG += src[oIn + 1] - src[oOut + 1];
                sumR += src[oIn + 2] - src[oOut + 2];
                sumA += src[oIn + 3] - src[oOut + 3];
            }
        }
    }

}
