using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LyricsBackgroundBrightnessTests
{
    [Theory]
    [InlineData(35, true)]
    [InlineData(90, true)]
    [InlineData(110, true)]
    [InlineData(180, false)]
    public void OnlyDarkArtworkIsBrightenedAndDisablingRestoresOriginal(byte level, bool changes)
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { level, level, level, 128 }, 4);
            source.Freeze();
            var result = (BitmapSource)LyricsBackgroundBrightness.Apply(source, true)!;
            var pixels = new byte[4];
            result.CopyPixels(pixels, 4, 0);
            Assert.Equal(changes, pixels[0] > level);
            if (changes) Assert.True(pixels[0] >= level + 35, "The brightness lift should be visibly stronger on dark artwork.");
            Assert.Equal(128, pixels[3]);
            Assert.Same(result, LyricsBackgroundBrightness.Apply(source, true));
            Assert.Same(source, LyricsBackgroundBrightness.Apply(source, false));
            Assert.Equal(source.PixelWidth, result.PixelWidth);
            Assert.Equal(source.PixelHeight, result.PixelHeight);
        });
    }

    [Fact]
    public void PreferenceSurvivesSettingsCloneAndSerialization()
    {
        Assert.False(new NotchSettings().BrightenDarkLyricsBackground);
        var settings = new NotchSettings { BrightenDarkLyricsBackground = true };
        Assert.True(settings.Clone().BrightenDarkLyricsBackground);
        var json = System.Text.Json.JsonSerializer.Serialize(settings);
        Assert.True(System.Text.Json.JsonSerializer.Deserialize<NotchSettings>(json)!.BrightenDarkLyricsBackground);
    }
}
