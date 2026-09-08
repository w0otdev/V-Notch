using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class CropCacheCollisionTests
{
    [Fact]
    public void TwoImagesWithIdenticalSamplePoints_HaveDifferentFingerprintsAndDoNotCollideInCropCache()
    {
        SharedStaTestRunner.Run(() =>
        {
            int w = 100;
            int h = 60;

            // Generate two 100x60 images where the 25 grid sample points:
            // xs = { 0, 25, 50, 75, 99 }, ys = { 0, 15, 30, 45, 59 } are ALL black (0, 0, 0, 255).
            // But internal regions (e.g. between the grid lines) differ completely.
            var pixelsA = CreateCollidingSampleGridPixels(w, h, fillByte: 50);
            var pixelsB = CreateCollidingSampleGridPixels(w, h, fillByte: 200);

            var sourceA = CreateBitmapImage(w, h, pixelsA);
            var sourceB = CreateBitmapImage(w, h, pixelsB);

            // 1. Verify ArtworkFingerprint differentiates them
            var fpA = ArtworkFingerprint.Create(sourceA);
            var fpB = ArtworkFingerprint.Create(sourceB);

            Assert.NotEqual(fpA, fpB);

            // 2. Verify CropToSquare does not return image A when image B is cropped
            using var service = new MediaArtworkService();
            var croppedA = service.CropToSquare(sourceA, "testMedia", forceCenterCrop: true);
            var croppedB = service.CropToSquare(sourceB, "testMedia", forceCenterCrop: true);

            Assert.NotNull(croppedA);
            Assert.NotNull(croppedB);
            Assert.NotSame(croppedA, croppedB);
        });
    }

    private static byte[] CreateCollidingSampleGridPixels(int w, int h, byte fillByte)
    {
        var pixels = new byte[w * h * 4];

        int[] xs = { 0, w / 4, w / 2, 3 * w / 4, w - 1 };
        int[] ys = { 0, h / 4, h / 2, 3 * h / 4, h - 1 };

        // Fill background with distinct fillByte
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = fillByte;
            pixels[i + 1] = fillByte;
            pixels[i + 2] = fillByte;
            pixels[i + 3] = 255;
        }

        // Overwrite the 25 grid points (or even whole grid lines) with pure black
        foreach (int y in ys)
        {
            foreach (int x in xs)
            {
                int idx = (y * w + x) * 4;
                pixels[idx] = 0;
                pixels[idx + 1] = 0;
                pixels[idx + 2] = 0;
                pixels[idx + 3] = 255;
            }
        }

        return pixels;
    }

    private static BitmapImage CreateBitmapImage(int width, int height, byte[] pixels)
    {
        var bsource = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            width * 4);

        using var ms = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bsource));
        encoder.Save(ms);
        ms.Position = 0;

        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.StreamSource = ms;
        img.EndInit();
        img.Freeze();

        return img;
    }
}
