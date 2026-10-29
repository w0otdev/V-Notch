using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class NativeWinHttpHandlerTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _prefix;
    private readonly Task _loop;
    private readonly CancellationTokenSource _stop = new();
    private int _requestsSeen;

    public NativeWinHttpHandlerTests()
    {
        int port = GetFreePort();
        _prefix = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        _loop = Task.Run(Serve, _stop.Token);
    }

    private static int GetFreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception) { return; }
            _ = Task.Run(() => Handle(context), _stop.Token);
        }
    }

    private void Handle(HttpListenerContext context)
    {
        Interlocked.Increment(ref _requestsSeen);
        var request = context.Request;
        var response = context.Response;
        try
        {
            switch (request.Url!.AbsolutePath)
            {
                case "/echo":
                    {
                        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                        string body = reader.ReadToEnd();
                        response.Headers["X-Seen-Agent"] = request.UserAgent ?? "";
                        response.Headers["X-Seen-Custom"] = request.Headers["X-Custom"] ?? "";
                        response.ContentType = "text/plain";
                        byte[] bytes = Encoding.UTF8.GetBytes($"{request.HttpMethod}|{body}");
                        response.OutputStream.Write(bytes, 0, bytes.Length);
                        break;
                    }
                case "/redirect":
                    response.StatusCode = 302;
                    response.RedirectLocation = "/echo";
                    break;
                case "/missing":
                    response.StatusCode = 404;
                    break;
                case "/gzip":
                    {
                        response.AddHeader("Content-Encoding", "gzip");
                        using var gzip = new GZipStream(response.OutputStream, CompressionLevel.Fastest);
                        byte[] bytes = Encoding.UTF8.GetBytes(new string('a', 5000));
                        gzip.Write(bytes, 0, bytes.Length);
                        break;
                    }
                case "/large":
                    {
                        byte[] chunk = new byte[64 * 1024];
                        Array.Fill(chunk, (byte)7);
                        response.ContentLength64 = chunk.Length * 20L;
                        for (int i = 0; i < 20; i++) response.OutputStream.Write(chunk, 0, chunk.Length);
                        break;
                    }
                case "/slow":
                    Thread.Sleep(5000);
                    break;
                case "/hang":
                    _stop.Token.WaitHandle.WaitOne(10_000);
                    break;
            }
        }
        catch (Exception)
        {
            // The client may abort the connection; that is part of the cancellation test.
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* ignored */ }
        }
    }

    private HttpClient NewClient(bool redirect = true) =>
        new(new NativeWinHttpHandler { AllowAutoRedirect = redirect }) { Timeout = TimeSpan.FromSeconds(20) };

    [Fact]
    public async Task GetAndPostRoundTripBodiesAndHeaders()
    {
        using var client = NewClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("VNotchTest/1.0");
        client.DefaultRequestHeaders.Add("X-Custom", "alpha");

        using var get = await client.GetAsync(_prefix + "echo", _stop.Token);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal("GET|", await get.Content.ReadAsStringAsync(_stop.Token));
        Assert.Equal("text/plain", get.Content.Headers.ContentType?.MediaType);
        Assert.Contains("VNotchTest/1.0", get.Headers.GetValues("X-Seen-Agent").Single());
        Assert.Equal("alpha", get.Headers.GetValues("X-Seen-Custom").Single());

        using var post = await client.PostAsync(_prefix + "echo", new StringContent("héllo", Encoding.UTF8, "text/plain"), _stop.Token);
        Assert.Equal("POST|héllo", await post.Content.ReadAsStringAsync(_stop.Token));
    }

    [Fact]
    public async Task RedirectsFollowOrSurfaceAccordingToPolicy()
    {
        using var following = NewClient(redirect: true);
        using var followed = await following.GetAsync(_prefix + "redirect", _stop.Token);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);

        using var manual = NewClient(redirect: false);
        using var surfaced = await manual.GetAsync(_prefix + "redirect", _stop.Token);
        Assert.Equal(HttpStatusCode.Found, surfaced.StatusCode);
        Assert.Equal("/echo", surfaced.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task ErrorStatusIsReportedAndEnsureSuccessThrows()
    {
        using var client = NewClient();
        using var response = await client.GetAsync(_prefix + "missing", _stop.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Throws<HttpRequestException>(() => response.EnsureSuccessStatusCode());
    }

    [Fact]
    public async Task GzipBodiesAreDecodedByTheTransport()
    {
        using var client = NewClient();
        using var response = await client.GetAsync(_prefix + "gzip", _stop.Token);
        Assert.Equal(new string('a', 5000), await response.Content.ReadAsStringAsync(_stop.Token));
    }

    [Fact]
    public async Task LargeBodiesStreamWithoutBufferingTheWholeResponse()
    {
        using var client = NewClient();
        using var response = await client.GetAsync(_prefix + "large", HttpCompletionOption.ResponseHeadersRead, _stop.Token);
        Assert.Equal(64 * 1024 * 20L, response.Content.Headers.ContentLength);
        await using var stream = await response.Content.ReadAsStreamAsync(_stop.Token);
        byte[] buffer = new byte[10_000];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, _stop.Token)) > 0) total += read;
        Assert.Equal(64 * 1024 * 20L, total);
    }

    [Fact]
    public async Task CancellationAbortsABlockedRequest()
    {
        using var client = NewClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var started = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(_prefix + "slow", cts.Token));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), "cancellation must not wait for the server");
    }

    private static class Native
    {
        private const string Dll = "VNotch.Native.dll";
        internal const int OperationCancelled = 12017;

        [DllImport(Dll, EntryPoint = "vn_http_create")]
        internal static extern int Create(out long handle);

        [DllImport(Dll, EntryPoint = "vn_http_send", CharSet = CharSet.Unicode)]
        internal static extern int Send(
            long handle, string method, int methodLength, string url, int urlLength, string headers, int headersLength,
            IntPtr body, int bodyLength, uint timeoutMs, uint flags, out int status, out int error);

        [DllImport(Dll, EntryPoint = "vn_http_cancel")]
        internal static extern int Cancel(long handle);

        [DllImport(Dll, EntryPoint = "vn_http_destroy")]
        internal static extern int Destroy(long handle);

        internal static int Get(long handle, string url, uint timeoutMs, out int status, out int error) =>
            Send(handle, "GET", 3, url, url.Length, string.Empty, 0, IntPtr.Zero, 0, timeoutMs, 0, out status, out error);
    }

    [Fact]
    public async Task CancelBeforeSendFailsFastWithoutContactingTheServer()
    {
        Assert.Equal(0, Native.Create(out long handle));
        try
        {
            Assert.Equal(0, Native.Cancel(handle));
            int seenBefore = Volatile.Read(ref _requestsSeen);
            var started = System.Diagnostics.Stopwatch.StartNew();

            int result = Native.Get(handle, _prefix + "slow", 20_000, out int status, out int error);

            Assert.NotEqual(0, result);
            Assert.Equal(0, status);
            Assert.Equal(Native.OperationCancelled, error);
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), "a cancelled request must not wait for the server");
            await Task.Delay(150, _stop.Token);
            Assert.Equal(seenBefore, Volatile.Read(ref _requestsSeen));
        }
        finally
        {
            Native.Destroy(handle);
        }
    }

    [Fact]
    public void CancelStaysCancelledForEverySubsequentSend()
    {
        Assert.Equal(0, Native.Create(out long handle));
        try
        {
            Native.Cancel(handle);
            for (int i = 0; i < 3; i++)
            {
                Assert.NotEqual(0, Native.Get(handle, _prefix + "echo", 5_000, out int status, out int error));
                Assert.Equal(0, status);
                Assert.Equal(Native.OperationCancelled, error);
            }
        }
        finally
        {
            Native.Destroy(handle);
        }
    }

    [Fact]
    public async Task CancelRacingWithSendNeverLeavesTheSendBlocked()
    {
        // The server never answers /hang, so a send that missed its cancel would block for the 20 s native timeout.
        for (int iteration = 0; iteration < 60; iteration++)
        {
            Assert.Equal(0, Native.Create(out long handle));
            try
            {
                var send = Task.Run(() => Native.Get(handle, _prefix + "hang", 20_000, out _, out _), _stop.Token);
                Thread.SpinWait(iteration * 400);
                Native.Cancel(handle);

                int status = await send.WaitAsync(TimeSpan.FromSeconds(5), _stop.Token);
                Assert.NotEqual(0, status);
            }
            finally
            {
                Native.Destroy(handle);
            }
        }
    }

    [Fact]
    public async Task TokenCancelledBeforeTheCallNeverReachesTheServer()
    {
        using var client = NewClient();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        int seenBefore = Volatile.Read(ref _requestsSeen);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(_prefix + "slow", cts.Token));

        await Task.Delay(150, _stop.Token);
        Assert.Equal(seenBefore, Volatile.Read(ref _requestsSeen));
    }

    [Fact]
    public async Task UnreachableHostsRaiseHttpRequestException()
    {
        using var client = NewClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{GetFreePort()}/", _stop.Token));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try { _loop.Wait(1000, CancellationToken.None); } catch (Exception) { /* ignored */ }
        _stop.Dispose();
    }
}
