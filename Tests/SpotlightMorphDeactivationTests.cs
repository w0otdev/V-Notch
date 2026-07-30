using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightMorphDeactivationTests
{
    [Fact]
    public void SpotlightSession_BlocksSecondaryAndTimerViewCollapse()
    {
        Assert.False(MainWindow.ShouldCollapseOnDeactivation(
            spotlightMorphSessionActive: true,
            spotlightMorphOwnsNotchVisibility: false,
            isSecondaryView: true,
            isTimerView: false,
            isExpanded: true,
            isMusicExpanded: false,
            isAnimating: false));

        Assert.False(MainWindow.ShouldCollapseOnDeactivation(
            spotlightMorphSessionActive: true,
            spotlightMorphOwnsNotchVisibility: false,
            isSecondaryView: false,
            isTimerView: true,
            isExpanded: true,
            isMusicExpanded: false,
            isAnimating: false));
    }

    [Fact]
    public void SnapshotOwnership_RemainsAFallbackCollapseGuard()
    {
        Assert.False(MainWindow.ShouldCollapseOnDeactivation(
            spotlightMorphSessionActive: false,
            spotlightMorphOwnsNotchVisibility: true,
            isSecondaryView: true,
            isTimerView: false,
            isExpanded: true,
            isMusicExpanded: false,
            isAnimating: false));

        Assert.False(MainWindow.ShouldCollapseOnDeactivation(
            spotlightMorphSessionActive: false,
            spotlightMorphOwnsNotchVisibility: true,
            isSecondaryView: false,
            isTimerView: true,
            isExpanded: true,
            isMusicExpanded: false,
            isAnimating: false));
    }

    [Theory]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, true)]
    public void ViewDeactivation_StillCollapsesWithoutSpotlight(
        bool isSecondaryView,
        bool isTimerView,
        bool isExpanded,
        bool isMusicExpanded)
    {
        Assert.True(MainWindow.ShouldCollapseOnDeactivation(
            spotlightMorphSessionActive: false,
            spotlightMorphOwnsNotchVisibility: false,
            isSecondaryView,
            isTimerView,
            isExpanded,
            isMusicExpanded,
            isAnimating: false));
    }

    [Fact]
    public void PrimaryOrAnimatingView_DoesNotCollapseFromThisPolicy()
    {
        Assert.False(MainWindow.ShouldCollapseOnDeactivation(
            spotlightMorphSessionActive: false,
            spotlightMorphOwnsNotchVisibility: false,
            isSecondaryView: false,
            isTimerView: false,
            isExpanded: true,
            isMusicExpanded: false,
            isAnimating: false));

        Assert.False(MainWindow.ShouldCollapseOnDeactivation(
            spotlightMorphSessionActive: false,
            spotlightMorphOwnsNotchVisibility: false,
            isSecondaryView: true,
            isTimerView: false,
            isExpanded: true,
            isMusicExpanded: false,
            isAnimating: true));
    }
}
