using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VNotch.Controllers;

internal static partial class GlassBoxBlur
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_glass_box_blur_horizontal(
        byte* src, byte* dst, int w, int radius, int y0, int y1);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_glass_box_blur_vertical(
        byte* src, byte* dst, int w, int h, int radius, int x0, int x1);

    internal static unsafe void Horizontal(IntPtr src, byte[] dst, int w, int radius, int y0, int y1)
    {
        fixed (byte* destination = dst)
        {
            _ = vn_glass_box_blur_horizontal((byte*)src, destination, w, radius, y0, y1);
        }
    }

    internal static unsafe void Horizontal(byte[] src, byte[] dst, int w, int radius, int y0, int y1)
    {
        fixed (byte* source = src, destination = dst)
        {
            _ = vn_glass_box_blur_horizontal(source, destination, w, radius, y0, y1);
        }
    }

    internal static unsafe void Vertical(byte[] src, byte[] dst, int w, int h, int radius, int x0, int x1)
    {
        fixed (byte* source = src, destination = dst)
        {
            _ = vn_glass_box_blur_vertical(source, destination, w, h, radius, x0, x1);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static Vector<int> Average(Vector<int> sum, Vector<float> divisor) =>
        Vector.ConvertToInt32(Vector.ConvertToSingle(sum) / divisor);

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
