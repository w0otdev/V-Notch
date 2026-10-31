using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;
using VNotch.Models;

namespace VNotch.Services.Clipboard;

internal static partial class ClipboardDropReader
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_drop_extract_download_url_image(
        ReadOnlySpan<char> downloadPtr,
        int downloadLen,
        out byte outFound,
        out int outStartIndex);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_drop_validate_data_uri_image(
        ReadOnlySpan<char> uriPtr,
        int uriLen,
        int maxImageBytes,
        out byte outValid,
        out int outCommaIndex);

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_clipboard_drop_strip_html_fragment_tags(
        ReadOnlySpan<char> htmlPtr,
        int htmlLen,
        Span<char> outBuf,
        int outCap,
        out int outLen);

    internal sealed record Payload(ClipboardCapture Capture, Uri? ImageUrl = null, BitmapSource? Bitmap = null)
    {
        internal ClipboardKind Kind => ImageUrl != null || Bitmap != null ? ClipboardKind.Image : ClipboardClassifier.Detect(Capture);
    }
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private static readonly HttpClient Client = CreateClient();

    internal static HttpClient CreateClient(HttpMessageHandler? transport = null, NetworkPrivacy? privacy = null) =>
        new(new NetworkPrivacy.PrivacyHandler(privacy ?? NetworkPrivacy.Current, NetworkFeature.Artwork,
            transport ?? CreateImageTransport()))
        { Timeout = TimeSpan.FromSeconds(20) };

    private static SocketsHttpHandler CreateImageTransport() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        // A proxy would resolve the destination again, bypassing the checked address.
        UseProxy = false,
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
                throw new HttpRequestException("Image downloads require a public Internet address.");
            SocketException? failure = null;
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException ex) { socket.Dispose(); failure = ex; }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("Could not connect to the image host.", failure);
        }
    };

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (b.Length == 4)
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                !(b[0] == 192 && (b[1] == 168 || b[1] == 0)) && !(b[0] == 198 && b[1] is 18 or 19);
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 2) &&
            !(b[0] == 0x20 && b[1] == 1 && b[2] == 0 && b[3] == 0);
    }

    private static async Task<HttpResponseMessage> DownloadAsync(Uri uri, HttpClient client, CancellationToken token)
    {
        for (int hop = 0; hop <= 5; hop++)
        {
            if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0)
                throw new InvalidDataException("Unsupported image URL.");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location == null) throw new InvalidDataException("Invalid image redirect.");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
        throw new InvalidDataException("Too many image redirects.");
    }

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
        {
            if ((long)bitmap.PixelWidth * bitmap.PixelHeight > 32_000_000) throw new InvalidDataException("Image is too large.");
            var snapshot = bitmap.Clone();
            snapshot.Freeze();
            return new(capture, Bitmap: snapshot);
        }
        if (data.GetData("PNG") is Stream png)
        {
            using var bytes = new MemoryStream();
            CopyBounded(png, bytes);
            return new(capture with { ImagePng = bytes.ToArray() });
        }
        string? image = null;
        if (ReadText(data, "DownloadURL") is string download && vn_clipboard_drop_extract_download_url_image(download.AsSpan(), download.Length, out byte found, out int startIdx) == 0 && found != 0)
        {

            image = download[startIdx..];
        }
        if (image == null && (string.IsNullOrWhiteSpace(text) || Uri.TryCreate(text.Trim(), UriKind.Absolute, out _)))
        {
            var match = ImageSourcePattern().Match(html);
            if (match.Success)
            {
                string rawImage = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[3].Value;
                if (match.Groups[2].Success) rawImage = match.Groups[2].Value;
                image = WebUtility.HtmlDecode(rawImage);
            }
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
            char[] buf = new char[html.Length];
            if (vn_clipboard_drop_strip_html_fragment_tags(html.AsSpan(), html.Length, buf, buf.Length, out int strippedLen) == 0)
                text = WebUtility.HtmlDecode(new string(buf, 0, strippedLen)).Trim();
        }
        return string.IsNullOrWhiteSpace(text) ? null : new(capture with { Text = text });
    }

    internal static async Task<ClipboardCapture> ResolveAsync(Payload payload, CancellationToken cancellation = default,
        HttpClient? client = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        cancellation = timeout.Token;
        if (payload.Bitmap is { } bitmap)
            return payload.Capture with { ImagePng = await Task.Run(() => Encode(bitmap), cancellation).ConfigureAwait(false) };
        if (payload.ImageUrl is not { } uri)
            return payload.Capture.ImagePng is { } png
                ? payload.Capture with { ImagePng = await Task.Run(() => Decode(png), cancellation).ConfigureAwait(false) }
                : payload.Capture;
        byte[] bytes;
        if (uri.Scheme == "data")
        {
            string value = uri.OriginalString;
            if (vn_clipboard_drop_validate_data_uri_image(value.AsSpan(), value.Length, MaxImageBytes, out byte valid, out int comma) != 0 || valid == 0)
                throw new InvalidDataException("Unsupported image data.");
            bytes = Convert.FromBase64String(value[(comma + 1)..]);
        }
        else
        {
            using var response = await DownloadAsync(uri, client ?? Client, cancellation).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxImageBytes) throw new InvalidDataException("Image is too large.");
            await using var input = await response.Content.ReadAsStreamAsync(cancellation);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                if (output.Length + read > MaxImageBytes) throw new InvalidDataException("Image is too large.");
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
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
