using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ArtworkCacheLifetimeTests(ITestOutputHelper output)
{
    [Fact]
    public void PreviousTracksCanBeCollectedWhileTheLatestCropStaysWarm() => SharedStaTestRunner.Run(() =>
    {
        MediaArtworkService.TrimGlobalMemory(false);
        try
        {
            using var service = new MediaArtworkService();
            var images = CacheCrops(service, 20);
            Collect();
            int retained = CountAlive(images);
            output.WriteLine($"After 20 distinct 512 x 512 BGRA covers: {retained} cached images alive, {retained} MiB pixel payload.");
            Assert.True(IsAlive(images[^1]));
            Assert.Equal(1, retained);

            MediaArtworkService.TrimGlobalMemory(false);
            Collect();
            Assert.Equal(0, CountAlive(images));
        }
        finally { MediaArtworkService.TrimGlobalMemory(false); }
    });

    [Fact]
    public async Task PreviousDecodesCanBeCollectedWhileTheLatestDecodeStaysWarm()
    {
        MediaArtworkService.TrimGlobalMemory(false);
        try
        {
            using var client = new HttpClient(new ArtworkHandler());
            using var service = new MediaArtworkService(client);
            var images = await CacheDecodes(service);
            Collect();
            int retained = CountAlive(images);
            output.WriteLine($"After four distinct 512 x 512 BGRA decodes: {retained} cached images alive, {retained} MiB pixel payload.");
            Assert.True(IsAlive(images[^1]));
            Assert.Equal(1, retained);

            MediaArtworkService.TrimGlobalMemory(false);
            Collect();
            Assert.Equal(0, CountAlive(images));
        }
        finally { MediaArtworkService.TrimGlobalMemory(false); }
    }

    [Fact]
    public async Task ArtworkStillInUseReusesTheSameDecodeAndCropAcrossCollections()
    {
        MediaArtworkService.TrimGlobalMemory(false);
        try
        {
            using var client = new HttpClient(new ArtworkHandler());
            using var service = new MediaArtworkService(client);
            var sources = new BitmapImage[4];
            var crops = new BitmapSource[4];
            for (int i = 0; i < sources.Length; i++)
            {
                sources[i] = (await service.DownloadImageAsync($"https://fixture.invalid/{i}"))!;
                Assert.NotNull(sources[i]);
                crops[i] = service.CropToSquare(sources[i], "test", forceCenterCrop: true)!;
                Assert.NotNull(crops[i]);
            }
            Collect();
            for (int i = 0; i < sources.Length; i++)
            {
                Assert.Same(sources[i], await service.DownloadImageAsync($"https://fixture.invalid/{i}"));
                Assert.Same(crops[i], service.CropToSquare(sources[i], "test", forceCenterCrop: true));
            }
        }
        finally { MediaArtworkService.TrimGlobalMemory(false); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReclaimedCropsRegenerateWithIdenticalPixels(bool smartCrop) => SharedStaTestRunner.Run(() =>
    {
        MediaArtworkService.TrimGlobalMemory(false);
        try
        {
            using var service = new MediaArtworkService { EnableSmartCrop = smartCrop };
            var source = CreateArtwork(735, 512, 256);
            var (image, expected, width, height) = SnapshotCrop(service, source, !smartCrop);
            _ = CacheCrops(service, 2);
            Collect();
            Assert.False(IsAlive(image));
            var regenerated = service.CropToSquare(source, "test", forceCenterCrop: !smartCrop);
            Assert.NotNull(regenerated);
            Assert.True(regenerated.IsFrozen);
            Assert.Equal(width, regenerated.PixelWidth);
            Assert.Equal(height, regenerated.PixelHeight);
            Assert.Equal(source.DpiX, regenerated.DpiX);
            Assert.Equal(source.DpiY, regenerated.DpiY);
            Assert.Equal(expected, ReadPixels(regenerated));
        }
        finally { MediaArtworkService.TrimGlobalMemory(false); }
    });

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<BitmapSource>[] CacheCrops(MediaArtworkService service, int count)
    {
        var images = new WeakReference<BitmapSource>[count];
        for (int i = 0; i < count; i++)
        {
            var image = service.CropToSquare(CreateArtwork(i, 512, 512), "test", forceCenterCrop: true);
            Assert.NotNull(image);
            images[i] = new(image);
        }
        return images;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference<BitmapImage>[]> CacheDecodes(MediaArtworkService service)
    {
        var images = new WeakReference<BitmapImage>[4];
        for (int i = 0; i < images.Length; i++)
        {
            var image = await service.DownloadImageAsync($"https://fixture.invalid/{i}");
            Assert.NotNull(image);
            Assert.Equal(512, image.PixelWidth);
            Assert.Equal(512, image.PixelHeight);
            Assert.Equal(32, image.Format.BitsPerPixel);
            images[i] = new(image);
        }
        return images;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<BitmapSource>, byte[], int, int) SnapshotCrop(MediaArtworkService service, BitmapSource source, bool center)
    {
        var image = service.CropToSquare(source, "test", forceCenterCrop: center)!;
        Assert.NotNull(image);
        return (new(image), ReadPixels(image), image.PixelWidth, image.PixelHeight);
    }

    private static byte[] ReadPixels(BitmapSource image)
    {
        int stride = (image.PixelWidth * image.Format.BitsPerPixel + 7) / 8;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static BitmapSource CreateArtwork(int seed, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(seed).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int CountAlive<T>(WeakReference<T>[] images) where T : class => images.Count(image => image.TryGetTarget(out _));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> image) where T : class => image.TryGetTarget(out _);

    private static void Collect()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private sealed class ArtworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int seed = int.Parse(request.RequestUri!.AbsolutePath.AsSpan(1));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(CreateArtwork(seed, 512, 512)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) });
        }
    }
}
