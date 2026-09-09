using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using VNotch.Services;
using Windows.Foundation;
using Windows.Storage.Streams;
using Xunit;

namespace VNotch.Tests;

public class MediaArtworkServiceTests : IDisposable
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Handler == null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(Handler(request));
        }
    }

    private sealed class FakeOversizedStream : IRandomAccessStreamWithContentType
    {
        public ulong Size { get; set; } = 9 * 1024 * 1024; // 9 MiB
        public string ContentType => "image/png";
        public bool CanRead => true;
        public bool CanWrite => false;
        public ulong Position => 0;

        public IInputStream GetInputStreamAt(ulong position) => throw new NotImplementedException();
        public IOutputStream GetOutputStreamAt(ulong position) => throw new NotImplementedException();
        public void Seek(ulong position) => throw new NotImplementedException();
        public IRandomAccessStream CloneStream() => throw new NotImplementedException();
        public void Dispose() { }
        public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options) => throw new NotImplementedException();
        public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) => throw new NotImplementedException();
        public IAsyncOperation<bool> FlushAsync() => throw new NotImplementedException();
    }

    private static byte[] CreateTestPngBytes(int width, int height)
    {
        var pixelFormat = System.Windows.Media.PixelFormats.Bgra32;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;     // B
            pixels[i + 1] = 128; // G
            pixels[i + 2] = 64;  // R
            pixels[i + 3] = 255; // A
        }

        var source = BitmapSource.Create(width, height, 96, 96, pixelFormat, null, pixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task DownloadImageAsync_WhenContentLengthExceedsLimit_RejectsImmediately()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(new byte[10])
                };
                response.Content.Headers.ContentLength = 9 * 1024 * 1024; // 9 MiB declared
                return response;
            }
        };

        using var client = new HttpClient(handler);
        using var service = new MediaArtworkService(client);

        var result = await service.DownloadImageAsync("https://example.com/huge.png");

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadImageAsync_WhenStreamExceedsLimit_AbortsDownload()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ =>
            {
                // Generate a stream exceeding 8 MiB without Content-Length
                byte[] chunk = new byte[9 * 1024 * 1024]; // 9 MiB
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(chunk)
                };
                response.Content.Headers.ContentLength = null; // chunked / no content length
                return response;
            }
        };

        using var client = new HttpClient(handler);
        using var service = new MediaArtworkService(client);

        var result = await service.DownloadImageAsync("https://example.com/stream-huge.png");

        Assert.Null(result);
    }

    [Fact]
    public async Task DownloadImageAsync_ValidImage_DecodesAndFreezesOffUiThread()
    {
        byte[] pngBytes = CreateTestPngBytes(1600, 1200);

        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pngBytes)
            }
        };

        using var client = new HttpClient(handler);
        using var service = new MediaArtworkService(client);

        var bitmap = await service.DownloadImageAsync("https://example.com/artwork.png");

        Assert.NotNull(bitmap);
        Assert.True(bitmap.IsFrozen);
        Assert.Equal(1024, bitmap.DecodePixelWidth);
        Assert.Equal(1024, bitmap.PixelWidth);
        Assert.Equal(768, bitmap.PixelHeight); // 1600x1200 scaled to 1024 width -> 768 height
    }

    [Fact]
    public async Task DownloadImageAsync_InvalidImageBytes_ReturnsNullSafely()
    {
        var handler = new MockHttpMessageHandler
        {
            Handler = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4, 5 })
            }
        };

        using var client = new HttpClient(handler);
        using var service = new MediaArtworkService(client);

        var bitmap = await service.DownloadImageAsync("https://example.com/corrupt.png");

        Assert.Null(bitmap);
    }

    [Fact]
    public async Task ConvertToWpfBitmapAsync_WhenStreamExceedsLimit_ReturnsNull()
    {
        using var service = new MediaArtworkService();
        var fakeStream = new FakeOversizedStream { Size = 10 * 1024 * 1024 };

        var result = await service.ConvertToWpfBitmapAsync(fakeStream);

        Assert.Null(result);
    }

    public void Dispose()
    {
    }
}
