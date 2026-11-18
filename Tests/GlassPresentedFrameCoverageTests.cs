using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public sealed class GlassPresentedFrameCoverageTests
{
    [Theory]
    [InlineData(-54, 600, 728, 0, 1, -54)]
    [InlineData(-154, 700, 728, 0, 1, -28)]
    [InlineData(26, 600, 728, 0, 1, 0)]
    [InlineData(5, 600, 600, 0, 1, 0)]
    [InlineData(0, 1150, 729, 0, 1150d / 729, 0)]
    [InlineData(0, 334, 313, 0, 334d / 313, 0)]
    [InlineData(0, 1150, 300, 25, 4.6, -115)]
    public void OpaqueSpanCoversTheLiveLens(double offset, double live, double rendered, double inset,
        double expectedScale, double expectedOffset)
    {
        var fit = LiquidGlassController.FitPresentedSpan(offset, live, rendered, inset);
        Assert.Equal(expectedScale, fit.Scale, 9);
        Assert.Equal(expectedOffset, fit.Offset, 9);
        Assert.True(fit.Offset + inset * fit.Scale <= 1e-8);
        Assert.True(fit.Offset + (rendered - inset) * fit.Scale >= live - 1e-8);
    }
}
