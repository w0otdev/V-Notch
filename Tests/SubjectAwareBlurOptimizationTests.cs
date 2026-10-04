using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SubjectAwareBlurOptimizationTests
{
    [Theory]
    [InlineData(192, 192, 16, 6, 0.5f, 0.5f, 0.4f, 0.6f)]
    [InlineData(64, 1, 32, 0, 0f, 1f, 0f, 0f)]
    [InlineData(65, 17, 32, 32, 1f, 0f, 1f, 1f)]
    [InlineData(257, 31, 21, 1, -0.1f, 1.1f, 0.1f, 0.2f)]
    public async Task RenderMatchesOriginalPixelsIncludingFeatherAndRadius32(
        int width, int height, int backgroundRadius, int subjectRadius,
        float centerX, float centerY, float subjectWidth, float subjectHeight)
    {
        byte[] pixels = new byte[width * height * 4];
        new Random(42).NextBytes(pixels);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        var subject = new SubjectBounds(centerX, centerY, subjectWidth, subjectHeight, 1f, 0);
        byte[] expected = RenderOriginal(source, subject, backgroundRadius, subjectRadius);

        var result = await SubjectAwareBlurService.GetSubjectBlurredAsync(source, subject, width, backgroundRadius, subjectRadius);

        Assert.NotNull(result);
        Assert.True(result.IsFrozen);
        Assert.Equal(width, result.PixelWidth);
        Assert.Equal(height, result.PixelHeight);
        byte[] actual = new byte[pixels.Length];
        result.CopyPixels(actual, width * 4, 0);
        Assert.Equal(expected, actual);
    }

    private static byte[] RenderOriginal(BitmapSource source, SubjectBounds subject, int backgroundRadius, int subjectRadius)
    {
        int width = source.PixelWidth, height = source.PixelHeight;
        byte[] background = new byte[width * height * 4];
        var transformed = new TransformedBitmap(ArtworkAnalysisSource.GetBgra32(source), new ScaleTransform(1, 1));
        transformed.CopyPixels(background, width * 4, 0);
        byte[] subjectLayer = (byte[])background.Clone();
        byte[] temporary = new byte[background.Length];
        for (int pass = 0; pass < 2; pass++)
        {
            BaselineFastBlurService.BoxBlurHorizontal(background, temporary, width, height, backgroundRadius);
            BaselineFastBlurService.BoxBlurVertical(temporary, background, width, height, backgroundRadius);
        }
        BaselineFastBlurService.DarkenPixels(background, background.Length, 0.78f);
        if (subjectRadius > 0)
        {
            BaselineFastBlurService.BoxBlurHorizontal(subjectLayer, temporary, width, height, subjectRadius);
            BaselineFastBlurService.BoxBlurVertical(temporary, subjectLayer, width, height, subjectRadius);
        }
        for (int p = 0; p < subjectLayer.Length; p += 4)
        {
            for (int c = 0; c < 3; c++)
                subjectLayer[p + c] = (byte)Math.Min(255, subjectLayer[p + c] * 1.04f);
        }

        float cx = Math.Clamp(subject.CenterX, 0f, 1f) * width;
        float cy = Math.Clamp(subject.CenterY, 0f, 1f) * height;
        float rx = MathF.Max(width * 0.18f, subject.Width * width * 0.55f);
        float ry = MathF.Max(height * 0.22f, subject.Height * height * 0.55f);
        for (int y = 0; y < height; y++)
        {
            float dy = (y - cy) / ry;
            for (int x = 0; x < width; x++)
            {
                float dx = (x - cx) / rx;
                float distance = MathF.Sqrt(dx * dx + dy * dy);
                float u = (distance - 1f) / 0.4f;
                float weight = distance <= 1f ? 1f : distance >= 1f + 0.4f ? 0f : 1f - u * u * (3f - 2f * u);
                int p = (y * width + x) * 4;
                for (int c = 0; c < 3; c++)
                    background[p + c] = (byte)(background[p + c] + (subjectLayer[p + c] - background[p + c]) * weight);
                background[p + 3] = 255;
            }
        }
        return background;
    }
}
