using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class LightweightThumbnailCropTests
{
    [Theory]
    [InlineData(300, false)]
    [InlineData(40, true)]
    public void ThumbnailCroppingUsesSaliencyWithoutInitializingTheModel(int targetSize, bool requestObjectDetection)
    {
        SharedStaTestRunner.Run(() =>
        {
            using var service = new SmartThumbnailCropService();
            var source = CreateWideArtwork();
            var crop = service.GetSmartCropRect(source, targetSize, requestObjectDetection);
            Assert.True(crop.HasValue);
            Assert.Equal(targetSize, crop.Value.Width);
            Assert.Equal(targetSize, crop.Value.Height);
            Assert.InRange(crop.Value.X, 0, source.PixelWidth - targetSize);
            Assert.InRange(crop.Value.Y, 0, source.PixelHeight - targetSize);
            Assert.False(service.IsLoaded);
            Assert.Equal(crop, service.GetSmartCropRect(source, targetSize, requestObjectDetection));
        });
    }

    [Fact]
    public void ArtworkServiceCanUseSmartThumbnailCroppingWithoutAModel()
    {
        SharedStaTestRunner.Run(() =>
        {
            using var service = new MediaArtworkService { EnableSmartCrop = true };
            var crop = service.CropToSquare(CreateWideArtwork(), "test");
            Assert.NotNull(crop);
            Assert.Equal(crop.PixelWidth, crop.PixelHeight);
            Assert.IsType<CroppedBitmap>(crop);
            Assert.True(crop.IsFrozen);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CroppedSourcesPreservePixelsAndCanBeReadAcrossThreads(bool smartCrop)
    {
        SharedStaTestRunner.Run(() =>
        {
            using var service = new MediaArtworkService { EnableSmartCrop = smartCrop };
            var source = CreateWideArtwork();
            var crop = Assert.IsType<CroppedBitmap>(service.CropToSquare(source, "pixel-test", forceCenterCrop: !smartCrop));
            Assert.True(crop.IsFrozen);
            Assert.Same(crop, service.CropToSquare(source, "pixel-test", forceCenterCrop: !smartCrop));
            int stride = (crop.PixelWidth * crop.Format.BitsPerPixel + 7) / 8;
            var expected = new byte[stride * crop.PixelHeight];
            crop.Source.CopyPixels(crop.SourceRect, expected, stride, 0);
            var actual = Task.Run(() =>
            {
                var pixels = new byte[expected.Length];
                crop.CopyPixels(pixels, stride, 0);
                return pixels;
            }).GetAwaiter().GetResult();
            Assert.Equal(expected, actual);
            Assert.NotNull(service.CropToSquare(crop, "already-cropped"));
        });
    }

    private static BitmapImage CreateWideArtwork()
    {
        const int width = 640, height = 320;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                pixels[index] = 60;
                pixels[index + 1] = 60;
                pixels[index + 2] = x is > 60 and < 220 ? (byte)220 : (byte)60;
                pixels[index + 3] = 255;
            }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
