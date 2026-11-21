using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static partial class DynamicIslandColorExtractor
{
    public readonly record struct Palette(Color Main, Color Sub);

    private const string LogTag = "COLOR-PICK";
    private static readonly Palette DefaultPalette = new(Color.FromRgb(34, 34, 34), Color.FromRgb(180, 180, 180));

    private sealed class BoxedPalette
    {
        public readonly Palette Palette;
        public BoxedPalette(Palette palette) => Palette = palette;
    }

    private sealed class BoxedDim
    {
        public readonly double Dim;
        public BoxedDim(double dim) => Dim = dim;
    }

    private static readonly ConditionalWeakTable<BitmapSource, BoxedPalette> _paletteCache = new();
    private static readonly ConditionalWeakTable<BitmapSource, BoxedDim> _dimCache = new();

    internal static void ClearCache()
    {
        _paletteCache.Clear();
        _dimCache.Clear();
    }

    #region Public entry points

    public static Task<Palette> PreloadDynamicIslandPaletteAsync(BitmapSource bitmap)
    {
        if (bitmap == null) return Task.FromResult(DefaultPalette);
        if (_paletteCache.TryGetValue(bitmap, out var cachedBox))
            return Task.FromResult(cachedBox.Palette);

        if (!bitmap.CheckAccess())
            return PreloadOnOwnerAsync(bitmap);

        return TryFreezeBitmap(bitmap)
            ? Task.Run(() => GetDynamicIslandPalette(bitmap))
            : Task.FromResult(GetDynamicIslandPalette(bitmap));
    }

    private static async Task<Palette> PreloadOnOwnerAsync(BitmapSource bitmap)
    {
        try
        {
            var dispatcher = bitmap.Dispatcher;
            if (dispatcher == null)
                return await Task.Run(() => GetDynamicIslandPalette(bitmap)).ConfigureAwait(false);
            Task<Palette> pending = await dispatcher.InvokeAsync(
                () => PreloadDynamicIslandPaletteAsync(bitmap)).Task.ConfigureAwait(false);
            return await pending.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            return DefaultPalette;
        }
    }

    private static bool TryFreezeBitmap(BitmapSource bitmap)
    {
        if (!bitmap.CheckAccess()) return false;
        if (bitmap.IsFrozen) return true;
        try
        {
            if (bitmap.CanFreeze)
                bitmap.Freeze();
        }
        catch (InvalidOperationException)
        {
            // Bitmap cannot be frozen on current thread.
        }
        return bitmap.IsFrozen;
    }

    public static Palette GetDynamicIslandPalette(BitmapSource bitmap)
    {
        if (bitmap == null)
            return DefaultPalette;

        if (_paletteCache.TryGetValue(bitmap, out var cachedBox))
            return cachedBox.Palette;
        if (!bitmap.CheckAccess())
            return DefaultPalette;

        var result = ExtractAdvancedPalette(bitmap);
        Palette palette;
        if (result.IsMonotone || result.Primary == default)
        {
            RuntimeLog.Debug(LogTag,
                $"FALLBACK: IsMonotone={result.IsMonotone} Primary={result.Primary} (R={result.Primary.R},G={result.Primary.G},B={result.Primary.B})");
            palette = DefaultPalette;
        }
        else
        {
            RuntimeLog.Debug(LogTag,
                $"OK: Primary=({result.Primary.R},{result.Primary.G},{result.Primary.B}) Secondary=({result.Secondary.R},{result.Secondary.G},{result.Secondary.B})");
            var main = result.Primary;
            var darkUiBackground = Colors.Black;
            var sub = result.Accent != default ? result.Accent : EnsureTextOnDarkBackground(main, darkUiBackground, 4.5);
            palette = new Palette(main, sub);
        }

        _paletteCache.AddOrUpdate(bitmap, new BoxedPalette(palette));
        return palette;
    }

    public static double GetAdaptiveBlurOpacity(double luminance, double brightnessBoost = 1.0)
    {
        vn_dicolor_get_adaptive_blur_opacity(luminance, brightnessBoost, out double opacity);
        return opacity;
    }

    public static double GetBrightnessDimOverlay(BitmapSource bitmap)
    {
        if (bitmap == null) return 0;

        if (_dimCache.TryGetValue(bitmap, out var cachedDim))
            return cachedDim.Dim;

        try
        {
            int sampleSize = 32;
            var formatted = ArtworkAnalysisSource.GetBgra32(bitmap);
            var scaled = new TransformedBitmap(formatted,
                new ScaleTransform((double)sampleSize / formatted.PixelWidth, (double)sampleSize / formatted.PixelHeight));

            int actualW = scaled.PixelWidth;
            int actualH = scaled.PixelHeight;
            int stride = actualW * 4;
            int bufLen = actualH * stride;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                scaled.CopyPixels(pixels, stride, 0);
                double result;
                unsafe
                {
                    fixed (byte* p = pixels)
                    {
                        vn_dicolor_compute_brightness_dim(p, bufLen, actualW * actualH, out result);
                    }
                }
                _dimCache.AddOrUpdate(bitmap, new BoxedDim(result));
                return result;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region Advanced palette extraction

    public readonly record struct PaletteResult(
        Color Primary, Color Secondary, Color Accent,
        bool IsMonotone, bool IsFlatBg, Color TextOnPrimary);

    private static PaletteResult ExtractAdvancedPalette(BitmapSource bitmap, int analysisSize = 36)
    {
        if (bitmap == null)
            return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);

        try
        {
            var small = ArtworkAnalysisSource.GetSample(bitmap, analysisSize, analysisSize);

            int width = small.PixelWidth;
            int height = small.PixelHeight;
            if (width <= 4 || height <= 4)
                return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);

            int stride = width * 4;
            int bufLen = height * stride;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                small.CopyPixels(pixels, stride, 0);

                VnPaletteResult nRes;
                unsafe
                {
                    fixed (byte* p = pixels)
                    {
                        vn_dicolor_extract_advanced_palette(p, width, height, stride, out nRes);
                    }
                }

                if (nRes.IsMonotone != 0)
                    return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);

                Color primary = Color.FromArgb(nRes.PrimaryA, nRes.PrimaryR, nRes.PrimaryG, nRes.PrimaryB);
                Color secondary = nRes.SecondaryA != 0
                    ? Color.FromArgb(nRes.SecondaryA, nRes.SecondaryR, nRes.SecondaryG, nRes.SecondaryB)
                    : default;
                Color textOnPrimary = Color.FromArgb(nRes.TextA, nRes.TextR, nRes.TextG, nRes.TextB);

                Color accent = nRes.AccentA != 0 ? Color.FromArgb(nRes.AccentA, nRes.AccentR, nRes.AccentG, nRes.AccentB) : secondary;
                return new PaletteResult(primary, secondary, accent, false, nRes.IsFlatBg != 0, textOnPrimary);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch
        {
            return new PaletteResult(Color.FromRgb(30, 30, 30), default, default, true, false, Colors.White);
        }
    }

    #endregion

    public static Color EnsureTextOnDarkBackground(Color color, Color background, double minRatio)
    {
        vn_dicolor_ensure_text_on_dark_bg(
            color.R, color.G, color.B,
            background.R, background.G, background.B,
            minRatio,
            out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnPaletteResult
    {
        public byte PrimaryR;
        public byte PrimaryG;
        public byte PrimaryB;
        public byte PrimaryA;
        public byte SecondaryR;
        public byte SecondaryG;
        public byte SecondaryB;
        public byte SecondaryA;
        public byte AccentR;
        public byte AccentG;
        public byte AccentB;
        public byte AccentA;
        public byte IsMonotone;
        public byte IsFlatBg;
        public byte TextR;
        public byte TextG;
        public byte TextB;
        public byte TextA;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_adaptive_blur_opacity(
        double luminance, double brightness_boost, out double out_opacity);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dicolor_compute_brightness_dim(
        byte* pixels, int buf_len, int pixel_count, out double out_dim);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dicolor_extract_advanced_palette(
        byte* pixels, int width, int height, int stride, out VnPaletteResult out_res);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_ensure_text_on_dark_bg(
        byte cr, byte cg, byte cb, byte br, byte bg, byte bb, double min_ratio,
        out byte out_r, out byte out_g, out byte out_b);

}
