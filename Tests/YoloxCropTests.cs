using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;
using Detection = VNotch.Services.SmartThumbnailCropService.Detection;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed partial class YoloxCropTests
{
    private const int PredictionCount = 3549;
    private const int RowStride = 85;
    private const int TotalOutputFloats = PredictionCount * RowStride;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_smartcrop_preprocess_bgra32(
        ReadOnlySpan<byte> pixels, int length, int width, int height, int stride,
        Span<float> tensor, int capacity, out float scale);

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_smartcrop_parse_yolox_and_nms(
        ReadOnlySpan<float> buffer, int imgWidth, int imgHeight, float scale,
        Detection* outDetections, int outCap,
        out int outRawCount, out int outNmsCount);

    [Theory]
    [InlineData(8, 0, 52, 10, 20)]
    [InlineData(16, 2704, 26, 8, 10)]
    [InlineData(32, 3380, 13, 5, 4)]
    public void DecodesRawGridCoordinatesAndMultipliesObjectness(int stride, int start, int gridWidth, int x, int y)
    {
        float[] output = Prediction(start + y * gridWidth + x, .6f, 0, .5f, MathF.Log(64f / stride), MathF.Log(96f / stride));
        var box = Assert.Single(ParseYolox(output, 832, 468, .5f));
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
        float[] output = Prediction(1050, .11f, 16, .9f, MathF.Log(8), MathF.Log(12));
        Assert.Empty(ParseYolox(output, 832, 468, .5f));
    }

    [Fact]
    public void ClassReductionRejectsInvalidScoresAndKeepsTheEarliestClassOnTies()
    {
        float[] output = Prediction(1050, .9f, 0, .9f, MathF.Log(8), MathF.Log(12));
        var row = output.AsSpan(1050 * RowStride, RowStride);
        row[5 + 79] = .9f;
        row[5 + 8] = float.NaN;
        row[5 + 32] = 1.1f;
        row[5 + 63] = float.PositiveInfinity;
        Assert.Equal(0, Assert.Single(ParseYolox(output, 832, 468, .5f)).ClassId);
        row[5 + 79] = .99f;
        Assert.Equal(79, Assert.Single(ParseYolox(output, 832, 468, .5f)).ClassId);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void RejectsNonfiniteBoxes(float width)
    {
        float[] output = Prediction(1050, .9f, 0, .9f, width, MathF.Log(12));
        Assert.Empty(ParseYolox(output, 832, 468, .5f));
    }

    [Fact]
    public unsafe void RejectsInvalidDimensionsAndInvalidScale()
    {
        float[] output = new float[TotalOutputFloats];
        Detection* detections = stackalloc Detection[8];
        Assert.Equal(-1, vn_smartcrop_parse_yolox_and_nms(output, 0, 468, .5f, detections, 8, out _, out _));
        Assert.Equal(-1, vn_smartcrop_parse_yolox_and_nms(output, 832, 0, .5f, detections, 8, out _, out _));
        Assert.Equal(-1, vn_smartcrop_parse_yolox_and_nms(output, 832, 468, 0f, detections, 8, out _, out _));
        Assert.Equal(-1, vn_smartcrop_parse_yolox_and_nms(output, 832, 468, float.NaN, detections, 8, out _, out _));
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

            int stride = image.PixelWidth * 4;
            byte[] decoded = new byte[image.PixelHeight * stride];
            BitmapSource source = image.Format == PixelFormats.Bgra32
                ? image
                : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            source.CopyPixels(decoded, stride, 0);

            var tensor = new float[3 * plane];
            Assert.Equal(0, vn_smartcrop_preprocess_bgra32(decoded, decoded.Length, image.PixelWidth, image.PixelHeight, stride, tensor, tensor.Length, out float scale));
            Assert.Equal(1f, scale);
            Assert.Equal(20f, tensor[0]);
            Assert.Equal(80f, tensor[plane]);
            Assert.Equal(160f, tensor[2 * plane]);
            Assert.Equal(20f, tensor[207 * 416 + 415]);
            foreach (int channel in new[] { 0, 1, 2 })
            { Assert.Equal(114f, tensor[channel * plane + 208 * 416]); Assert.Equal(114f, tensor[(channel + 1) * plane - 1]); }
        });
    }

    private static unsafe List<Detection> ParseYolox(ReadOnlySpan<float> output, int imgWidth, int imgHeight, float scale)
    {
        Detection* buf = stackalloc Detection[128];
        int rc = vn_smartcrop_parse_yolox_and_nms(output, imgWidth, imgHeight, scale, buf, 128, out _, out int count);
        Assert.Equal(0, rc);
        var list = new List<Detection>(count);
        for (int i = 0; i < count; i++) list.Add(buf[i]);
        return list;
    }

    private static float[] Prediction(int index, float objectness, int classId, float probability, float width, float height)
    {
        var output = new float[TotalOutputFloats];
        var row = output.AsSpan(index * RowStride, RowStride);
        row[0] = .5f;
        row[1] = .5f;
        row[2] = width;
        row[3] = height;
        row[4] = objectness;
        row[5 + classId] = probability;
        return output;
    }
}
