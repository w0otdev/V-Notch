using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static class ThumbnailHeuristics
{
    public static bool IsLikelyPlaceholderThumbnail(BitmapImage? thumbnail)
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
            return true;
        }

        return HasLowEntropyMonochromeProfile(thumbnail);
    }

    public static bool IsLikelyArtworkCandidate(BitmapImage? thumbnail)
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

        return thumbnail.PixelWidth >= 360 &&
               thumbnail.PixelHeight >= 360 &&
               !IsLikelyPlaceholderThumbnail(thumbnail);
    }

    public static bool HasLowEntropyMonochromeProfile(BitmapImage thumbnail)
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

                var quantizedBins = new HashSet<int>();
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

                    quantizedBins.Add(((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5));
                    saturationSum += max == 0 ? 0 : (double)delta / max;
                }

                double avgSaturation = saturationSum / pixelCount;
                double grayRatio = (double)nearGrayCount / pixelCount;
                double brightRatio = (double)brightCount / pixelCount;

                return quantizedBins.Count <= 18 &&
                       avgSaturation <= 0.08 &&
                       grayRatio >= 0.84 &&
                       brightRatio >= 0.02;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch
        {
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
        bool skipSmtcThumbForFreshSoundCloudTrack = x.IsSoundCloudSource &&
                                                    x.TrackChanged &&
                                                    !x.HasVerifiedSoundCloudThumb &&
                                                    !x.LikelySoundCloudArtwork;

        double aspect = x.PixelHeight == 0 ? 0 : (double)x.PixelWidth / x.PixelHeight;
        bool isWideVideoFrame = aspect > 1.3;
        bool skipUnverifiedSmtcThumbAfterBrowserSessionChange =
            x.IsYouTubeLikeSource &&
            x.BrowserSessionChanged &&
            x.TrackChanged &&
            !x.HasVerifiedYouTubeThumb;
        bool skipSmtcThumbForFreshYouTubeTrack = x.IsYouTubeLikeSource &&
                                                 isWideVideoFrame &&
                                                 (x.TrackChanged || (x.RecentTrackChange && x.CachedThumbnailIsNull));

        if (skipSmtcThumbForFreshSoundCloudTrack ||
            skipSmtcThumbForFreshYouTubeTrack ||
            skipUnverifiedSmtcThumbAfterBrowserSessionChange)
        {
            return SmtcThumbnailDecision.Reject;
        }

        bool isSquare = Math.Abs(aspect - 1.0) < 0.05;
        bool isLikelySoundCloudPlaceholder = x.IsSoundCloudSource &&
                                             isSquare &&
                                             (x.PixelWidth <= 320 || x.PixelHeight <= 320);
        bool isUnverifiedBrowserIcon = x.IsBrowserSession &&
                                       x.IsBrowserOrYouTubePlatform &&
                                       !x.HasVerifiedYouTubeThumb &&
                                       isSquare;
        bool isGenericIcon = (x.IsBrowserOrYouTubePlatform || isLikelySoundCloudPlaceholder) &&
                             isSquare &&
                             (x.PixelWidth <= 300 || isUnverifiedBrowserIcon);
        bool shouldPreferVerifiedYouTubeLookup = x.IsYouTubeLikeSource &&
                                                 !x.HasVerifiedYouTubeThumb &&
                                                 !x.TrackChanged;

        if (!(isSquare && isGenericIcon) && !shouldPreferVerifiedYouTubeLookup)
        {
            return SmtcThumbnailDecision.Accept;
        }

        return SmtcThumbnailDecision.Skip;
    }
}
