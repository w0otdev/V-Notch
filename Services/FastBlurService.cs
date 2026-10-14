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

                int actualW = smallBitmap.PixelWidth;
                int actualH = smallBitmap.PixelHeight;
                int stride = actualW * 4;
                int bufferSize = actualH * stride;

                byte[] pixels = ArrayPool<byte>.Shared.Rent(bufferSize);
                byte[] target = ArrayPool<byte>.Shared.Rent(bufferSize);

                try
                {
                    smallBitmap.CopyPixels(pixels, stride, 0);

                    int passes = 2;
                    for (int i = 0; i < passes; i++)
                    {
                        BoxBlurHorizontal(pixels, target, actualW, actualH, blurRadius);
                        BoxBlurVertical(target, pixels, actualW, actualH, blurRadius);
                    }

                    DarkenPixels(pixels, bufferSize);

                    var writeableBitmap = new WriteableBitmap(actualW, actualH, 96, 96, PixelFormats.Bgra32, null);
                    writeableBitmap.WritePixels(new Int32Rect(0, 0, actualW, actualH), pixels, stride, 0);
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

    internal static void DarkenPixels(byte[] pixels, int width, int height, float factor)
        => DarkenPixels(pixels, width * height * 4, factor);

    internal static void DarkenPixels(byte[] pixels, int length, float factor = 0.96f)
    {
        ushort scale = (ushort)Math.Clamp((int)Math.Ceiling(factor * 65536.0), 0, 65535);
        int i = 0;
        if (Sse2.IsSupported)
        {
            var factorVec = Vector128.Create(scale);
            var alphaMask = Vector128.Create(0xff000000u).AsByte();
            for (; i <= length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                var original = Vector128.LoadUnsafe(ref pixels[i]);
                var low = Sse2.UnpackLow(original, Vector128<byte>.Zero).AsUInt16();
                var high = Sse2.UnpackHigh(original, Vector128<byte>.Zero).AsUInt16();
                var dark = Sse2.PackUnsignedSaturate(
                    Sse2.MultiplyHigh(low, factorVec).AsInt16(),
                    Sse2.MultiplyHigh(high, factorVec).AsInt16());
                Sse2.Or(Sse2.AndNot(alphaMask, dark), Sse2.And(alphaMask, original))
                    .StoreUnsafe(ref pixels[i]);
            }
        }
        for (; i < length; i += 4)
        {
            pixels[i] = (byte)((pixels[i] * scale) >> 16);
            pixels[i + 1] = (byte)((pixels[i + 1] * scale) >> 16);
            pixels[i + 2] = (byte)((pixels[i + 2] * scale) >> 16);
        }
    }

    internal static void BoxBlurHorizontal(byte[] source, byte[] target, int w, int h, int radius)
    {
        int window = 2 * radius + 1;
        uint reciprocal = GetWindowReciprocal(window);
        int lastX = w - 1;

        for (int y = 0; y < h; y++)
        {
            int pBase = y * w * 4;
            int firstEdgeCount = radius + 1;
            int sumB = source[pBase] * firstEdgeCount;
            int sumG = source[pBase + 1] * firstEdgeCount;
            int sumR = source[pBase + 2] * firstEdgeCount;
            int sumA = source[pBase + 3] * firstEdgeCount;

            for (int dx = 1; dx <= radius; dx++)
            {
                int nx = dx < lastX ? dx : lastX;
                int o = pBase + nx * 4;
                sumB += source[o];
                sumG += source[o + 1];
                sumR += source[o + 2];
                sumA += source[o + 3];
            }

            if (w > window)
            {
                int x = 0;
                int rightLimit = w - radius - 1;
                for (; x < radius; x++)
                {
                    int t = pBase + x * 4;
                    target[t] = (byte)(((uint)sumB * reciprocal) >> 24);
                    target[t + 1] = (byte)(((uint)sumG * reciprocal) >> 24);
                    target[t + 2] = (byte)(((uint)sumR * reciprocal) >> 24);
                    target[t + 3] = (byte)(((uint)sumA * reciprocal) >> 24);

                    int oIn = pBase + (x + 1 + radius) * 4;
                    sumB += source[oIn] - source[pBase];
                    sumG += source[oIn + 1] - source[pBase + 1];
                    sumR += source[oIn + 2] - source[pBase + 2];
                    sumA += source[oIn + 3] - source[pBase + 3];
                }

                for (; x < rightLimit; x++)
                {
                    int t = pBase + x * 4;
                    target[t] = (byte)(((uint)sumB * reciprocal) >> 24);
                    target[t + 1] = (byte)(((uint)sumG * reciprocal) >> 24);
                    target[t + 2] = (byte)(((uint)sumR * reciprocal) >> 24);
                    target[t + 3] = (byte)(((uint)sumA * reciprocal) >> 24);

                    int oOut = pBase + (x - radius) * 4;
                    int oIn = pBase + (x + 1 + radius) * 4;
                    sumB += source[oIn] - source[oOut];
                    sumG += source[oIn + 1] - source[oOut + 1];
                    sumR += source[oIn + 2] - source[oOut + 2];
                    sumA += source[oIn + 3] - source[oOut + 3];
                }

                int oLast = pBase + lastX * 4;
                for (; x < w; x++)
                {
                    int t = pBase + x * 4;
                    target[t] = (byte)(((uint)sumB * reciprocal) >> 24);
                    target[t + 1] = (byte)(((uint)sumG * reciprocal) >> 24);
                    target[t + 2] = (byte)(((uint)sumR * reciprocal) >> 24);
                    target[t + 3] = (byte)(((uint)sumA * reciprocal) >> 24);

                    int oOut = pBase + (x - radius) * 4;
                    sumB += source[oLast] - source[oOut];
                    sumG += source[oLast + 1] - source[oOut + 1];
                    sumR += source[oLast + 2] - source[oOut + 2];
                    sumA += source[oLast + 3] - source[oOut + 3];
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    int t = pBase + x * 4;
                    target[t] = (byte)(((uint)sumB * reciprocal) >> 24);
                    target[t + 1] = (byte)(((uint)sumG * reciprocal) >> 24);
                    target[t + 2] = (byte)(((uint)sumR * reciprocal) >> 24);
                    target[t + 3] = (byte)(((uint)sumA * reciprocal) >> 24);

                    int outX = Math.Clamp(x - radius, 0, lastX);
                    int inX = Math.Clamp(x + 1 + radius, 0, lastX);

                    int oOut = pBase + outX * 4;
                    int oIn = pBase + inX * 4;
                    sumB += source[oIn] - source[oOut];
                    sumG += source[oIn + 1] - source[oOut + 1];
                    sumR += source[oIn + 2] - source[oOut + 2];
                    sumA += source[oIn + 3] - source[oOut + 3];
                }
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
        var reciprocalVector = Vector256.Create(reciprocal);
        try
        {
            int firstEdgeCount = radius + 1;
            for (int c = 0; c < rowStride; c++)
                sums[c] = source[c] * firstEdgeCount;

            for (int dy = 1; dy <= radius; dy++)
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
