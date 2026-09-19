using System.Numerics;
using System.Runtime.CompilerServices;

namespace VNotch.Controllers;

internal static class GlassBoxBlur
{
    internal static unsafe void Horizontal(byte[] src, byte[] dst, int w, int radius, int y0, int y1)
    {
        var divisor = new ExactByteAverage(2 * radius + 1);
        fixed (byte* source = src, destination = dst)
        {
            for (int y = y0; y < y1; y++)
            {
                int rowBase = y * w * 4;
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int nx = Math.Clamp(dx, 0, w - 1);
                    int o = rowBase + nx * 4;
                    sumB += source[o]; sumG += source[o + 1]; sumR += source[o + 2]; sumA += source[o + 3];
                }
                for (int x = 0; x < w; x++)
                {
                    int t = rowBase + x * 4;
                    destination[t] = divisor.Divide(sumB);
                    destination[t + 1] = divisor.Divide(sumG);
                    destination[t + 2] = divisor.Divide(sumR);
                    destination[t + 3] = divisor.Divide(sumA);

                    int outX = Math.Max(0, x - radius);
                    int inX = Math.Min(w - 1, x + 1 + radius);
                    int oOut = rowBase + outX * 4;
                    int oIn = rowBase + inX * 4;
                    sumB += source[oIn] - source[oOut];
                    sumG += source[oIn + 1] - source[oOut + 1];
                    sumR += source[oIn + 2] - source[oOut + 2];
                    sumA += source[oIn + 3] - source[oOut + 3];
                }
            }
        }
    }

    // Accumulate adjacent BGRA channels in SIMD lanes rather than walking
    // individual columns. Every lane retains the original integer window sum.
    internal static void Vertical(byte[] src, byte[] dst, int w, int h, int radius, int x0, int x1)
    {
        int window = 2 * radius + 1;
        int stride = w * 4;
        int column = x0 * 4, end = x1 * 4;
        if (Vector.IsHardwareAccelerated && window <= 4097)
        {
            var divisor = new Vector<float>(window);
            for (; column <= end - Vector<byte>.Count; column += Vector<byte>.Count)
            {
                Vector<int> s0 = Vector<int>.Zero, s1 = s0, s2 = s0, s3 = s0;
                for (int dy = -radius; dy <= radius; dy++)
                {
                    Widen(new Vector<byte>(src, Math.Clamp(dy, 0, h - 1) * stride + column),
                        out var a, out var b, out var c, out var d);
                    s0 += a; s1 += b; s2 += c; s3 += d;
                }
                for (int y = 0; y < h; y++)
                {
                    var lo = Vector.Narrow(Average(s0, divisor), Average(s1, divisor));
                    var hi = Vector.Narrow(Average(s2, divisor), Average(s3, divisor));
                    Vector.AsVectorByte(Vector.Narrow(lo, hi)).CopyTo(dst, y * stride + column);
                    Widen(new Vector<byte>(src, Math.Min(h - 1, y + radius + 1) * stride + column),
                        out var a, out var b, out var c, out var d);
                    Widen(new Vector<byte>(src, Math.Max(0, y - radius) * stride + column),
                        out var e, out var f, out var g, out var i);
                    s0 = s0 + a - e; s1 = s1 + b - f; s2 = s2 + c - g; s3 = s3 + d - i;
                }
            }
        }
        var scalarDivisor = new ExactByteAverage(window);
        for (; column < end; column++)
        {
            int sum = 0;
            for (int dy = -radius; dy <= radius; dy++)
                sum += src[Math.Clamp(dy, 0, h - 1) * stride + column];
            for (int y = 0; y < h; y++)
            {
                dst[y * stride + column] = scalarDivisor.Divide(sum);
                sum += src[Math.Min(h - 1, y + radius + 1) * stride + column]
                    - src[Math.Max(0, y - radius) * stride + column];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Widen(Vector<byte> pixels, out Vector<int> a, out Vector<int> b,
        out Vector<int> c, out Vector<int> d)
    {
        Vector.Widen(pixels, out var lo, out var hi);
        Vector.Widen(Vector.AsVectorInt16(lo), out a, out b);
        Vector.Widen(Vector.AsVectorInt16(hi), out c, out d);
    }

    // For window <= 4097, all integer sums (<= 1,044,735) are exactly
    // representable as float. Non-integral quotients are at least 1/4097 from
    // the next integer, much larger than half a float ULP at 255. Therefore
    // correctly rounded division followed by truncation equals integer division.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector<int> Average(Vector<int> sum, Vector<float> divisor) =>
        Vector.ConvertToInt32(Vector.ConvertToSingle(sum) / divisor);

    // ceil(2^32 / divisor) replaces integer division in the pixel loop.
    // Correct an overestimate to preserve integer truncation exactly, including
    // boundary sums. No floating-point rounding or approximate color values.
    internal readonly struct ExactByteAverage
    {
        private readonly uint _divisor;
        private readonly ulong _reciprocal;

        internal ExactByteAverage(int divisor)
        {
            _divisor = (uint)divisor;
            _reciprocal = ((1UL << 32) + _divisor - 1) / _divisor;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal byte Divide(int sum)
        {
            uint quotient = (uint)((uint)sum * _reciprocal >> 32);
            if (quotient * _divisor > (uint)sum) quotient--;
            return (byte)quotient;
        }
    }
}
