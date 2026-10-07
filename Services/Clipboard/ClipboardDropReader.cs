using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

internal static partial class ClipboardDropReader
{
    internal sealed record Payload(ClipboardCapture Capture, Uri? ImageUrl = null)
    {
        internal ClipboardKind Kind => ImageUrl != null ? ClipboardKind.Image : ClipboardClassifier.Detect(Capture);
    }
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20) };

    internal static bool Supports(IDataObject data)
    {
        try
        {
            return new[] { DataFormats.FileDrop, DataFormats.Bitmap, DataFormats.UnicodeText, DataFormats.Text,
                DataFormats.Html, "PNG", "DownloadURL", "text/uri-list" }.Any(data.GetDataPresent);
        }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    // Read the OLE object before leaving the Drop event; it may expire after the first await.
    internal static Payload? Read(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop))
        {
            if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths ||
                paths.Length > ClipboardHistoryStore.MaxImportItems || paths.Any(p => !ClipboardImportPaths.IsLocal(p))) return null;
            return new(new ClipboardCapture { FilePaths = paths });
        }
        string text = ReadText(data, DataFormats.UnicodeText) ?? ReadText(data, DataFormats.Text) ?? "";
        string html = ReadText(data, DataFormats.Html) ?? "";
        var capture = new ClipboardCapture { Text = text, Html = html };
        if (data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return new(capture with { ImagePng = Encode(bitmap) });
        if (data.GetData("PNG") is Stream png)
        {
            using var bytes = new MemoryStream();
            CopyBounded(png, bytes);
            return new(capture with { ImagePng = Decode(bytes.ToArray()) });
        }
        string? image = null;
        if (ReadText(data, "DownloadURL") is string download)
        {
            int first = download.IndexOf(':');
            int second = first < 0 ? -1 : download.IndexOf(':', first + 1);
            if (second > first && download.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) image = download[(second + 1)..];
        }
        if (image == null && (string.IsNullOrWhiteSpace(text) || Uri.TryCreate(text.Trim(), UriKind.Absolute, out _)))
        {
            var match = ImageSourcePattern().Match(html);
            if (match.Success) image = WebUtility.HtmlDecode(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
        }
        if (image != null)
        {
            if (!Uri.TryCreate(image, UriKind.Absolute, out var uri))
            {
                var source = SourcePattern().Match(html);
                if (source.Success && Uri.TryCreate(source.Groups[1].Value.Trim(), UriKind.Absolute, out var page))
                    Uri.TryCreate(page, image, out uri);
            }
            if (uri?.Scheme is "http" or "https" or "data") return new(capture, uri);
        }
        if (string.IsNullOrWhiteSpace(text)) text = ReadText(data, "text/uri-list") ?? "";
        if (string.IsNullOrWhiteSpace(text) && html.Length > 0)
        {
            int start = html.IndexOf("<!--StartFragment-->", StringComparison.Ordinal);
            int end = html.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
            string fragment = start >= 0 && end > start ? html[(start + 20)..end] : html;
            text = WebUtility.HtmlDecode(Regex.Replace(fragment, "<[^>]+>", " ")).Trim();
        }
        return string.IsNullOrWhiteSpace(text) ? null : new(capture with { Text = text });
    }

    internal static async Task<ClipboardCapture> ResolveAsync(Payload payload, CancellationToken cancellation = default)
    {
        if (payload.ImageUrl is not { } uri) return payload.Capture;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        cancellation = timeout.Token;
        byte[] bytes;
        if (uri.Scheme == "data")
        {
            string value = uri.OriginalString;
            int comma = value.IndexOf(',');
            if (comma < 0 || !value[..comma].StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
                !value[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase) || value.Length - comma > MaxImageBytes * 4 / 3 + 4)
                throw new InvalidDataException("Unsupported image data.");
            bytes = Convert.FromBase64String(value[(comma + 1)..]);
        }
        else
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("Image is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                if (output.Length + read > MaxImageBytes) throw new InvalidDataException("Image is too large.");
                output.Write(buffer, 0, read);
            }
            bytes = output.ToArray();
        }
        return payload.Capture with { ImagePng = await Task.Run(() => Decode(bytes), cancellation) };
    }

    private static byte[] Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return Encode(decoder.Frames[0]);
    }
    private static string? ReadText(IDataObject data, string format)
    {
        if (!data.GetDataPresent(format)) return null;
        object? value = data.GetData(format);
        if (value is string text) return text.TrimEnd('\0');
        if (value is not Stream stream) return null;
        long position = stream.CanSeek ? stream.Position : 0;
        try
        {
            using var output = new MemoryStream();
            CopyBounded(stream, output);
            byte[] bytes = output.ToArray();
            bool unicode = format == DataFormats.UnicodeText || bytes.Length > 1 && bytes[1] == 0;
            return (unicode ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8).GetString(bytes).Trim('\0', '\uFEFF');
        }
        finally { if (stream.CanSeek) stream.Position = position; }
    }
    private static byte[] Encode(BitmapSource bitmap)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32_000_000) throw new InvalidDataException("Image is too large.");
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(output);
        if (output.Length > MaxImageBytes) throw new InvalidDataException("Image is too large.");
        return output.ToArray();
    }
    private static void CopyBounded(Stream input, Stream output)
    {
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxImageBytes) throw new InvalidDataException("Image is too large.");
            output.Write(buffer, 0, read);
        }
    }
    [GeneratedRegex("<img\\b[^>]*?\\bsrc\\s*=\\s*(?:\"([^\"]+)\"|'([^']+)'|([^\\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex ImageSourcePattern();
    [GeneratedRegex(@"(?m)^SourceURL:([^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SourcePattern();
}
