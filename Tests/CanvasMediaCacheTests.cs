using System.IO;
using System.Net;
using System.Net.Http;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class CanvasMediaCacheTests
{
    [Fact]
    public async Task TwoDecodersReuseOneLocalDownloadAndInterruptedFilesAreRemoved()
    {
        string root = Directory.CreateTempSubdirectory("vnotch-canvas-cache-").FullName;
        try
        {
            var handler = new Handler();
            using var client = new HttpClient(handler);
            var cache = new CanvasMediaCache(client, root);
            var remote = new Uri("https://canvaz.scdn.co/fixture.mp4");
            var first = await cache.GetAsync(remote, default);
            var second = await cache.GetAsync(remote, default);
            Assert.Equal(first, second);
            Assert.True(first.IsFile);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(first.LocalPath));
            Assert.Equal(1, handler.Requests);
            handler.TooLarge = true;
            await Assert.ThrowsAsync<InvalidDataException>(() => cache.GetAsync(new Uri("https://canvaz.scdn.co/large.mp4"), default));
            Assert.Single(Directory.EnumerateFiles(root));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(root, true);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int Requests;
        internal bool TooLarge;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var content = new ByteArrayContent([1, 2, 3, 4]);
            if (TooLarge) content.Headers.ContentLength = 20 * 1024 * 1024;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
