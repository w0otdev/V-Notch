using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class DynamicIslandCropPaletteTests
{
    [Fact]
    public void ForeignMutableSourceReturnsAnUncachedFallbackWithoutBlockingItsOwner()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork(freeze: false);
            var fallback = DynamicIslandColorExtractor.GetDynamicIslandPalette(null!);

            // The owner deliberately waits here: a synchronous Dispatcher.Invoke would deadlock.
            Assert.Equal(fallback, Task.Run(() => DynamicIslandColorExtractor.GetDynamicIslandPalette(source))
                .WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
            Assert.False(source.IsFrozen);

            var whole = DynamicIslandColorExtractor.GetDynamicIslandPalette(source);
            Assert.NotEqual(fallback, whole);
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
            Assert.False(source.IsFrozen);
        });
    }

    [Fact]
    public void DeepBlueIndigoArtworkAvoidsHarshNeonIndigoSubColorAndPrefersNonBluePrimaryWhenPresent()
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = CreateTwoColorArtwork();
            var whole = DynamicIslandColorExtractor.GetDynamicIslandPalette(source);
            Assert.True(whole.Main.R > 200 && whole.Main.B < 40);

            var pureBlue = DynamicIslandColorExtractor.GetDynamicIslandPalette(new CroppedBitmap(source, new Int32Rect(40, 0, 40, 40)));
            Assert.True(pureBlue.Main.B > 200 && pureBlue.Main.R < 40);
            Assert.True(pureBlue.Sub.B >= 220 && pureBlue.Sub.B > pureBlue.Sub.G && pureBlue.Sub.G > pureBlue.Sub.R && pureBlue.Sub.G >= 100, $"Expected vibrant readable blue sub color instead of washed-out gray or dark indigo, got ({pureBlue.Sub.R},{pureBlue.Sub.G},{pureBlue.Sub.B})");
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
