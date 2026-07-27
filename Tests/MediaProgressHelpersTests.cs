using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class MediaProgressHelpersTests
{
    [Fact]
    public void LogicalSessionChanged_SameChromeAppNewInstance_ReturnsTrue()
    {
        Assert.True(MediaProgressHelpers.HasLogicalSessionChanged(
            lastSourceAppId: "chrome.exe",
            lastSessionInstanceKey: "chrome.exe|111",
            currentSourceAppId: "chrome.exe",
            currentSessionInstanceKey: "chrome.exe|222"));
    }

    [Fact]
    public void LogicalSessionChanged_SameInstance_ReturnsFalse()
    {
        Assert.False(MediaProgressHelpers.HasLogicalSessionChanged(
            lastSourceAppId: "chrome.exe",
            lastSessionInstanceKey: "chrome.exe|111",
            currentSourceAppId: "chrome.exe",
            currentSessionInstanceKey: "chrome.exe|111"));
    }

    [Fact]
    public void LogicalSessionChanged_MissingInstanceKeys_FallsBackToApp()
    {
        Assert.True(MediaProgressHelpers.HasLogicalSessionChanged(
            lastSourceAppId: "chrome.exe",
            lastSessionInstanceKey: "",
            currentSourceAppId: "Spotify.exe",
            currentSessionInstanceKey: ""));
    }

    [Fact]
    public void LogicalSessionChanged_NoPreviousIdentity_IsNotAChange()
    {
        Assert.False(MediaProgressHelpers.HasLogicalSessionChanged(
            lastSourceAppId: "",
            lastSessionInstanceKey: "",
            currentSourceAppId: "chrome.exe",
            currentSessionInstanceKey: "chrome.exe|111"));
    }

    [Fact]
    public void Clone_ThumbnailOnlyFlagDoesNotMutateOriginalMediaInfo()
    {
        var original = new MediaInfo
        {
            CurrentTrack = "Real video",
            SessionInstanceKey = "chrome.exe|111",
            IsThumbnailOnlyUpdate = false,
        };

        var update = original.Clone();
        update.IsThumbnailOnlyUpdate = true;

        Assert.False(original.IsThumbnailOnlyUpdate);
        Assert.True(update.IsThumbnailOnlyUpdate);
        Assert.Equal(original.CurrentTrack, update.CurrentTrack);
        Assert.Equal(original.SessionInstanceKey, update.SessionInstanceKey);
    }
}
