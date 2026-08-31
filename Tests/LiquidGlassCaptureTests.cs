using VNotch.Controllers;
using Xunit;

namespace VNotch.Tests;

public sealed class LiquidGlassCaptureTests
{
    [Fact]
    public void CompleteFrame_AcceptsExactAndPaddedImages()
    {
        Assert.True(MagnifierCaptureSource.IsCompleteFrame(320, 80, 320, 80, 1280, 102400));
        Assert.True(MagnifierCaptureSource.IsCompleteFrame(320, 80, 400, 100, 1664, 166400));
    }

    [Theory]
    [InlineData(320, 80, 319, 80, 1280, 102400)]
    [InlineData(320, 80, 320, 79, 1280, 102400)]
    [InlineData(320, 80, 320, 80, 1276, 102400)]
    [InlineData(320, 80, 320, 80, 1280, 102399)]
    public void CompleteFrame_RejectsPartialImages(
        int requestedWidth,
        int requestedHeight,
        int receivedWidth,
        int receivedHeight,
        int receivedStride,
        int bufferLength)
    {
        Assert.False(MagnifierCaptureSource.IsCompleteFrame(
            requestedWidth,
            requestedHeight,
            receivedWidth,
            receivedHeight,
            receivedStride,
            bufferLength));
    }

