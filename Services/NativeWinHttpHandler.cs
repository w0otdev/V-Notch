using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace VNotch.Services;

/// <summary>
/// HttpMessageHandler whose transport is WinHTTP inside VNotch.Native (Native/Services/HttpTransport.cpp).
/// Services keep their HttpClient and handler-injection seams; the network I/O itself is native.
/// </summary>
internal sealed partial class NativeWinHttpHandler : HttpMessageHandler
{
    private const string NativeDll = "VNotch.Native.dll";
    private const uint FlagAllowRedirect = 1u << 0;
    private const uint FlagUseCookies = 1u << 1;
    private const uint FlagDecompress = 1u << 2;
    private const int NativeBufferTooSmall = -2;
    private const uint DefaultIoTimeoutMs = 60_000;

    [LibraryImport(NativeDll, EntryPoint = "vn_http_create")]
    private static partial int NativeCreate(out long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_http_send", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int NativeSend(
        long handle, string method, int methodLength, string url, int urlLength, string headers, int headersLength,
        byte* body, int bodyLength, uint timeoutMs, uint flags, out int status, out int error);

    [LibraryImport(NativeDll, EntryPoint = "vn_http_get_headers")]
    private static unsafe partial int NativeGetHeaders(long handle, char* buffer, int capacity, out int length);

    [LibraryImport(NativeDll, EntryPoint = "vn_http_get_content_length")]
    private static partial int NativeGetContentLength(long handle, out long length);

    [LibraryImport(NativeDll, EntryPoint = "vn_http_read")]
    private static unsafe partial int NativeRead(long handle, byte* buffer, int capacity, out int read, out int error);

    [LibraryImport(NativeDll, EntryPoint = "vn_http_cancel")]
    private static partial int NativeCancel(long handle);

    [LibraryImport(NativeDll, EntryPoint = "vn_http_destroy")]
    private static partial int NativeDestroy(long handle);

    private static readonly HashSet<string> ContentHeaderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length",
        "Content-Location", "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    public bool AllowAutoRedirect { get; init; } = true;

    public bool UseCookies { get; init; } = true;

    public bool AutomaticDecompression { get; init; } = true;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        Uri uri = request.RequestUri ?? throw new InvalidOperationException("The request URI is required.");
        if (!uri.IsAbsoluteUri) throw new InvalidOperationException("The request URI must be absolute.");

        byte[] body = request.Content == null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        string headers = BuildRequestHeaders(request);
        uint flags = (AllowAutoRedirect ? FlagAllowRedirect : 0) | (UseCookies ? FlagUseCookies : 0) |
                     (AutomaticDecompression ? FlagDecompress : 0);

        if (NativeCreate(out long handle) != 0 || handle <= 0)
            throw new HttpRequestException("The native HTTP transport could not allocate a request.");

