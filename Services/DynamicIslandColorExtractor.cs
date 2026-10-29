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
    public readonly record struct PaletteColor(Color Color, int Population, double Score);

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
    private sealed record CroppedPalette(Int32Rect Crop, Palette Palette);
    private static readonly ConditionalWeakTable<BitmapSource, CroppedPalette> _croppedPaletteCache = new();
    private static readonly ConditionalWeakTable<BitmapSource, BoxedDim> _dimCache = new();

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
            var sub = EnsureTextOnDarkBackground(main, darkUiBackground, 4.5);
            palette = new Palette(main, sub);
        }

        _paletteCache.AddOrUpdate(bitmap, new BoxedPalette(palette));
        return palette;
    }

    public static Palette GetDynamicIslandPalette(BitmapSource bitmap, Rect smartCropBbox)
    {
        if (bitmap == null || !bitmap.CheckAccess())
            return GetDynamicIslandPalette(bitmap!);
        _ = TryFreezeBitmap(bitmap);

        Int32Rect? cropRect = GetClampedCropRect(smartCropBbox, bitmap.PixelWidth, bitmap.PixelHeight);
        if (cropRect is not Int32Rect crop)
            return GetDynamicIslandPalette(bitmap);

        if (_croppedPaletteCache.TryGetValue(bitmap, out var cached) && cached.Crop == crop)
            return cached.Palette;

        var cropped = new CroppedBitmap(bitmap, crop);
        _ = TryFreezeBitmap(cropped);
        Palette palette = GetDynamicIslandPalette(cropped);
        _croppedPaletteCache.AddOrUpdate(bitmap, new CroppedPalette(crop, palette));
        return palette;
    }

    internal static Int32Rect? GetClampedCropRect(Rect bounds, int pixelWidth, int pixelHeight)
    {
        if (bounds.IsEmpty)
            return null;

        vn_dicolor_get_clamped_crop_rect(
            bounds.Left, bounds.Top, bounds.Right, bounds.Bottom, 0,
            pixelWidth, pixelHeight,
            out int x, out int y, out int w, out int h, out byte valid);

        return valid != 0 ? new Int32Rect(x, y, w, h) : null;
    }

    public static Color GetDominantColor(BitmapSource bitmap)
    {
        var result = ExtractAdvancedPalette(bitmap);
        if (result.IsMonotone) return Colors.White;
        return result.Primary != default ? result.Primary : Color.FromRgb(30, 30, 30);
    }

    public static Color EnsureBrightColor(Color c)
    {
        vn_dicolor_ensure_bright(c.R, c.G, c.B, out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    public static Color GetVibrantColor(Color c)
    {
        vn_dicolor_get_vibrant(c.R, c.G, c.B, out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    public static double GetAdaptiveBlurOpacity(double luminance, double brightnessBoost = 1.0)
    {
        vn_dicolor_get_adaptive_blur_opacity(luminance, brightnessBoost, out double opacity);
        return opacity;
    }

    public static double GetAdaptiveBlurImageOpacity(double luminance)
    {
        vn_dicolor_get_adaptive_blur_image_opacity(luminance, out double opacity);
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
            var formattedBitmap = ArtworkAnalysisSource.GetBgra32(bitmap);

            double scaleX = (double)analysisSize / formattedBitmap.PixelWidth;
            double scaleY = (double)analysisSize / formattedBitmap.PixelHeight;
            var small = new TransformedBitmap(formattedBitmap, new ScaleTransform(scaleX, scaleY));

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

                return new PaletteResult(primary, secondary, secondary, false, nRes.IsFlatBg != 0, textOnPrimary);
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

    #region HSV / HSL / contrast helpers

    public static (double H, double S, double L) ToHsl(Color c)
    {
        vn_dicolor_to_hsl(c.R, c.G, c.B, out double h, out double s, out double l);
        return (h, s, l);
    }

    public static Color HslToColor(double h, double s, double l)
    {
        vn_dicolor_hsl_to_color(h, s, l, out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    public static double GetRelativeLuminance(Color c)
    {
        vn_dicolor_get_relative_luminance(c.R, c.G, c.B, out double lum);
        return lum;
    }

    public static double GetContrastRatio(Color a, Color b)
    {
        vn_dicolor_get_contrast_ratio(a.R, a.G, a.B, b.R, b.G, b.B, out double ratio);
        return ratio;
    }

    public static double ColorDistance(Color a, Color b)
    {
        vn_dicolor_color_distance(a.R, a.G, a.B, b.R, b.G, b.B, out double dist);
        return dist;
    }

    public static Color EnsureTextOnDarkBackground(Color color, Color background, double minRatio)
    {
        vn_dicolor_ensure_text_on_dark_bg(
            color.R, color.G, color.B,
            background.R, background.G, background.B,
            minRatio,
            out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    public static Color EnsureContrast(Color sub, Color main, double minRatio)
    {
        vn_dicolor_ensure_contrast(
            sub.R, sub.G, sub.B,
            main.R, main.G, main.B,
            minRatio,
            out byte r, out byte g, out byte b);
        return Color.FromRgb(r, g, b);
    }

    #endregion

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
    private static partial int vn_dicolor_get_clamped_crop_rect(
        double left, double top, double right, double bottom, byte is_empty,
        int pixel_width, int pixel_height,
        out int out_x, out int out_y, out int out_w, out int out_h, out byte out_valid);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_ensure_bright(
        byte r, byte g, byte b, out byte out_r, out byte out_g, out byte out_b);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_vibrant(
        byte r, byte g, byte b, out byte out_r, out byte out_g, out byte out_b);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_adaptive_blur_opacity(
        double luminance, double brightness_boost, out double out_opacity);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_adaptive_blur_image_opacity(
        double luminance, out double out_opacity);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dicolor_compute_brightness_dim(
        byte* pixels, int buf_len, int pixel_count, out double out_dim);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_dicolor_extract_advanced_palette(
        byte* pixels, int width, int height, int stride, out VnPaletteResult out_res);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_to_hsl(
        byte r, byte g, byte b, out double out_h, out double out_s, out double out_l);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_hsl_to_color(
        double h, double s, double l, out byte out_r, out byte out_g, out byte out_b);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_relative_luminance(
        byte r, byte g, byte b, out double out_lum);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_get_contrast_ratio(
        byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, out double out_ratio);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_color_distance(
        byte r1, byte g1, byte b1, byte r2, byte g2, byte b2, out double out_dist);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_ensure_text_on_dark_bg(
        byte cr, byte cg, byte cb, byte br, byte bg, byte bb, double min_ratio,
        out byte out_r, out byte out_g, out byte out_b);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_dicolor_ensure_contrast(
        byte sr, byte sg, byte sb, byte mr, byte mg, byte mb, double min_ratio,
        out byte out_r, out byte out_g, out byte out_b);
}