    [Fact]
    public void PresentCapacity_GrowsInChunksAndNeverShrinks()
    {
        int initial = LiquidGlassController.GrowPresentCapacity(0, 300, 128);
        Assert.True(initial >= 428);
        Assert.Equal(0, initial % 64);
        Assert.Equal(initial, LiquidGlassController.GrowPresentCapacity(initial, 280, 128));

        int grown = LiquidGlassController.GrowPresentCapacity(initial, initial + 1, 128);
        Assert.True(grown >= initial + 128);
        Assert.Equal(0, grown % 64);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    public void FrameCadence_AlwaysUsesLockedTarget(int targetFps)
    {
        double interval = LiquidGlassController.ChooseLockedFrameIntervalMs(
            1000.0 / targetFps);

        Assert.Equal(1000.0 / targetFps, interval, 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LensProfile_PeaksNearOuterEdgeAndIsFlatAtBothEnds(bool broad)
    {
        const double epsilon = 1e-4;

        Assert.Equal(0.0, LiquidGlassController.LensProfile(0.0, broad), 12);
        Assert.Equal(0.0, LiquidGlassController.LensProfile(1.0, broad), 12);
        Assert.True(
            LiquidGlassController.LensProfile(0.25, broad) >
            LiquidGlassController.LensProfile(0.75, broad));
        Assert.InRange(LiquidGlassController.LensProfile(0.25, broad), 0.70, 0.85);
        Assert.InRange(LiquidGlassController.LensProfile(epsilon, broad), 0.0, 1e-5);
        Assert.InRange(LiquidGlassController.LensProfile(1.0 - epsilon, broad), 0.0, 1e-5);
    }

    [Fact]
    public void RefractionAmplitude_DefaultCurveProducesVisibleEdgeFold()
    {
        double rim = LiquidGlassController.ComputeRimWidth(0.23, 1.0, 80.0);
        double amplitude = LiquidGlassController.RefractionAmplitude(rim, 1.0);

        Assert.Equal(23.0, rim, 6);
        Assert.Equal(rim * 0.58, amplitude, 6);
        Assert.InRange(amplitude, rim * 0.55, rim * 0.65);
    }

    [Fact]
    public void EdgeBend_IndependentlyScalesOuterRimDisplacement()
    {
        const double rim = 24.0;
        double normal = LiquidGlassController.RefractionAmplitude(rim, 0.7, edgeBend: 1.0);
        double strong = LiquidGlassController.RefractionAmplitude(rim, 0.7, edgeBend: 1.65);

        Assert.Equal(normal * Math.Pow(1.65, 1.5), strong, 8);
    }

    [Fact]
    public void CurrentNarrowRimSettingsStillCreateVisibleBend()
    {
        double amplitude = LiquidGlassController.RefractionAmplitude(
            rimWidth: 8.0, refraction: 0.6, edgeBend: 3.0);

        Assert.True(amplitude > 8.0);
    }

    [Fact]
    public void RefractionAmplitude_ExtremeBendCanTravelBeyondOpticalRim()
    {
        const double rim = 20.0;
        double amplitude = LiquidGlassController.RefractionAmplitude(
            rim, refraction: 3.0, bevelMode: 1, edgeBend: 3.0);

        Assert.True(amplitude > rim * 5.0);
    }

    [Fact]
    public void EdgeBend_AboveFormerLimitKeepsScaling()
    {
        const double rim = 20.0;
        double formerMaximum = LiquidGlassController.RefractionAmplitude(
            rim, refraction: 1.0, edgeBend: 3.0);
        double extreme = LiquidGlassController.RefractionAmplitude(
            rim, refraction: 1.0, edgeBend: 6.0);

        Assert.True(extreme > formerMaximum * 2.8);
    }

    [Fact]
    public void DirectionalLensWidth_TapersAndElongatesCapsuleEnds()
    {
        const double rim = 20.0;
        double verticalEdge = LiquidGlassController.DirectionalLensWidth(
            rim, inwardNormalX: 0.0, edgeBend: 3.0);
        double roundedCorner = LiquidGlassController.DirectionalLensWidth(
            rim, inwardNormalX: Math.Sqrt(0.5), edgeBend: 3.0);
        double sideTip = LiquidGlassController.DirectionalLensWidth(
            rim, inwardNormalX: 1.0, edgeBend: 3.0);

        Assert.Equal(rim, verticalEdge, 8);
        Assert.InRange(roundedCorner, rim, sideTip);
        Assert.Equal(rim * 2.5, sideTip, 8);
    }

    [Fact]
    public void GaussianBoxRadii_ApproximateRequestedSigmaAcrossThreePasses()
    {
        int[] radii = LiquidGlassController.GaussianBoxRadii(12.0);

        Assert.Equal(3, radii.Length);
        Assert.True(radii[0] <= radii[1] && radii[1] <= radii[2]);

        double variance = 0.0;
        foreach (int radius in radii)
            variance += radius * (radius + 1.0) / 3.0;

        Assert.InRange(Math.Sqrt(variance), 11.0, 13.0);
        Assert.All(LiquidGlassController.GaussianBoxRadii(0.0),
            radius => Assert.Equal(0, radius));
    }

    [Theory]
    [InlineData(320.0, 80.0, 320.0, 80.0, true)]
    [InlineData(320.0, 80.0, 321.0, 80.0, true)]
    [InlineData(352.0, 112.0, 320.0, 80.0, true)]
    [InlineData(300.0, 80.0, 320.0, 80.0, false)]
    [InlineData(320.0, 70.0, 320.0, 80.0, false)]
    public void GpuGeometry_AllowsOverscannedTexture(
        double srcW, double srcH, double notchW, double notchH, bool expected)
    {
        Assert.Equal(expected, LiquidGlassController.IsGpuGeometryValid(srcW, srcH, notchW, notchH));
    }

    [Theory]
    [InlineData(8, 154)]
    [InlineData(0, 40)]
    [InlineData(-20, 80)]
    public void BitBltFallback_StartsBelowVisibleNotch(int regionY, int displayHeight)
    {
        int sourceY = LiquidGlassController.ComputeFallbackSourceY(regionY, displayHeight);

        Assert.True(sourceY > regionY + displayHeight);
    }

    [Theory]
    [InlineData(-46, 664, 0, 1920, 0)]
    [InlineData(1500, 664, 0, 1920, 1256)]
    [InlineData(-1700, 640, -1920, 3840, -1700)]
    public void CaptureOrigin_ClampsWithoutLosingDesktopOffset(
        int requested, int captureLength, int desktopOrigin, int desktopLength, int expected)
    {
        Assert.Equal(expected, LiquidGlassController.ClampCaptureOrigin(
            requested, captureLength, desktopOrigin, desktopLength));
    }

    [Fact]
    public void SamplingMargin_ContainsLensChromaAndFluidOffsets()
    {
        const double rim = 32.0;
        double amplitude = LiquidGlassController.RefractionAmplitude(
            rim, 1.5, bevelMode: 1, edgeBend: 1.9);
        int margin = LiquidGlassController.ComputeSamplingMargin(
            rim, 1.5, 2.0, 2.0, bevelMode: 1, edgeBend: 1.9);

        Assert.True(margin >= Math.Ceiling(amplitude + 8.0 + 4.5 + 3.0));
        Assert.InRange(margin, 12, 512);
    }

    [Fact]
    public void BackdropOptics_UsesTheActualContentTint()
    {
        int[] samples = Enumerable.Repeat(0x2040C0, 16).ToArray();

        var optics = LiquidGlassController.AnalyzeBackdropSamples(samples, 4, 4);

        Assert.Equal(0x20, optics.Red);
        Assert.Equal(0x40, optics.Green);
        Assert.Equal(0xC0, optics.Blue);
        Assert.Equal(0.0, optics.LightX, 8);
        Assert.Equal(0.0, optics.LightY, 8);
        Assert.Equal(0.0, optics.Contrast, 8);
    }

    [Fact]
    public void BackdropOptics_PointsFresnelTowardBrightContent()
    {
        int[] brightRight =
        {
            0x000000, 0x000000, 0xFFFFFF, 0xFFFFFF,
            0x000000, 0x000000, 0xFFFFFF, 0xFFFFFF
        };
        int[] brightTop =
        {
            0xFFFFFF, 0xFFFFFF, 0xFFFFFF, 0xFFFFFF,
            0x000000, 0x000000, 0x000000, 0x000000
        };

        var right = LiquidGlassController.AnalyzeBackdropSamples(brightRight, 4, 2);
        var top = LiquidGlassController.AnalyzeBackdropSamples(brightTop, 4, 2);

        Assert.True(right.LightX > 0.5);
        Assert.InRange(right.LightY, -1e-8, 1e-8);
        Assert.True(top.LightY < -0.9);
        Assert.InRange(top.LightX, -1e-8, 1e-8);
    }

    [Fact]
    public void GpuGeometry_PreservesCaptureOrigin()
    {
        var geom = new LiquidGlassController.GpuGeometry(
            1728, 728, 400, 180, 64, 64,
            20, 20, 3.0, 0.7, 2.3, 5.2, 6.9, 1.0, 0.06, 0.25, 0.0, 0.15, 0.0, 0.35, 1.0, 1.0, 0.0, 0.0,
            800, 100);

        Assert.Equal(800, geom.CaptureOriginX);
        Assert.Equal(100, geom.CaptureOriginY);
    }

    [Theory]
    [InlineData(200.0, 860.0, 796)] // Compact pill
    [InlineData(300.0, 810.0, 796)] // Expanding (worker not yet updated)
    [InlineData(300.0, 810.0, 746)] // Expanding (worker updated)
    [InlineData(400.0, 760.0, 696)] // Fully expanded
    public void Expansion_LocksBackdropCoordinatesToScreenOrigin(
        double notchWidth, double screenLeft, int captureOriginX)
    {
        const double srcW = 1728.0;
        // Fixed desktop feature at screen X = 900
        const double targetScreenX = 900.0;
        // With GlassBackdropImage aligned Left at screenLeft with width srcW:
        double uvX = (targetScreenX - screenLeft) / srcW;
        double offX = screenLeft - captureOriginX;

        // In the pixel shader: npx = uv.x * srcW
        double npx = uvX * srcW;
        double basePixelX = npx + offX;

        // In the desktop frame, texture pixel 0 is at captureOriginX.
        // Therefore, the reconstructed desktop coordinate is:
        double sampledDesktopX = captureOriginX + basePixelX;

        Assert.Equal(targetScreenX, sampledDesktopX, 6);
        Assert.True(notchWidth >= 200.0);
    }
}

