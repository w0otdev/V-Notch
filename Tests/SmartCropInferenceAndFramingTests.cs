using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;
using Detection = VNotch.Services.SmartThumbnailCropService.Detection;

namespace VNotch.Tests;

public sealed class SmartCropInferenceAndFramingTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    [Fact]
    public void BundledModelRunsOfflineAndReusesInferenceUntilItsIdleSessionIsReleased() => SharedStaTestRunner.Run(() =>
    {
        using var service = new SmartThumbnailCropService();
        Assert.True(service.TryInitialize());
        var image = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Fixtures", "white-mat-artwork.png")));
        image.Freeze();
        var first = service.GetSmartCropRect(image, 256, useObjectDetection: true);
        Assert.True(first.HasValue);
        AssertCrop(first.Value, image.PixelWidth, image.PixelHeight);
        object session = Get<object>(service, "_cachedSession");
        Assert.NotNull(session);
        Assert.Equal(first, service.GetSmartCropRect(image, 256, useObjectDetection: true));
        Assert.Same(session, Get<object>(service, "_cachedSession"));
        _ = service.GetDominantSubjectBounds(image);
        Assert.Same(session, Get<object>(service, "_cachedSession"));
        typeof(SmartThumbnailCropService).GetField("_lastUsedUtc", Private)!.SetValue(service, DateTime.UtcNow.AddMinutes(-6));
        Invoke(service, "OnIdleCheck", (object?)null);
        Assert.Null(Get<object?>(service, "_cachedSession"));
        Assert.Null(Get<object?>(service, "_idleTimer"));
        service.Unload();
        service.Dispose();
        Assert.False(service.TryInitialize());
        Assert.Null(service.GetDominantSubjectBounds(image));
        Assert.Null(service.GetSmartCropRect(image, 256, true));
    });

    [Theory]
    [InlineData(0, .31f)]
    [InlineData(16, .5f)]
    [InlineData(41, .5f)]
    public void CachedDetectionsProduceNormalizedSubjectBoundsWithoutAnotherInference(int classId, float expectedY) => SharedStaTestRunner.Run(() =>
    {
        using var service = new SmartThumbnailCropService();
        Assert.True(service.TryInitialize());
        var image = Image(400, 200, false);
        Detection[] detections = [new() { X1 = 100, Y1 = 50, X2 = 300, Y2 = 150, Confidence = .9f, ClassId = classId }, new() { X1 = 10, Y1 = 10, X2 = 10, Y2 = 10, Confidence = .99f, ClassId = classId }];
        Invoke(service, "AddInferenceCacheEntryLocked", ArtworkFingerprint.Create(image), detections);
        var subject = service.GetDominantSubjectBounds(image);
        Assert.True(subject.HasValue);
        var expected = new SubjectBounds(.5f, expectedY, .5f, .5f, .9f, classId);
        Assert.Equal(expected, subject.Value);
        Assert.Null(Get<object?>(service, "_cachedSession"));
        Assert.Equal(subject, service.GetDominantSubjectBounds(image));
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HybridFramingPreservesPortraitGroupsObjectsAndTextWithinTheImage(int scenario) => SharedStaTestRunner.Run(() =>
    {
        using var service = new SmartThumbnailCropService();
        var image = Image(400, 200, scenario == 3);
        IReadOnlyList<Detection> detections = scenario switch
        {
            0 => [new() { X1 = 130, Y1 = 20, X2 = 230, Y2 = 185, Confidence = .9f, ClassId = 0 }, new() { X1 = 350, Y1 = 40, X2 = 390, Y2 = 100, Confidence = .1f, ClassId = 0 }],
            1 => [new() { X1 = 100, Y1 = 20, X2 = 160, Y2 = 180, Confidence = .9f, ClassId = 0 }, new() { X1 = 200, Y1 = 20, X2 = 260, Y2 = 180, Confidence = .85f, ClassId = 0 }],
            2 => [new() { X1 = 160, Y1 = 40, X2 = 240, Y2 = 160, Confidence = .9f, ClassId = 16 }, new() { X1 = 350, Y1 = 10, X2 = 390, Y2 = 50, Confidence = .4f, ClassId = 41 }],
            _ => []
        };
        var crop = (Int32Rect)Invoke(service, "GetHybridCropRect", detections, image, 400, 200, 160)!;
        AssertCrop(crop, 400, 200);
        Assert.InRange(200, crop.X, crop.X + crop.Width);
        if (scenario == 0) Assert.True(crop.Y <= 20);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextAnalysisDistinguishesFlatArtworkFromDenseContrastingEdges(bool edges) => SharedStaTestRunner.Run(() =>
    {
        var image = Image(256, 128, edges);
        var method = typeof(SmartThumbnailCropService).GetMethod("DetectTextRegions", BindingFlags.Static | BindingFlags.NonPublic)!;
        var regions = (System.Collections.ICollection)method.Invoke(null, [image, 256, 128])!;
        if (edges) Assert.True(regions.Count >= 2);
        else Assert.Empty(regions.Cast<object>());
    });

    [Theory]
    [InlineData(400, 200)]
    [InlineData(200, 400)]
    public void ModelInputKeepsBgrValuesAndUses114ForLetterboxedPixels(int width, int height) => SharedStaTestRunner.Run(() =>
    {
        var image = Image(width, height, false);
        const int plane = 416 * 416;
        float[] tensor = new float[3 * plane];
        float scale = SmartThumbnailCropService.PreprocessImageFast(image, tensor);
        Assert.Equal(416f / Math.Max(width, height), scale, 5);
        Assert.Equal(30, tensor[0]);
        Assert.Equal(20, tensor[plane]);
        Assert.Equal(10, tensor[2 * plane]);
        Assert.Equal(114, tensor[plane - 1]);
        Assert.Equal(114, tensor[2 * plane - 1]);
        Assert.Equal(114, tensor[3 * plane - 1]);
    });

    private static BitmapImage Image(int width, int height, bool edges)
    {
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 3;
                byte value = (byte)((x / 4 + y / 4) % 2 == 0 ? 255 : 0);
                pixels[index] = edges ? value : (byte)10;
                pixels[index + 1] = edges ? value : (byte)20;
                pixels[index + 2] = edges ? value : (byte)30;
            }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(stream);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }
    private static void AssertCrop(Int32Rect crop, int width, int height)
    {
        Assert.True(crop.Width > 0);
        Assert.Equal(crop.Width, crop.Height);
        Assert.InRange(crop.X, 0, width - crop.Width);
        Assert.InRange(crop.Y, 0, height - crop.Height);
    }
    private static T Get<T>(SmartThumbnailCropService service, string name) => (T)typeof(SmartThumbnailCropService).GetField(name, Private)!.GetValue(service)!;
    private static object? Invoke(SmartThumbnailCropService service, string name, params object?[] arguments) => typeof(SmartThumbnailCropService).GetMethod(name, Private)!.Invoke(service, arguments);
}
