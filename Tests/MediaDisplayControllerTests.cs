using System.Windows.Media.Imaging;
using VNotch.Controllers;
using VNotch.Models;
using Xunit;

namespace VNotch.Tests;

public class MediaDisplayControllerTests
{
    private readonly MediaDisplayController _sut = new();

    private static BitmapImage NewThumb() => new BitmapImage();

    private static MediaInfo Track(string title, string artist = "A", string source = "Spotify",
        BitmapImage? thumb = null, bool playing = true)
        => new MediaInfo
        {
            CurrentTrack = title,
            CurrentArtist = artist,
            MediaSource = source,
            Thumbnail = thumb,
            IsAnyMediaPlaying = true,
            IsPlaying = playing
        };

    #region ShouldBeCompactMode

    [Fact]
    public void ShouldBeCompactMode_Null_False()
    {
        Assert.False(MediaDisplayController.ShouldBeCompactMode(null));
    }

    [Fact]
    public void ShouldBeCompactMode_NotPlaying_False()
    {
        var info = Track("Song");
        info.IsAnyMediaPlaying = false;
        Assert.False(MediaDisplayController.ShouldBeCompactMode(info));
    }

    [Fact]
    public void ShouldBeCompactMode_EmptyTrack_False()
    {
        var info = Track("");
        Assert.False(MediaDisplayController.ShouldBeCompactMode(info));
    }

    [Fact]
    public void ShouldBeCompactMode_PlayingWithTrack_True()
    {
        Assert.True(MediaDisplayController.ShouldBeCompactMode(Track("Song")));
    }

    [Fact]
    public void ShouldBeCompactMode_BrowserWithTrack_True()
    {
        Assert.True(MediaDisplayController.ShouldBeCompactMode(Track("Some video", source: "Browser")));
    }

    #endregion

    #region ShouldAnimateCompactThumbnail

    [Fact]
    public void ShouldAnimateCompactThumbnail_NoThumbnail_False()
    {
        Assert.False(_sut.ShouldAnimateCompactThumbnail(Track("Song")));
    }

    [Fact]
    public void ShouldAnimateCompactThumbnail_NewTrack_True_ThenSameTrack_False()
    {
        var info = Track("Song", thumb: NewThumb());
        Assert.True(_sut.ShouldAnimateCompactThumbnail(info));
        Assert.False(_sut.ShouldAnimateCompactThumbnail(Track("Song", thumb: NewThumb())));
    }

    #endregion

    #region ProcessMediaUpdate — track identity & thumbnail actions

    [Fact]
    public void ProcessMediaUpdate_FirstTrackWithThumbnail_RevealsFirst()
    {
        var result = _sut.ProcessMediaUpdate(Track("Song", thumb: NewThumb()),
            isExpanded: true, isMusicExpanded: false, isMusicCompactMode: false, isAnimating: false);

        Assert.Equal(MediaDisplayAction.Update, result.Action);
        Assert.True(result.IsNewTrack);
        Assert.True(result.HasRealTrack);
        Assert.True(result.HasThumbnail);
        Assert.Equal(ThumbnailAction.RevealFirst, result.ThumbnailAction);
        Assert.True(result.NeedsBackgroundUpdate);
        Assert.Equal("Song|A", result.TrackIdentity);
    }

    [Fact]
    public void ProcessMediaUpdate_SecondDistinctTrack_AnimatesSwitch()
    {
        _sut.ProcessMediaUpdate(Track("Song1", thumb: NewThumb()), true, false, false, false);
        var result = _sut.ProcessMediaUpdate(Track("Song2", thumb: NewThumb()), true, false, false, false);

        Assert.True(result.IsNewTrack);
        Assert.Equal(ThumbnailAction.AnimateSwitch, result.ThumbnailAction);
    }

    [Fact]
    public void ProcessMediaUpdate_SameTrackSameThumbnail_NoThumbnailAction()
    {
        var thumb = NewThumb();
        _sut.ProcessMediaUpdate(Track("Song", thumb: thumb), true, false, false, false);
        var result = _sut.ProcessMediaUpdate(Track("Song", thumb: thumb), true, false, false, false);

        Assert.False(result.IsNewTrack);
        Assert.Equal(ThumbnailAction.None, result.ThumbnailAction);
    }

