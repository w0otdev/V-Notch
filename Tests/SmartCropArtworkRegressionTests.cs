using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;
using Detection = VNotch.Services.SmartThumbnailCropService.Detection;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SmartCropArtworkRegressionTests
{
    [Theory]
    [InlineData(512, 288)]
    [InlineData(320, 180)]
    [InlineData(384, 288)]
    [InlineData(240, 400)]
    public void ArtworkCropKeepsTheDetectedPersonInsteadOfTheTitle(int width, int height) => SharedStaTestRunner.Run(() =>
    {
        var source = CreateArtwork(width, height);
        var person = new Detection
        {
            X1 = width * .64f, Y1 = height * .06f,
            X2 = width * .92f, Y2 = height * .96f,
            Confidence = .95f, ClassId = 0
        };
        using var artwork = new MediaArtworkService { EnableSmartCrop = true };
        var smartCrop = (SmartThumbnailCropService)typeof(MediaArtworkService)
            .GetField("_smartCrop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(artwork)!;
        SeedDetection(smartCrop, source, person);

        var actual = artwork.CropToSquare(source, "YouTube");
        Assert.NotNull(actual);
        int side = Math.Min(width, height);
        var expectedRect = SmartThumbnailCropService.GetSinglePersonCropRect(person, width, height, side);
        var expected = new CroppedBitmap(source, expectedRect);
        Assert.Equal(Pixels(expected), Pixels(actual));
    });

    [Theory]
    [InlineData(512, 288)]
    [InlineData(320, 180)]
    public void ExplicitObjectDetectionWorksAtDecodedThumbnailSizes(int width, int height) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var source = CreateArtwork(width, height);
        var person = new Detection
        {
            X1 = width * .64f, Y1 = height * .06f,
            X2 = width * .92f, Y2 = height * .96f,
            Confidence = .95f, ClassId = 0
        };
        using var service = new SmartThumbnailCropService();
        SeedDetection(service, source, person);
        var crop = await service.GetSmartCropRectAsync(source, height, true, ct);
        Assert.True(crop.HasValue);
        Assert.True(crop.Value.X <= person.X1);
        Assert.True(crop.Value.X + crop.Value.Width >= person.X2,
            $"Crop {crop} cuts the person at x={person.X2}.");
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DisabledSmartCropAndForcedCenterCropKeepTheCenter(bool smartEnabled, bool forceCenter) => SharedStaTestRunner.Run(() =>
    {
        var source = CreateArtwork(512, 288);
        using var artwork = new MediaArtworkService { EnableSmartCrop = smartEnabled };
        var actual = artwork.CropToSquare(source, "YouTube", forceCenter);
        Assert.NotNull(actual);
        Assert.Equal(Pixels(new CroppedBitmap(source, new Int32Rect(116, 4, 279, 279))), Pixels(actual));
    });

    [Fact]
    public void MissingModelFallsBackToSaliency() => SharedStaTestRunner.Run(() =>
    {
        var source = CreateArtwork(320, 180);
        using var service = new SmartThumbnailCropService();
        typeof(SmartThumbnailCropService).GetField("_modelExistsChecked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, true);
        var expected = service.GetSmartCropRect(source, 180);
        Assert.NotNull(expected);
        Assert.Equal(expected, service.GetSmartCropRect(source, 180, useObjectDetection: true));
        Assert.False(service.IsLoaded);
    });

    private static void SeedDetection(SmartThumbnailCropService service, BitmapSource source, Detection person) =>
        typeof(SmartThumbnailCropService).GetMethod("AddInferenceCacheEntryLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [ArtworkFingerprint.Create(source), new[] { person }]);

    private static byte[] Pixels(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        byte[] pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static BitmapSource CreateArtwork(int width, int height)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                bool title = x < width * .5 && y > height * .6;
                byte detail = title ? (byte)(((x / 3 + y / 3) % 2) * 140) : (byte)0;
                pixels[index] = (byte)(70 + x * 80 / width + detail / 4);
                pixels[index + 1] = (byte)(40 + y * 60 / height + detail);
                pixels[index + 2] = (byte)(90 + x * 50 / width + detail / 3);
                pixels[index + 3] = 255;
            }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }
}
