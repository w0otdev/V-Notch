using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ArtworkFrameDetectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoesNotTreatBrightRoundSubjectOrHorizonAsAnInsetCover(bool horizon)
    {
        SharedStaTestRunner.Run(() =>
        {
            const int width = 384, height = 216;
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    bool bright = horizon ? y > 45 : Math.Pow(x - 192, 2) + Math.Pow(y - 108, 2) < 85 * 85;
                    int i = (y * width + x) * 4;
                    byte value = bright ? (byte)160 : (byte)(15 + (x + y) % 30);
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                    pixels[i + 3] = 255;
                }
            var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            source.Freeze();
            Assert.Null(ArtworkFrameDetector.DetectFrame(source));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(320)]
    public void TexturedBackgroundIsExcludedFromReportedAlbumCover(int decodeWidth)
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "dark-textured-artwork.png"));
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.DecodePixelWidth = decodeWidth;
            source.EndInit();
            source.Freeze();
            var bounds = ArtworkFrameDetector.DetectFrame(source);
            Assert.True(bounds.HasValue);
            Assert.InRange(bounds.Value.X / (double)source.PixelWidth, .283, .30);
            Assert.InRange(bounds.Value.Y / (double)source.PixelHeight, .122, .15);
            Assert.InRange((bounds.Value.X + bounds.Value.Width) / (double)source.PixelWidth, .69, .707);
            Assert.InRange((bounds.Value.Y + bounds.Value.Height) / (double)source.PixelHeight, .92, .948);
            using var service = new MediaArtworkService();
            var cropped = service.CropToSquare(source, "YouTube");
            Assert.NotNull(cropped);
            Assert.Equal(cropped.PixelWidth, cropped.PixelHeight);
            Assert.InRange(cropped.PixelWidth / (double)source.PixelWidth, .39, .425);
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1024)]
    [InlineData(320)]
    public void ReportedArtworkCropsInsideForestWithoutWhiteFringe(int decodeWidth)
    {
        SharedStaTestRunner.Run(() =>
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "white-mat-artwork.png"));
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.DecodePixelWidth = decodeWidth;
            source.EndInit();
            source.Freeze();
            var bounds = ArtworkFrameDetector.DetectWhiteFrame(source);
            Assert.True(bounds.HasValue);
            Assert.InRange(bounds.Value.X / (double)source.PixelWidth, .26, .28);
            Assert.InRange(bounds.Value.Y / (double)source.PixelHeight, .077, .1);
            using var service = new MediaArtworkService();
            var cropped = service.CropToSquare(source, "YouTube");
            Assert.NotNull(cropped);
            Assert.Equal(cropped.PixelWidth, cropped.PixelHeight);
            var converted = new FormatConvertedBitmap(cropped, PixelFormats.Bgra32, null, 0);
            int side = cropped.PixelWidth;
            var pixels = new byte[side * side * 4];
            converted.CopyPixels(pixels, side * 4, 0);
            for (int p = 0; p < side; p++)
                foreach (int i in new[] { p * 4, ((side - 1) * side + p) * 4, p * side * 4, (p * side + side - 1) * 4 })
                    Assert.False(pixels[i] >= 225 && pixels[i + 1] >= 225 && pixels[i + 2] >= 225,
                        "A white frame pixel survived on the cropped artwork edge.");
        });
    }

    [Theory]
    [InlineData(640, 360)]
    [InlineData(320, 180)]
    [InlineData(360, 360)]
    public void RemovesDecoratedMatWithoutFollowingWhiteSubject(int width, int height)
    {
        SharedStaTestRunner.Run(() =>
        {
            var picture = new Int32Rect(width / 4, height / 12, width / 2, height * 5 / 6);
            var source = CreateImage(width, height, picture);
            var result = ArtworkFrameDetector.DetectWhiteFrame(source);
            Assert.True(result.HasValue);
            var crop = result.Value;
            Assert.InRange(crop.X, picture.X, picture.X + 8);
            Assert.InRange(crop.Y, picture.Y, picture.Y + 8);
            Assert.True(crop.X + crop.Width <= picture.X + picture.Width);
            Assert.True(crop.Y + crop.Height <= picture.Y + picture.Height);
            Assert.True(crop.Width >= picture.Width - 16);
            Assert.True(crop.Height >= picture.Height - 16);
        });
    }

    [Theory]
    [InlineData(0, 0, 640, 360)]
    [InlineData(160, 0, 320, 360)]
    [InlineData(0, 30, 640, 300)]
    [InlineData(0, 0, 0, 0)]
    public void LeavesUnframedAndPlainWhiteImagesAlone(int x, int y, int w, int h)
    {
        SharedStaTestRunner.Run(() =>
            Assert.Null(ArtworkFrameDetector.DetectFrame(CreateImage(640, 360, new Int32Rect(x, y, w, h)))));
    }

    private static BitmapSource CreateImage(int width, int height, Int32Rect picture)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                bool inside = x >= picture.X && x < picture.X + picture.Width &&
                    y >= picture.Y && y < picture.Y + picture.Height;
                bool subject = x > width * .36 && x < width * .64 && y > height * .2 && y < height * .85;
                byte value = inside && !subject ? (byte)60 : (byte)250;
                if (y == 0 || x == 0) value = 0;
                else if (!inside && (x < 6 || y < 6 || x >= width - 6 || y >= height - 6) && (x + y) % 9 == 0)
                    value = 190;
                pixels[i] = value;
                pixels[i + 1] = inside && !subject ? (byte)120 : value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }
}