        var session = new NativeHttpSession(handle);
        try
        {
            using (cancellationToken.Register(static state => ((NativeHttpSession)state!).Cancel(), session))
            {
                SendResult result = await Task.Run(() => Send(handle, request.Method.Method, uri.AbsoluteUri, headers, body, flags), CancellationToken.None)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (result.Native != 0)
                    throw new HttpRequestException($"WinHTTP request failed (native={result.Native}, error={result.Error}).");

                var response = new HttpResponseMessage((HttpStatusCode)result.Status)
                {
                    RequestMessage = request,
                    Content = new NativeResponseContent(session, AutomaticDecompression),
                };
                ApplyResponseHeaders(session, response, AutomaticDecompression);
                session = null!;
                return response;
            }
        }
        finally
        {
            session?.Dispose();
        }
    }

    private readonly record struct SendResult(int Native, int Status, int Error);

    private static unsafe SendResult Send(long handle, string method, string url, string headers, byte[] body, uint flags)
    {
        fixed (byte* bodyPointer = body)
        {
            int native = NativeSend(handle, method, method.Length, url, url.Length, headers, headers.Length,
                body.Length == 0 ? null : bodyPointer, body.Length, DefaultIoTimeoutMs, flags, out int status, out int error);
            return new SendResult(native, status, error);
        }
    }

    private static string BuildRequestHeaders(HttpRequestMessage request)
    {
        var builder = new StringBuilder();
        AppendHeaders(builder, request.Headers);
        if (request.Content != null) AppendHeaders(builder, request.Content.Headers);
        return builder.ToString();
    }

    private static void AppendHeaders(StringBuilder builder, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        foreach (var header in headers)
        {
            string separator = header.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase) ? " " : ", ";
            builder.Append(header.Key).Append(": ").AppendJoin(separator, header.Value).Append("\r\n");
        }
    }

    private static unsafe void ApplyResponseHeaders(NativeHttpSession session, HttpResponseMessage response, bool decompressed)
    {
        char* stackBuffer = stackalloc char[2048];
        int status = NativeGetHeaders(session.Handle, stackBuffer, 2048, out int length);
        string block;
        if (status == 0)
        {
            block = new string(stackBuffer, 0, length);
        }
        else if (status == NativeBufferTooSmall && length > 0 && length <= 1 << 20)
        {
            char[] heap = new char[length];
            fixed (char* pinned = heap)
            {
                if (NativeGetHeaders(session.Handle, pinned, length, out int written) != 0) return;
                block = new string(pinned, 0, written);
            }
        }
        else
        {
            return;
        }

        foreach (string line in block.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                ParseStatusLine(line, response);
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            // WinHTTP hands back decoded bytes, so the wire encoding and length no longer describe the body.
            if (decompressed && (name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (ContentHeaderNames.Contains(name))
                response.Content.Headers.TryAddWithoutValidation(name, value);
            else
                response.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static void ParseStatusLine(string line, HttpResponseMessage response)
    {
        string[] parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 0)
        {
            string text = parts[0]["HTTP/".Length..];
            if (!text.Contains('.')) text += ".0";
            if (Version.TryParse(text, out var version)) response.Version = version;
        }

        if (parts.Length > 2) response.ReasonPhrase = parts[2];
    }
    /// <summary>Owns one native request. Cancel aborts blocked I/O; Dispose waits for it and releases the request.</summary>
    private sealed class NativeHttpSession(long handle) : IDisposable
    {
        private readonly object _gate = new();
        private int _readsInFlight;
        private bool _disposed;

        public long Handle => handle;

        public void Cancel()
        {
            lock (_gate)
            {
                if (!_disposed) NativeCancel(handle);
            }
        }

        public bool TryBeginRead()
        {
            lock (_gate)
            {
                if (_disposed) return false;
                _readsInFlight++;
                return true;
            }
        }

        public void EndRead()
        {
            lock (_gate)
            {
                _readsInFlight--;
                Monitor.PulseAll(_gate);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                NativeCancel(handle);
                // Blocked reads return once the native handle is closed; wait before freeing the object.
                while (_readsInFlight > 0) Monitor.Wait(_gate, 50);
                NativeDestroy(handle);
            }
        }
    }

    private sealed class NativeResponseContent(NativeHttpSession session, bool decompressed) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var source = new NativeResponseStream(session, ownsSession: false);
            await source.CopyToAsync(stream, 81920, cancellationToken).ConfigureAwait(false);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new NativeResponseStream(session, ownsSession: false));

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            if (decompressed) return false;
            return NativeGetContentLength(session.Handle, out length) == 0 && length >= 0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) session.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class NativeResponseStream(NativeHttpSession session, bool ownsSession) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count), CancellationToken.None);

        public override int Read(Span<byte> buffer) => ReadCore(buffer, CancellationToken.None);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            cancellationToken.ThrowIfCancellationRequested();
            using (cancellationToken.Register(static state => ((NativeHttpSession)state!).Cancel(), session))
            {
                int read = await Task.Run(() => ReadCore(buffer.Span, cancellationToken), CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return read;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private unsafe int ReadCore(Span<byte> buffer, CancellationToken cancellationToken)
        {
            if (buffer.IsEmpty) return 0;
            if (!session.TryBeginRead()) throw new ObjectDisposedException(nameof(NativeResponseStream));
            try
            {
                fixed (byte* pinned = buffer)
                {
                    if (NativeRead(session.Handle, pinned, buffer.Length, out int read, out int error) == 0) return read;
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new IOException($"WinHTTP read failed (error={error}).");
                }
            }
            finally
            {
                session.EndRead();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && ownsSession) session.Dispose();
            base.Dispose(disposing);
        }
    }
}
