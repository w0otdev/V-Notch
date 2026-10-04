using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public static class FastBlurService
{

    public static async Task<BitmapSource?> GetBlurredImageAsync(BitmapSource source, int downscaleWidth = 128, int blurRadius = 8)
    {
        if (source == null) return null;

        try
        {
            source = await ArtworkAnalysisSource.GetFrozenSnapshotAsync(source).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("FAST-BLUR", $"Could not prepare bitmap: {ex}");
            return null;
        }

        return await Task.Run(() =>
        {
            try
            {
                int width = Math.Max(64, downscaleWidth);
                int height = (int)(source.PixelHeight * ((double)width / source.PixelWidth));
                if (height < 1) height = 1;
                blurRadius = Math.Clamp(blurRadius, 1, 20);

                var formattedBitmap = ArtworkAnalysisSource.GetBgra32(source);

                var smallBitmap = new TransformedBitmap(formattedBitmap, new ScaleTransform((double)width / formattedBitmap.PixelWidth, (double)height / formattedBitmap.PixelHeight));

                int stride = width * 4;
                int bufferSize = height * stride;

                byte[] pixels = ArrayPool<byte>.Shared.Rent(bufferSize);
                byte[] target = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    smallBitmap.CopyPixels(pixels, stride, 0);

                    int passes = 2;
                    for (int i = 0; i < passes; i++)
                    {
                        BoxBlurHorizontal(pixels, target, width, height, blurRadius);
                        BoxBlurVertical(target, pixels, width, height, blurRadius);
                    }

                    DarkenPixels(pixels, bufferSize);

                    var writeableBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
                    writeableBitmap.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                    writeableBitmap.Freeze();

                    return (BitmapSource)writeableBitmap;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixels);
                    ArrayPool<byte>.Shared.Return(target);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Warn("FAST-BLUR", $"Image processing failed: {ex}");
                return null;
            }
        }).ConfigureAwait(false);
    }

    internal static void DarkenPixels(byte[] pixels, int length)
    {
        // ceil(0.96 * 2^16): matches (byte)(value * 0.96f) for every byte.
        int i = 0;
        if (Sse2.IsSupported)
        {
            var factor = Vector128.Create((ushort)62915);
            var alphaMask = Vector128.Create(0xff000000u).AsByte();
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                var original = Vector128.LoadUnsafe(ref pixels[i]);
                var low = Sse2.UnpackLow(original, Vector128<byte>.Zero).AsUInt16();
                var high = Sse2.UnpackHigh(original, Vector128<byte>.Zero).AsUInt16();
                var dark = Sse2.PackUnsignedSaturate(
                    Sse2.MultiplyHigh(low, factor).AsInt16(),
                    Sse2.MultiplyHigh(high, factor).AsInt16());
                Sse2.Or(Sse2.AndNot(alphaMask, dark), Sse2.And(alphaMask, original))
                    .StoreUnsafe(ref pixels[i]);
            }
        }
        for (; i < length; i += 4)
        {
            pixels[i] = (byte)((pixels[i] * 62915) >> 16);
            pixels[i + 1] = (byte)((pixels[i + 1] * 62915) >> 16);
            pixels[i + 2] = (byte)((pixels[i + 2] * 62915) >> 16);
        }
    }

    internal static void BoxBlurHorizontal(byte[] source, byte[] target, int w, int h, int radius)
    {
        int window = 2 * radius + 1;
        uint reciprocal = GetWindowReciprocal(window);

        for (int y = 0; y < h; y++)
        {
            int pBase = y * w * 4;

            int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
            for (int dx = -radius; dx <= radius; dx++)
            {
                int nx = Math.Clamp(dx, 0, w - 1);
                int o = pBase + nx * 4;
                sumB += source[o];
                sumG += source[o + 1];
                sumR += source[o + 2];
                sumA += source[o + 3];
            }

            for (int x = 0; x < w; x++)
            {
                int t = pBase + x * 4;
                target[t] = (byte)(((uint)sumB * reciprocal) >> 24);
                target[t + 1] = (byte)(((uint)sumG * reciprocal) >> 24);
                target[t + 2] = (byte)(((uint)sumR * reciprocal) >> 24);
                target[t + 3] = (byte)(((uint)sumA * reciprocal) >> 24);

                int outX = Math.Clamp(x - radius, 0, w - 1);
                int inX = Math.Clamp(x + 1 + radius, 0, w - 1);

                int oOut = pBase + outX * 4;
                int oIn = pBase + inX * 4;
                sumB += source[oIn] - source[oOut];
                sumG += source[oIn + 1] - source[oOut + 1];
                sumR += source[oIn + 2] - source[oOut + 2];
                sumA += source[oIn + 3] - source[oOut + 3];
            }
        }
    }

    // Callers use radii up to 32 (including subject-aware blur). Rounding the
    // reciprocal UP at 24 bits preserves floor(sum / window) for every possible
    // channel sum (0..255*window). The unsigned product stays below 2^32.
    internal static uint GetWindowReciprocal(int window) => ((1u << 24) + (uint)window - 1) / (uint)window;

    internal static void BoxBlurVertical(byte[] source, byte[] target, int w, int h, int radius)
    {
        int window = 2 * radius + 1;
        uint reciprocal = GetWindowReciprocal(window);
        int rowStride = w * 4;
        int[]? rented = null;
        Span<int> sums = rowStride <= 1024
            ? stackalloc int[rowStride]
            : (rented = ArrayPool<int>.Shared.Rent(rowStride)).AsSpan(0, rowStride);
        sums.Clear();
        var reciprocalVector = Vector256.Create(reciprocal);
        try
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int row = Math.Clamp(dy, 0, h - 1) * rowStride;
                for (int c = 0; c < rowStride; c++)
                    sums[c] += source[row + c];
            }

            // Walk entire contiguous rows; keep one rolling sum per channel.
            for (int y = 0; y < h; y++)
            {
                int row = y * rowStride;
                int outRow = Math.Clamp(y - radius, 0, h - 1) * rowStride;
                int inRow = Math.Clamp(y + radius + 1, 0, h - 1) * rowStride;
                int c = 0;
                if (Avx2.IsSupported)
                {
                    for (; c <= rowStride - Vector256<int>.Count; c += Vector256<int>.Count)
                    {
                        var sum = Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(sums), (nuint)c);
                        var average = Avx2.ShiftRightLogical(Avx2.MultiplyLow(sum.AsUInt32(), reciprocalVector), 24);
                        var shorts = Sse2.PackSignedSaturate(average.GetLower().AsInt32(), average.GetUpper().AsInt32());
                        var bytes = Sse2.PackUnsignedSaturate(shorts, Vector128<short>.Zero);
                        BinaryPrimitives.WriteUInt64LittleEndian(target.AsSpan(row + c, 8), bytes.AsUInt64().GetElement(0));
                        var incoming = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(
                            BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(inRow + c, 8))).AsByte());
                        var outgoing = Avx2.ConvertToVector256Int32(Vector128.CreateScalar(
                            BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(outRow + c, 8))).AsByte());
                        Avx2.Add(sum, Avx2.Subtract(incoming, outgoing))
                            .StoreUnsafe(ref MemoryMarshal.GetReference(sums), (nuint)c);
                    }
                }
                for (; c < rowStride; c++)
                {
                    target[row + c] = (byte)(((uint)sums[c] * reciprocal) >> 24);
                    sums[c] += source[inRow + c] - source[outRow + c];
                }
            }
        }
        finally
        {
            if (rented != null) ArrayPool<int>.Shared.Return(rented);
        }
    }
}
