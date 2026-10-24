using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

public interface IColorExtractionService
{
    Color ExtractDominantColor(BitmapSource? image);
}

public sealed partial class ColorExtractionService : IColorExtractionService
{
    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_color_extract_sampled(
        byte* pixels,
        int pixelLen,
        int width,
        int height,
        double* coords,
        int coordsLen,
        out byte outR,
        out byte outG,
        out byte outB);

    public Color ExtractDominantColor(BitmapSource? image)
    {
        if (image == null)
        {
            return Color.FromRgb(255, 255, 255);
        }

        try
        {
            const int sampleDimension = 64;
            var formatConvertedBitmap = ArtworkAnalysisSource.GetBgra32(image);

            double scaleX = (double)sampleDimension / formatConvertedBitmap.PixelWidth;
            double scaleY = (double)sampleDimension / formatConvertedBitmap.PixelHeight;
            BitmapSource smallBitmap = formatConvertedBitmap.PixelWidth == sampleDimension && formatConvertedBitmap.PixelHeight == sampleDimension
                ? formatConvertedBitmap
                : new TransformedBitmap(formatConvertedBitmap, new ScaleTransform(scaleX, scaleY));

            int width = smallBitmap.PixelWidth;
            int height = smallBitmap.PixelHeight;
            int stride = width * 4;
            int bufLen = height * stride;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                smallBitmap.CopyPixels(pixels, stride, 0);
                return ExtractSampledColor(pixels.AsSpan(0, bufLen), width, height);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception)
        {
            return Color.FromRgb(255, 255, 255);
        }
    }

    private static readonly double[] SampleCoordinates = CreateSampleCoordinates();

    private static double[] CreateSampleCoordinates()
    {
#pragma warning disable S2245 // Deterministic artwork sampling, not cryptography
        var random = new Random(42);
#pragma warning restore S2245
        var coordinates = new double[600];
        for (int i = 0; i < coordinates.Length; i++) coordinates[i] = random.NextDouble();
        return coordinates;
    }

    internal static unsafe Color ExtractSampledColor(ReadOnlySpan<byte> pixels, int width, int height)
    {
        fixed (byte* pPix = pixels)
        fixed (double* pCoords = SampleCoordinates)
        {
            _ = vn_color_extract_sampled(
                pPix, pixels.Length, width, height, pCoords, SampleCoordinates.Length,
                out byte r, out byte g, out byte b);
            return Color.FromRgb(r, g, b);
        }
    }
}
