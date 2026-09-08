using System;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public class PipDetectorTests
{
    [Theory]
    [InlineData("Picture in picture")]
    [InlineData("Picture-in-picture")]
    [InlineData("Picture-in-Picture")]
    [InlineData("video.mp4 - Picture in picture")]
    [InlineData("Never Gonna Give You Up - Picture-in-Picture")]
    [InlineData("Hình trong hình")]
    [InlineData("hinh trong hinh")]
    [InlineData("Video đang phát - Hình trong hình")]
    [InlineData("Bild-in-Bild")]
    [InlineData("Image dans l'image")]
    [InlineData("Pantalla en pantalla")]
    [InlineData("Cuadro en cuadro")]
    [InlineData("画中画")]
    [InlineData("畫中畫")]
    [InlineData("子母画面")]
    [InlineData("子母畫面")]
    [InlineData("ピクチャー イン ピクチャー")]
    [InlineData("ピクチャーインピクチャー")]
    [InlineData("картинка в картинке")]
    [InlineData("imagem na imagem")]
    [InlineData("finestra mobile")]
    [InlineData("gambar dalam gambar")]
    [InlineData("resim içinde resim")]
    [InlineData("화면 속 화면")]
    [InlineData("Video (PiP)")]
    [InlineData("[PiP] Stream")]
    [InlineData("PiP")]
    [InlineData("pip")]
    public void IsPipTitle_ValidPipTitles_ReturnsTrue(string title)
    {
        Assert.True(PipDetector.IsPipTitle(title));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("YouTube - Never Gonna Give You Up")]
    [InlineData("Spotify Free")]
    [InlineData("Visual Studio Code")]
    [InlineData("Inbox - Gmail")]
    [InlineData("Pipeline Management")]
    [InlineData("Piped Music Player")]
    [InlineData("Piper at the Gates of Dawn")]
    [InlineData("Pipe Dream")]
    [InlineData("Epiphanic Moments")]
    public void IsPipTitle_NonPipTitles_ReturnsFalse(string? title)
    {
        Assert.False(PipDetector.IsPipTitle(title));
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("msedge")]
    [InlineData("msedge.exe")]
    [InlineData("firefox")]
    [InlineData("firefox.exe")]
    [InlineData("brave")]
    [InlineData("opera")]
    [InlineData("vivaldi")]
    [InlineData("coccoc")]
    [InlineData("arc")]
    [InlineData("zen")]
    [InlineData("thorium")]
    public void IsKnownBrowserProcess_ValidBrowsers_ReturnsTrue(string proc)
    {
        Assert.True(PipDetector.IsKnownBrowserProcess(proc));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("spotify")]
    [InlineData("notepad")]
    [InlineData("explorer")]
    [InlineData("devenv")]
    [InlineData("cmd")]
    public void IsKnownBrowserProcess_NonBrowsers_ReturnsFalse(string? proc)
    {
        Assert.False(PipDetector.IsKnownBrowserProcess(proc));
    }

    [Fact]
    public void MediaInfo_IsPictureInPicture_DefaultsToFalse()
    {
        var info = new MediaInfo();
        Assert.False(info.IsPictureInPicture);
        Assert.False(info.IsPip);
    }

    [Fact]
    public void MediaInfo_SettingIsPictureInPicture_UpdatesIsPipAndIsVideoSource()
    {
        var info = new MediaInfo
        {
            MediaSource = "Unknown",
            IsPictureInPicture = true
        };

        Assert.True(info.IsPictureInPicture);
        Assert.True(info.IsPip);
        Assert.True(info.IsVideoSource);
    }

    [Fact]
    public void MediaInfo_Clone_PreservesIsPictureInPicture()
    {
        var info = new MediaInfo
        {
            CurrentTrack = "Rick Astley",
            IsPictureInPicture = true
        };

        var clone = info.Clone();
        Assert.True(clone.IsPictureInPicture);
        Assert.True(clone.IsPip);
    }
}
