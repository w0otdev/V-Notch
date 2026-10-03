using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime.Tensors;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class YoloxCropTests
{
    [Theory]
    [InlineData(8, 0, 52, 10, 20)]
    [InlineData(16, 2704, 26, 8, 10)]
    [InlineData(32, 3380, 13, 5, 4)]
    public void DecodesRawGridCoordinatesAndMultipliesObjectness(int stride, int start, int gridWidth, int x, int y)
    {
        var output = Prediction(start + y * gridWidth + x, .6f, 0, .5f, MathF.Log(64f / stride), MathF.Log(96f / stride));
        var box = Assert.Single(SmartThumbnailCropService.ParseYoloxOutput(output, 832, 468, .5f));
        Assert.Equal((x + .5f) * stride * 2 - 64, box.X1, 3);
        Assert.Equal((y + .5f) * stride * 2 - 96, box.Y1, 3);
        Assert.Equal(128, box.X2 - box.X1, 3);
        Assert.Equal(192, box.Y2 - box.Y1, 3);
        Assert.Equal(.3f, box.Confidence, 3);
        Assert.Equal(0, box.ClassId);
    }

    [Fact]
    public void RejectsLowJointConfidenceEvenWithHighClassProbability()
    {
        var output = Prediction(1050, .11f, 16, .9f, MathF.Log(8), MathF.Log(12));
        Assert.Empty(SmartThumbnailCropService.ParseYoloxOutput(output, 832, 468, .5f));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsNonfiniteBoxes(float width)
    {
        var output = Prediction(1050, .9f, 0, .9f, width, MathF.Log(12));
        Assert.Empty(SmartThumbnailCropService.ParseYoloxOutput(output, 832, 468, .5f));
    }

    [Fact]
    public void RejectsOldTransposedModelAndInvalidScale()
    {
        Assert.Throws<InvalidDataException>(() => SmartThumbnailCropService.ParseYoloxOutput(
            new DenseTensor<float>(new[] { 1, 84, 3549 }), 832, 468, .5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => SmartThumbnailCropService.ParseYoloxOutput(
            new DenseTensor<float>(new[] { 1, 3549, 85 }), 832, 468, 0));
    }

    [Fact]
    public void PreprocessingUsesBgrRawPixelsAndTopLeftPadding()
    {
        SharedStaTestRunner.Run(() =>
        {
            const int width = 416, height = 208, plane = 416 * 416;
            var pixels = new byte[width * height * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            { pixels[i] = 20; pixels[i + 1] = 80; pixels[i + 2] = 160; pixels[i + 3] = 255; }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            using var stream = new MemoryStream();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            encoder.Save(stream);
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
            var tensor = new float[3 * plane];
            Assert.Equal(1f, SmartThumbnailCropService.PreprocessImageFast(image, tensor));
            Assert.Equal(20f, tensor[0]);
            Assert.Equal(80f, tensor[plane]);
            Assert.Equal(160f, tensor[2 * plane]);
            Assert.Equal(20f, tensor[207 * 416 + 415]);
            foreach (int channel in new[] { 0, 1, 2 })
            { Assert.Equal(114f, tensor[channel * plane + 208 * 416]); Assert.Equal(114f, tensor[(channel + 1) * plane - 1]); }
        });
    }

    private static DenseTensor<float> Prediction(int index, float objectness, int classId, float probability, float width, float height)
    {
        var output = new DenseTensor<float>(new[] { 1, 3549, 85 });
        output[0, index, 0] = output[0, index, 1] = .5f;
        output[0, index, 2] = width; output[0, index, 3] = height;
        output[0, index, 4] = objectness; output[0, index, 5 + classId] = probability;
        return output;
    }
}
