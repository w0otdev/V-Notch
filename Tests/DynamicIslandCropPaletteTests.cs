using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class DynamicIslandCropPaletteTests
{
    [Fact]
    public void CropPaletteUsesTheSelectedPixelsWithoutChangingTheWholeImageCache()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork();
            var whole = DynamicIslandColorExtractor.GetDynamicIslandPalette(source);
            var red = DynamicIslandColorExtractor.GetDynamicIslandPalette(source, new Rect(0, 0, 40, 40));
            var blue = DynamicIslandColorExtractor.GetDynamicIslandPalette(source, new Rect(40, 0, 40, 40));
            Assert.True(red.Main.R > 200 && red.Main.B < 40);
            Assert.True(blue.Main.B > 200 && blue.Main.R < 40);
            Assert.Equal(whole, DynamicIslandColorExtractor.GetDynamicIslandPalette(source));
            Assert.Equal(blue, DynamicIslandColorExtractor.GetDynamicIslandPalette(source, new Rect(40, 0, 40, 40)));
        });
    }

    [Fact]
    public void CropBoundsAreClampedAndInvalidBoundsFallBackToTheWholeArtwork()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork();
            var blue = DynamicIslandColorExtractor.GetDynamicIslandPalette(source, new Rect(40, -5, 100, 60));
            Assert.True(blue.Main.B > 200 && blue.Main.R < 40);
            var whole = DynamicIslandColorExtractor.GetDynamicIslandPalette(source);
            foreach (var bounds in new[] { Rect.Empty, new Rect(90, 0, 10, 10), new Rect(0, 0, 0, 0), new Rect(double.NaN, 0, 10, 10) })
                Assert.Equal(whole, DynamicIslandColorExtractor.GetDynamicIslandPalette(source, bounds));
        });
    }

    [Fact]
    public void CropPaletteClampsSubpixelNoiseAtImageEdges()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork();
            var blue = DynamicIslandColorExtractor.GetDynamicIslandPalette(source, new Rect(40, 0, 40, 40));
            var noisyBounds = new Rect(40, -1e-16, Math.BitIncrement(40d), Math.BitIncrement(40d));

            Assert.Equal(blue, DynamicIslandColorExtractor.GetDynamicIslandPalette(source, noisyBounds));
            Assert.Equal(new Int32Rect(40, 0, 40, 40),
                DynamicIslandColorExtractor.GetClampedCropRect(noisyBounds, 80, 40));
        });
    }

    [Fact]
    public void CropRectKeepsFractionalEdgeIntersectionsInsideTheBitmap()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork();
            var bounds = new Rect(Math.BitDecrement(80d), Math.BitDecrement(40d), 1, 1);
            var crop = DynamicIslandColorExtractor.GetClampedCropRect(bounds, 80, 40);

            Assert.Equal(new Int32Rect(79, 39, 1, 1), crop);
            var cropped = new CroppedBitmap(source, crop!.Value);
            Assert.Equal(1, cropped.PixelWidth);
            Assert.Equal(1, cropped.PixelHeight);
        });
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(80, 0)]
    [InlineData(-1, 40)]
    public void CropRectRejectsInvalidImageDimensions(int width, int height)
    {
        Assert.Null(DynamicIslandColorExtractor.GetClampedCropRect(new Rect(0, 0, 1, 1), width, height));
    }

    [Fact]
    public void CropRectRejectsNonFiniteAndNonIntersectingBounds()
    {
        foreach (var bounds in new[]
        {
            Rect.Empty,
            new Rect(0, 0, 0, 0),
            new Rect(80, 0, 1, 1),
            new Rect(double.NaN, 0, 10, 10),
            new Rect(0, double.NaN, 10, 10),
            new Rect(0, 0, double.PositiveInfinity, 10),
            new Rect(0, 0, 10, double.PositiveInfinity),
            new Rect(double.MaxValue, 0, double.MaxValue, 10)
        })
            Assert.Null(DynamicIslandColorExtractor.GetClampedCropRect(bounds, 80, 40));
    }

    [Fact]
    public void CropPaletteFreezesAnAccessibleSourceBeforeSharingIt()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork(freeze: false);
            Assert.False(source.IsFrozen);
            var bounds = new Rect(40, 0, 40, 40);
            var palette = DynamicIslandColorExtractor.GetDynamicIslandPalette(source, bounds);

            Assert.True(source.IsFrozen);
            Assert.True(palette.Main.B > 200 && palette.Main.R < 40);
            Assert.Equal(palette, Task.Run(() => DynamicIslandColorExtractor.GetDynamicIslandPalette(source, bounds))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        });
    }

    [Fact]
    public void ForeignMutableSourceReturnsAnUncachedFallbackWithoutBlockingItsOwner()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork(freeze: false);
            var fallback = DynamicIslandColorExtractor.GetDynamicIslandPalette(null!);
            var bounds = new Rect(40, 0, 40, 40);

            // The owner deliberately waits here: a synchronous Dispatcher.Invoke would deadlock.
            Assert.Equal(fallback, Task.Run(() => DynamicIslandColorExtractor.GetDynamicIslandPalette(source, bounds))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Assert.Equal(fallback, Task.Run(() => DynamicIslandColorExtractor.GetDynamicIslandPalette(source))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Assert.False(source.IsFrozen);

            var whole = DynamicIslandColorExtractor.GetDynamicIslandPalette(source);
            Assert.NotEqual(fallback, whole);
            var cropped = DynamicIslandColorExtractor.GetDynamicIslandPalette(source, bounds);
            Assert.True(cropped.Main.B > 200 && cropped.Main.R < 40);
        });
    }

    [Fact]
    public void ForeignMutableSourcePreloadsOnItsOwnerAndThenRunsOnAWorker()
    {
        SharedStaTestRunner.RunAsync(async () =>
        {
            var source = CreateTwoColorArtwork(freeze: false);
            var reference = source.Clone();
            reference.Freeze();
            var expected = DynamicIslandColorExtractor.GetDynamicIslandPalette(reference);

            var result = await Task.Run(() => DynamicIslandColorExtractor.PreloadDynamicIslandPaletteAsync(source));

            Assert.True(source.IsFrozen);
            Assert.Equal(expected, result);
            Assert.Equal(expected, DynamicIslandColorExtractor.GetDynamicIslandPalette(source));
        });
    }

    [Fact]
    public void FrozenSourceCanBeCroppedDirectlyOnAWorker()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork();
            var palette = Task.Run(() => DynamicIslandColorExtractor.GetDynamicIslandPalette(
                source, new Rect(40, 0, 40, 40))).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();

            Assert.True(palette.Main.B > 200 && palette.Main.R < 40);
        });
    }

    [Fact]
    public void NonFreezableSourceIsAnalyzedOnItsOwner()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork(freeze: false);
            BindingOperations.SetBinding(source, FrameworkElement.TagProperty,
                new Binding { Source = "keep this source mutable" });
            Assert.False(source.CanFreeze);

            var reference = source.CloneCurrentValue();
            reference.Freeze();
            var expected = DynamicIslandColorExtractor.GetDynamicIslandPalette(reference);
            var pending = DynamicIslandColorExtractor.PreloadDynamicIslandPaletteAsync(source);

            Assert.True(pending.IsCompletedSuccessfully);
            Assert.Equal(expected, pending.GetAwaiter().GetResult());
            var crop = DynamicIslandColorExtractor.GetDynamicIslandPalette(source, new Rect(40, 0, 40, 40));
            Assert.True(crop.Main.B > 200 && crop.Main.R < 40);
            Assert.False(source.IsFrozen);
        });
    }

    private static BitmapSource CreateTwoColorArtwork(bool freeze = true)
    {
        var pixels = new byte[80 * 40 * 4];
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 80; x++)
            {
                int index = (y * 80 + x) * 4;
                pixels[index + (x < 40 ? 2 : 0)] = 255;
                pixels[index + 3] = 255;
            }
        var source = BitmapSource.Create(80, 40, 96, 96, PixelFormats.Bgra32, null, pixels, 80 * 4);
        if (freeze) source.Freeze();
        return source;
    }
}
