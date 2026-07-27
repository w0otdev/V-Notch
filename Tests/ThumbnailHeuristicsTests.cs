using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class ThumbnailHeuristicsTests
{

    private static BitmapImage FromPixels(int width, int height, byte[] bgra)
    {
        var src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapImage Solid(int width, int height, byte r, byte g, byte b)
    {
        var px = new byte[width * height * 4];
        for (int i = 0; i < px.Length; i += 4)
        {
            px[i] = b; px[i + 1] = g; px[i + 2] = r; px[i + 3] = 255;
        }
        return FromPixels(width, height, px);
    }

    private static BitmapImage Colorful(int width, int height)
    {
        var px = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool red = ((x / 8) + (y / 8)) % 2 == 0;
                px[i] = (byte)(red ? 0 : 255);
                px[i + 1] = 0;
                px[i + 2] = (byte)(red ? 255 : 0);
                px[i + 3] = 255;
            }
        }
        return FromPixels(width, height, px);
    }

    #region IsLikelyPlaceholderThumbnail

    [Fact]
    public void Placeholder_Null_True()
    {
        Assert.True(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(null));
    }

    [Fact]
    public void Placeholder_SmallSquare_True()
    {
        Assert.True(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Colorful(200, 200)));
    }

    [Fact]
    public void Placeholder_NonSquare_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Colorful(640, 320)));
    }

    [Fact]
    public void Placeholder_LargeSquare_LowEntropyGray_True()
    {
        Assert.True(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Solid(500, 500, 235, 235, 235)));
    }

    [Fact]
    public void Placeholder_LargeSquare_Colorful_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyPlaceholderThumbnail(Colorful(500, 500)));
    }

    #endregion

    #region IsLikelyArtworkCandidate

    [Fact]
    public void Candidate_Null_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(null));
    }

    [Fact]
    public void Candidate_LargeColorfulSquare_True()
    {
        Assert.True(ThumbnailHeuristics.IsLikelyArtworkCandidate(Colorful(500, 500)));
    }

    [Fact]
    public void Candidate_TooSmall_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(Colorful(300, 300)));
    }

    [Fact]
    public void Candidate_NonSquare_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(Colorful(640, 360)));
    }

    [Fact]
    public void Candidate_LargeGraySquare_False()
    {
        Assert.False(ThumbnailHeuristics.IsLikelyArtworkCandidate(Solid(500, 500, 235, 235, 235)));
    }

    #endregion

    #region HasLowEntropyMonochromeProfile

    [Fact]
    public void MonochromeProfile_LightGray_True()
    {
        Assert.True(ThumbnailHeuristics.HasLowEntropyMonochromeProfile(Solid(400, 400, 235, 235, 235)));
    }

    [Fact]
    public void MonochromeProfile_Colorful_False()
    {
        Assert.False(ThumbnailHeuristics.HasLowEntropyMonochromeProfile(Colorful(400, 400)));
    }

    [Fact]
    public void MonochromeProfile_SolidBlack_False()
    {
        Assert.False(ThumbnailHeuristics.HasLowEntropyMonochromeProfile(Solid(400, 400, 0, 0, 0)));
    }

    #endregion

    #region DecideSmtcThumbnail

    private static readonly ThumbnailHeuristics.SmtcThumbnailInputs WideYouTube = new()
    {
        IsYouTubeLikeSource = true,
        IsBrowserOrYouTubePlatform = true,
        PixelWidth = 640,
        PixelHeight = 360,
    };

    [Fact]
    public void Decide_YouTubeWideFrame_OnTrackChange_Rejects()
    {
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Reject,
            ThumbnailHeuristics.DecideSmtcThumbnail(WideYouTube with { TrackChanged = true }));
    }

    [Fact]
    public void Decide_YouTubeSquareImage_AfterBrowserSessionReplacement_Rejects()
    {
        var inputs = new ThumbnailHeuristics.SmtcThumbnailInputs
        {
            IsYouTubeLikeSource = true,
            IsBrowserOrYouTubePlatform = true,
            TrackChanged = true,
            BrowserSessionChanged = true,
            HasVerifiedYouTubeThumb = false,
            PixelWidth = 512,
            PixelHeight = 512,
        };

        Assert.Equal(
            ThumbnailHeuristics.SmtcThumbnailDecision.Reject,
            ThumbnailHeuristics.DecideSmtcThumbnail(inputs));
    }

    [Fact]
    public void Decide_YouTubeSquareImage_SameSession_CanStillBeAccepted()
    {
        var inputs = new ThumbnailHeuristics.SmtcThumbnailInputs
        {
            IsYouTubeLikeSource = true,
            IsBrowserOrYouTubePlatform = true,
            TrackChanged = true,
            BrowserSessionChanged = false,
            PixelWidth = 512,
            PixelHeight = 512,
        };

        Assert.Equal(
            ThumbnailHeuristics.SmtcThumbnailDecision.Accept,
            ThumbnailHeuristics.DecideSmtcThumbnail(inputs));
    }

    [Fact]
    public void Decide_YouTubeWideFrame_RecentChangeNoCache_Rejects()
    {
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Reject,
            ThumbnailHeuristics.DecideSmtcThumbnail(WideYouTube with { RecentTrackChange = true, CachedThumbnailIsNull = true }));
    }

    [Fact]
    public void Decide_YouTubeWideFrame_StableTrackWithVerifiedThumb_Accepts()
    {
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Accept,
            ThumbnailHeuristics.DecideSmtcThumbnail(WideYouTube with
            {
                RecentTrackChange = true,
                CachedThumbnailIsNull = false,
                HasVerifiedYouTubeThumb = true,
            }));
    }

    [Fact]
    public void Decide_YouTubeWideFrame_StableTrackNoVerifiedThumb_Skips()
    {
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Skip,
            ThumbnailHeuristics.DecideSmtcThumbnail(WideYouTube with { RecentTrackChange = true, CachedThumbnailIsNull = false }));
    }

    [Fact]
    public void Decide_SoundCloudNonArtwork_OnTrackChange_Rejects()
    {
        var inputs = new ThumbnailHeuristics.SmtcThumbnailInputs
        {
            IsSoundCloudSource = true,
            TrackChanged = true,
            HasVerifiedSoundCloudThumb = false,
            LikelySoundCloudArtwork = false,
            PixelWidth = 500,
            PixelHeight = 500,
        };
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Reject,
            ThumbnailHeuristics.DecideSmtcThumbnail(inputs));
    }

    [Fact]
    public void Decide_SoundCloudRealArtwork_OnTrackChange_Accepts()
    {
        var inputs = new ThumbnailHeuristics.SmtcThumbnailInputs
        {
            IsSoundCloudSource = true,
            TrackChanged = true,
            LikelySoundCloudArtwork = true,
            PixelWidth = 500,
            PixelHeight = 500,
        };
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Accept,
            ThumbnailHeuristics.DecideSmtcThumbnail(inputs));
    }

    [Fact]
    public void Decide_LargeSquareArtwork_Accepts()
    {
        var inputs = new ThumbnailHeuristics.SmtcThumbnailInputs
        {
            TrackChanged = true,
            PixelWidth = 640,
            PixelHeight = 640,
        };
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Accept,
            ThumbnailHeuristics.DecideSmtcThumbnail(inputs));
    }

    [Fact]
    public void Decide_SmallSquareGenericIcon_Skips()
    {
        var inputs = new ThumbnailHeuristics.SmtcThumbnailInputs
        {
            IsBrowserOrYouTubePlatform = true,
            TrackChanged = true,
            PixelWidth = 256,
            PixelHeight = 256,
        };
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Skip,
            ThumbnailHeuristics.DecideSmtcThumbnail(inputs));
    }

    [Fact]
    public void Decide_UnchangedYouTubeTrackWithoutVerifiedThumb_Skips()
    {
        var inputs = new ThumbnailHeuristics.SmtcThumbnailInputs
        {
            IsYouTubeLikeSource = true,
            IsBrowserOrYouTubePlatform = true,
            TrackChanged = false,
            HasVerifiedYouTubeThumb = false,
            PixelWidth = 640,
            PixelHeight = 640,
        };
        Assert.Equal(ThumbnailHeuristics.SmtcThumbnailDecision.Skip,
            ThumbnailHeuristics.DecideSmtcThumbnail(inputs));
    }

    #endregion
}
