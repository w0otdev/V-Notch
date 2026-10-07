using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardBrowserDropTests
{
    [Fact]
    public void ChromiumStreamMetadataIsDecodedBeforeDropObjectExpires() => SharedStaTestRunner.RunAsync(() =>
    {
        var data = new DataObject();
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("image/png:photo.png:https://example.com/photo.png\0"));
        data.SetData("DownloadURL", stream);
        Assert.Equal("https://example.com/photo.png", ClipboardDropReader.Read(data)!.ImageUrl!.AbsoluteUri);
        Assert.Equal(0, stream.Position);
        return Task.CompletedTask;
    });

    [Fact]
    public void RemoteImageDownloadDecodesTheResponse() => SharedStaTestRunner.RunAsync(async () =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4));
        byte[] png = ClipboardDropReader.Read(data)!.Capture.ImagePng!;
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(timeout.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 }) { }
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {png.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, timeout.Token);
            await stream.WriteAsync(png, timeout.Token);
        }, timeout.Token);
        var address = new Uri($"http://127.0.0.1:{((System.Net.IPEndPoint)listener.LocalEndpoint).Port}/image");
        var capture = await ClipboardDropReader.ResolveAsync(new(new ClipboardCapture(), address), timeout.Token);
        await serve;
        Assert.Equal(ClipboardKind.Image, ClipboardClassifier.Detect(capture));
        Assert.NotEmpty(capture.ImagePng!);
    });

    [Fact]
    public void TextSelectionPreservesTextAndHtml() => SharedStaTestRunner.RunAsync(() =>
    {
        var data = new DataObject();
        data.SetText("Đoạn text từ trình duyệt");
        data.SetData(DataFormats.Html, "<b>Đoạn text từ trình duyệt</b>");
        Assert.True(ClipboardDropReader.Supports(data));
        var payload = ClipboardDropReader.Read(data)!;
        Assert.Null(payload.ImageUrl);
        Assert.Equal("Đoạn text từ trình duyệt", payload.Capture.Text);
        Assert.Equal("<b>Đoạn text từ trình duyệt</b>", payload.Capture.Html);
        return Task.CompletedTask;
    });

    [Fact]
    public void ChromiumDownloadUrlIsAnImageNotALink() => SharedStaTestRunner.RunAsync(() =>
    {
        var data = new DataObject();
        data.SetData("DownloadURL", "image/png:palette.png:https://example.com/image?id=1");
        data.SetText("https://example.com/image?id=1");
        var payload = ClipboardDropReader.Read(data)!;
        Assert.Equal(ClipboardKind.Image, payload.Kind);
        Assert.Equal("https://example.com/image?id=1", payload.ImageUrl!.AbsoluteUri);
        return Task.CompletedTask;
    });

    [Fact]
    public void HtmlImageResolvesRelativeSourceAndEntities() => SharedStaTestRunner.RunAsync(() =>
    {
        var data = new DataObject();
        data.SetData(DataFormats.Html, "SourceURL:https://example.com/gallery/\r\n<!--StartFragment--><img src='../photo.png?a=1&amp;b=2'><!--EndFragment-->");
        Assert.Equal("https://example.com/photo.png?a=1&b=2", ClipboardDropReader.Read(data)!.ImageUrl!.AbsoluteUri);
        return Task.CompletedTask;
    });

    [Fact]
    public void BitmapAndDataUrlProduceStoredImageWithCorrectPixels() => SharedStaTestRunner.RunAsync(async () =>
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 30, 20, 10, 255 }, 4);
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, bitmap);
        var direct = ClipboardDropReader.Read(data)!;
        Assert.Equal(ClipboardKind.Image, direct.Kind);
        Assert.NotNull(direct.Capture.ImagePng);
        var html = new DataObject();
        html.SetData(DataFormats.Html, "<img src=\"data:image/png;base64," + Convert.ToBase64String(direct.Capture.ImagePng!) + "\">");
        var capture = await ClipboardDropReader.ResolveAsync(ClipboardDropReader.Read(html)!);
        using var stream = new MemoryStream(capture.ImagePng!);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var pixels = new byte[4];
        new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 4, 0);
        Assert.Equal(new byte[] { 30, 20, 10, 255 }, pixels);
        string root = Path.Combine(Path.GetTempPath(), "browser-drop-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(capture)).Entry!;
            Assert.Equal(ClipboardKind.Image, entry.Kind);
            Assert.Equal(entry.Id, Assert.Single(await store.SearchAsync("", "Images")).Id);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    [Fact]
    public void PlainLinkStaysLinkAndRemoteFileDropIsRejected() => SharedStaTestRunner.RunAsync(() =>
    {
        var data = new DataObject();
        data.SetText("https://example.com/page");
        Assert.Equal(ClipboardKind.Link, ClipboardDropReader.Read(data)!.Kind);
        data.SetData(DataFormats.FileDrop, new[] { @"\\server\share\file.png" });
        Assert.Null(ClipboardDropReader.Read(data));
        return Task.CompletedTask;
    });
}
