using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static class ThumbnailHeuristics
{
    public static bool IsLikelyPlaceholderThumbnail(BitmapSource? thumbnail)
    {
        if (thumbnail == null || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0)
        {
            return true;
        }

        double aspect = (double)thumbnail.PixelWidth / thumbnail.PixelHeight;
        bool isSquare = Math.Abs(aspect - 1.0) < 0.06;
        if (!isSquare)
        {
            return false;
        }

        if (thumbnail.PixelWidth <= 320 || thumbnail.PixelHeight <= 320)
        {
            return !IsLikelyArtworkCandidate(thumbnail);
        }

        return HasLowEntropyMonochromeProfile(thumbnail);
    }

    public static bool IsLikelyArtworkCandidate(BitmapSource? thumbnail)
    {
        if (thumbnail == null || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0)
        {
            return false;
        }

        double aspect = (double)thumbnail.PixelWidth / thumbnail.PixelHeight;
        bool isSquare = Math.Abs(aspect - 1.0) < 0.08;
        if (!isSquare)
        {
            return false;
        }

        // Browser media sessions can supply real covers at only 150x150.
        // Inspect their content instead of discarding every small square image.
        return thumbnail.PixelWidth >= 120 &&
               thumbnail.PixelHeight >= 120 &&
               !HasPlaceholderProfile(thumbnail, rejectSimpleColorIcons: thumbnail.PixelWidth <= 320 || thumbnail.PixelHeight <= 320);
    }

    public static bool HasLowEntropyMonochromeProfile(BitmapSource thumbnail)
        => HasPlaceholderProfile(thumbnail, rejectSimpleColorIcons: false);

    private static bool HasPlaceholderProfile(BitmapSource thumbnail, bool rejectSimpleColorIcons)
    {
        try
        {
            int sampleSize = 24;
            double scale = Math.Min(
                1.0,
                (double)sampleSize / Math.Max(thumbnail.PixelWidth, thumbnail.PixelHeight));

            BitmapSource source = thumbnail;
            if (scale < 1.0)
            {
                var transformed = new TransformedBitmap(thumbnail, new ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            formatted.Freeze();

            int width = Math.Max(1, formatted.PixelWidth);
            int height = Math.Max(1, formatted.PixelHeight);
            int stride = width * 4;
            int bufLen = stride * height;
            byte[] pixels = System.Buffers.ArrayPool<byte>.Shared.Rent(bufLen);

            try
            {
                formatted.CopyPixels(pixels, stride, 0);

                Span<ulong> bins = stackalloc ulong[8];
                ulong lumBins = 0;
                double saturationSum = 0;
                int nearGrayCount = 0;
                int brightCount = 0;
                int pixelCount = width * height;

                for (int i = 0; i < bufLen; i += 4)
                {
                    byte b = pixels[i];
                    byte g = pixels[i + 1];
                    byte r = pixels[i + 2];

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    int delta = max - min;

                    if (delta <= 12) nearGrayCount++;
                    if (max >= 220 && min >= 220) brightCount++;

                    int bin = ((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5);
                    bins[bin >> 6] |= 1UL << (bin & 63);
                    int lum = (r * 77 + g * 150 + b * 29) >> 10;
                    lumBins |= 1UL << (lum & 63);
                    saturationSum += max == 0 ? 0 : (double)delta / max;
                }

                int uniqueBinCount = 0;
                for (int i = 0; i < bins.Length; i++)
                {
                    uniqueBinCount += System.Numerics.BitOperations.PopCount(bins[i]);
                }

                int uniqueLumBins = System.Numerics.BitOperations.PopCount(lumBins);
                double avgSaturation = saturationSum / pixelCount;
                double grayRatio = (double)nearGrayCount / pixelCount;
                double brightRatio = (double)brightCount / pixelCount;

                return (rejectSimpleColorIcons && uniqueBinCount <= 18) ||
                       (uniqueBinCount <= 18 &&
                       uniqueLumBins <= 12 &&
                       avgSaturation <= 0.08 &&
                       grayRatio >= 0.84 &&
                       brightRatio >= 0.02);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception)
        {
            // Corrupt bitmap or conversion failure; treat as not low-entropy monochrome.
            return false;
        }
    }

    public enum SmtcThumbnailDecision
    {
        Reject,
        Accept,
        Skip,
    }

    public readonly struct SmtcThumbnailInputs
    {
        public bool IsYouTubeLikeSource { get; init; }
        public bool IsSoundCloudSource { get; init; }
        public bool IsBrowserOrYouTubePlatform { get; init; }
        public bool IsBrowserSession { get; init; }
        public bool TrackChanged { get; init; }
        public bool BrowserSessionChanged { get; init; }
        public bool HasVerifiedYouTubeThumb { get; init; }
        public bool HasVerifiedSoundCloudThumb { get; init; }
        public bool LikelySoundCloudArtwork { get; init; }
        public bool RecentTrackChange { get; init; }
        public bool CachedThumbnailIsNull { get; init; }
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
    }

    public static SmtcThumbnailDecision DecideSmtcThumbnail(in SmtcThumbnailInputs x)
    {
        double aspect = x.PixelHeight == 0 ? 0 : (double)x.PixelWidth / x.PixelHeight;
        if (ShouldRejectSmtcThumbnail(in x, aspect))
        {
            return SmtcThumbnailDecision.Reject;
        }

        bool isSquare = Math.Abs(aspect - 1.0) < 0.05;
        bool shouldPreferVerifiedYouTubeLookup = x.IsYouTubeLikeSource &&
                                                 !x.HasVerifiedYouTubeThumb &&
                                                 !x.TrackChanged;

        if (!IsGenericOrPlaceholderIcon(in x, isSquare) && !shouldPreferVerifiedYouTubeLookup)
        {
            return SmtcThumbnailDecision.Accept;
        }

        return SmtcThumbnailDecision.Skip;
    }

    private static bool ShouldRejectSmtcThumbnail(in SmtcThumbnailInputs x, double aspect)
    {
        if (x.IsSoundCloudSource &&
            !x.HasVerifiedSoundCloudThumb &&
            !x.LikelySoundCloudArtwork &&
            (x.TrackChanged || (x.RecentTrackChange && x.CachedThumbnailIsNull)))
        {
            return true;
        }

        if (x.IsYouTubeLikeSource && x.BrowserSessionChanged && x.TrackChanged && !x.HasVerifiedYouTubeThumb)
        {
            return true;
        }

        bool isWideVideoFrame = aspect > 1.3;
        return x.IsYouTubeLikeSource &&
               isWideVideoFrame &&
               (x.TrackChanged || (x.RecentTrackChange && x.CachedThumbnailIsNull));
    }

    private static bool IsGenericOrPlaceholderIcon(in SmtcThumbnailInputs x, bool isSquare)
    {
        if (x.LikelySoundCloudArtwork && !x.IsYouTubeLikeSource)
            return false;

        if (!isSquare)
        {
            return false;
        }

        bool isLikelySoundCloudPlaceholder = x.IsSoundCloudSource &&
                                             !x.HasVerifiedSoundCloudThumb &&
                                             !x.LikelySoundCloudArtwork;
        if (isLikelySoundCloudPlaceholder)
        {
            return true;
        }

        bool isUnverifiedBrowserIcon = x.IsBrowserSession &&
                                       x.IsBrowserOrYouTubePlatform &&
                                       !x.HasVerifiedYouTubeThumb;

        return x.IsBrowserOrYouTubePlatform &&
               (x.PixelWidth <= 300 || isUnverifiedBrowserIcon);
    }
}