    [Fact]
    public void ProcessMediaUpdate_NewTrackWithoutThumbnail_ShowsFallback()
    {
        var result = _sut.ProcessMediaUpdate(Track("Song", thumb: null), true, false, false, false);

        Assert.Equal(MediaDisplayAction.Update, result.Action);
        Assert.True(result.IsNewTrack);
        Assert.True(result.HasRealTrack);
        Assert.False(result.HasThumbnail);
        Assert.Equal(ThumbnailAction.ShowFallback, result.ThumbnailAction);
    }

    [Fact]
    public void ProcessMediaUpdate_StaleThumbnailOnlyUpdate_IsIgnored()
    {
        _sut.ProcessMediaUpdate(Track("Song", thumb: NewThumb()), true, false, false, false);

        var stale = Track("OtherSong", thumb: NewThumb());
        stale.IsThumbnailOnlyUpdate = true;

        var result = _sut.ProcessMediaUpdate(stale, true, false, false, false);
        Assert.Equal(MediaDisplayAction.Ignore, result.Action);
    }

    #endregion

    #region ProcessMediaUpdate — clear vs update for no-track

    [Fact]
    public void ProcessMediaUpdate_NoTrackNotPlaying_Clears()
    {
        var info = new MediaInfo { CurrentTrack = "", IsAnyMediaPlaying = false };
        var result = _sut.ProcessMediaUpdate(info, true, false, false, false);

        Assert.Equal(MediaDisplayAction.Clear, result.Action);
        Assert.False(result.HasRealTrack);
    }

    [Fact]
    public void ProcessMediaUpdate_NoTrackButPlaying_Updates()
    {
        var info = new MediaInfo { CurrentTrack = "", IsAnyMediaPlaying = true };
        var result = _sut.ProcessMediaUpdate(info, true, false, false, false);

        Assert.Equal(MediaDisplayAction.Update, result.Action);
        Assert.False(result.HasRealTrack);
    }

    #endregion

    #region ProcessMediaUpdate — display text resolution

    [Fact]
    public void ProcessMediaUpdate_NoTrack_DisplaysPlaceholders()
    {
        var info = new MediaInfo { CurrentTrack = "", IsAnyMediaPlaying = false };
        var result = _sut.ProcessMediaUpdate(info, true, false, false, false);

        Assert.Equal("No media playing", result.DisplayText.Title);
        Assert.Equal("Artist name", result.DisplayText.Artist);
    }

    [Fact]
    public void ProcessMediaUpdate_RealArtist_UsedDirectly()
    {
        var result = _sut.ProcessMediaUpdate(Track("Song", artist: "Daft Punk", thumb: NewThumb()),
            true, false, false, false);

        Assert.Equal("Song", result.DisplayText.Title);
        Assert.Equal("Daft Punk", result.DisplayText.Artist);
    }

    [Fact]
    public void ProcessMediaUpdate_TopicSuffix_IsStripped()
    {
        var result = _sut.ProcessMediaUpdate(Track("Song", artist: "Some Band - Topic", thumb: NewThumb()),
            true, false, false, false);

        Assert.Equal("Some Band", result.DisplayText.Artist);
    }

    [Fact]
    public void ProcessMediaUpdate_GenericArtist_FallsBackToSource()
    {
        var result = _sut.ProcessMediaUpdate(Track("Song", artist: "YouTube", source: "YouTube", thumb: NewThumb()),
            true, false, false, false);

        Assert.Equal("YouTube", result.DisplayText.Artist);
    }

    #endregion

    #region Generation counter

    [Fact]
    public void IncrementGeneration_IsMonotonic()
    {
        Assert.Equal(0, _sut.ThumbnailSwitchGeneration);
        Assert.Equal(1, _sut.IncrementGeneration());
        Assert.Equal(2, _sut.IncrementGeneration());
        Assert.Equal(2, _sut.ThumbnailSwitchGeneration);
    }

    [Fact]
    public void Reset_ClearsSignaturesAndBumpsGeneration()
    {
        _sut.ProcessMediaUpdate(Track("Song", thumb: NewThumb()), true, false, false, false);
        int genBefore = _sut.ThumbnailSwitchGeneration;

        _sut.Reset();

        Assert.Equal("", _sut.LastAnimatedTrackSignature);
        Assert.True(_sut.ThumbnailSwitchGeneration > genBefore);

        var result = _sut.ProcessMediaUpdate(Track("Song", thumb: NewThumb()), true, false, false, false);
        Assert.Equal(ThumbnailAction.RevealFirst, result.ThumbnailAction);
    }

    #endregion
}
