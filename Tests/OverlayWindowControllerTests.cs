using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public class OverlayWindowControllerTests
{
    [Fact]
    public void CalculateCenteredBounds_UsesPhysicalPixelsAndScreenOffset()
    {
        var bounds = OverlayWindowController.CalculateCenteredBounds(
            screenLeft: -1920, screenWidth: 1920, widthDip: 500, heightDip: 200, dpiScale: 1.5);

        Assert.Equal(-1335, bounds.X);
        Assert.Equal(750, bounds.Width);
        Assert.Equal(300, bounds.Height);
    }

    [Fact]
    public void CalculateCenteredBounds_CentersTheStartupHostAt150PercentScale()
    {
        var bounds = OverlayWindowController.CalculateCenteredBounds(
            screenLeft: 0, screenWidth: 2560, widthDip: 816, heightDip: 200, dpiScale: 1.5);

        Assert.Equal(668, bounds.X);
        Assert.Equal(1224, bounds.Width);
        Assert.Equal(300, bounds.Height);
    }

    [Fact]
    public void TryApplyDesktopLayerZOrder_RefreshesExplorerAnchorAfterFailure()
    {
        var staleAnchor = new IntPtr(1);
        var refreshedAnchor = new IntPtr(2);
        var attemptedAnchors = new List<IntPtr>();
        int resolveCount = 0;

        bool applied = OverlayWindowController.TryApplyDesktopLayerZOrder(
            getDesktopAnchor: () => ++resolveCount == 1 ? staleAnchor : refreshedAnchor,
            applyZOrder: anchor =>
            {
                attemptedAnchors.Add(anchor);
                return anchor == refreshedAnchor;
            });

        Assert.True(applied);
        Assert.Equal(new[] { staleAnchor, refreshedAnchor }, attemptedAnchors);
        Assert.Equal(2, resolveCount);
    }
}
