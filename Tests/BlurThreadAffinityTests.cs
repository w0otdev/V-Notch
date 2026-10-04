using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class BlurThreadAffinityTests
{
    [Theory]
    [InlineData("fast", false)]
    [InlineData("fast", true)]
    [InlineData("subject", false)]
    [InlineData("subject", true)]
    [InlineData("fallback", false)]
    [InlineData("fallback", true)]
    public void UnfrozenUiBitmapCanBeBlurredFromEitherThread(string mode, bool workerCaller) => SharedStaTestRunner.RunAsync(async () =>
    {
        var pixels = new byte[64 * 32 * 4];
        new Random(79).NextBytes(pixels);
        var source = BitmapSource.Create(64, 32, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        Assert.False(source.IsFrozen);
        Task<BitmapSource?> Blur() => mode switch
        {
            "fast" => FastBlurService.GetBlurredImageAsync(source, 64),
            "subject" => SubjectAwareBlurService.GetSubjectBlurredAsync(source, new SubjectBounds(0.5f, 0.5f, 0.4f, 0.6f, 1, 0), 64),
            _ => SubjectAwareBlurService.GetSubjectBlurredAsync(source, null, 64)
        };

        var result = await (workerCaller ? Task.Run(Blur) : Blur()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(result);
        Assert.True(result.IsFrozen);
        Assert.Equal(64, result.PixelWidth);
        Assert.Equal(32, result.PixelHeight);
        Assert.False(source.IsFrozen);
        var original = new byte[pixels.Length];
        source.CopyPixels(original, 64 * 4, 0);
        Assert.Equal(pixels, original);
        await Task.Run(() => result.CopyPixels(new byte[pixels.Length], 64 * 4, 0));
    });
}
