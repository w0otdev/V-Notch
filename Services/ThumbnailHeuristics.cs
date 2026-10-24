using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static partial class ThumbnailHeuristics
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

                byte hasProfile;
                unsafe
                {
                    fixed (byte* p = pixels)
                    {
                        vn_thumbnail_has_placeholder_profile(
                            p, bufLen, width * height,
                            rejectSimpleColorIcons ? (byte)1 : (byte)0,
                            out hasProfile);
                    }
                }
                return hasProfile != 0;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        catch (Exception)
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
        var nInputs = new VnSmtcThumbnailInputs
        {
            IsYouTubeLikeSource = x.IsYouTubeLikeSource ? (byte)1 : (byte)0,
            IsSoundCloudSource = x.IsSoundCloudSource ? (byte)1 : (byte)0,
            IsBrowserOrYouTubePlatform = x.IsBrowserOrYouTubePlatform ? (byte)1 : (byte)0,
            IsBrowserSession = x.IsBrowserSession ? (byte)1 : (byte)0,
            TrackChanged = x.TrackChanged ? (byte)1 : (byte)0,
            BrowserSessionChanged = x.BrowserSessionChanged ? (byte)1 : (byte)0,
            HasVerifiedYouTubeThumb = x.HasVerifiedYouTubeThumb ? (byte)1 : (byte)0,
            HasVerifiedSoundCloudThumb = x.HasVerifiedSoundCloudThumb ? (byte)1 : (byte)0,
            LikelySoundCloudArtwork = x.LikelySoundCloudArtwork ? (byte)1 : (byte)0,
            RecentTrackChange = x.RecentTrackChange ? (byte)1 : (byte)0,
            CachedThumbnailIsNull = x.CachedThumbnailIsNull ? (byte)1 : (byte)0,
            PixelWidth = x.PixelWidth,
            PixelHeight = x.PixelHeight,
        };
        vn_thumbnail_decide_smtc(in nInputs, out int decision);
        return (SmtcThumbnailDecision)decision;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VnSmtcThumbnailInputs
    {
        public byte IsYouTubeLikeSource;
        public byte IsSoundCloudSource;
        public byte IsBrowserOrYouTubePlatform;
        public byte IsBrowserSession;
        public byte TrackChanged;
        public byte BrowserSessionChanged;
        public byte HasVerifiedYouTubeThumb;
        public byte HasVerifiedSoundCloudThumb;
        public byte LikelySoundCloudArtwork;
        public byte RecentTrackChange;
        public byte CachedThumbnailIsNull;
        public int PixelWidth;
        public int PixelHeight;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_thumbnail_has_placeholder_profile(
        byte* pixels, int buf_len, int pixel_count, byte reject_simple_color_icons, out byte out_has_profile);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_thumbnail_decide_smtc(
        in VnSmtcThumbnailInputs inputs, out int out_decision);
}
