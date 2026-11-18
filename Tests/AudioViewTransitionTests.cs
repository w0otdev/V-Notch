using Xunit;

namespace VNotch.Tests;

public class AudioViewTransitionTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Snapshot_IsDeferredOnlyDuringAudioViewTransition(
        bool isAudioView, bool isAnimating, bool expected)
    {
        Assert.Equal(expected,
            Controls.AudioMixerView.ShouldDeferAudioSnapshotDuringTransition(isAudioView, isAnimating));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void PrivacyDotIsSuppressedByMixerAndTemporaryOverlays(
        bool audio, bool volume, bool bluetooth, bool clipboard)
    {
        Assert.True(MainWindow.ShouldSuppressPrivacyDot(
            true, audio, volume, bluetooth, clipboard));
    }

    [Fact]
    public void PrivacyDotIsVisibleWhenMixerAndTemporaryOverlaysAreInactive()
    {
        Assert.False(MainWindow.ShouldSuppressPrivacyDot(
            true, false, false, false, false));
    }

    [Fact]
    public void PrivacyDotIsSuppressedOutsideCompactPill()
    {
        Assert.True(MainWindow.ShouldSuppressPrivacyDot(
            false, false, false, false, false));
    }
}
