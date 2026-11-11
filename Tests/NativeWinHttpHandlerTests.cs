using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using VNotch.Models;
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
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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
                case "/set-cookie":
                    response.Headers.Add("Set-Cookie", "session=tracked-secret; Path=/");
                    break;
                case "/check-cookie":
                    {
                        byte[] cookieBytes = Encoding.UTF8.GetBytes(request.Headers["Cookie"] ?? "");
                        response.ContentType = "text/plain";
                        response.OutputStream.Write(cookieBytes, 0, cookieBytes.Length);
                        break;
                    }
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
                case "/brotli":
                    {
                        response.AddHeader("Content-Encoding", "br");
                        using var brotli = new BrotliStream(response.OutputStream, CompressionLevel.Fastest);
                        byte[] bytes = Encoding.UTF8.GetBytes(new string('b', 5000));
                        brotli.Write(bytes, 0, bytes.Length);
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
                    _stop.Token.WaitHandle.WaitOne(5000);
                    break;
            }
        }
        catch (Exception)
        {
            // Client-initiated aborts are expected during cancellation tests.
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* ignored */ }
        }
    }

    private static HttpClient NewClient(bool allowAutoRedirect = false) =>
        new(NetworkPrivacy.CreateHttpHandler(allowAutoRedirect)) { Timeout = TimeSpan.FromSeconds(20) };

    [Fact]
    public void CreateHttpHandlerConfiguresHardenedTransportDefaults()
    {
        using var handler = NetworkPrivacy.CreateHttpHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Equal(
            DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            handler.AutomaticDecompression);

        using var privacyWrapper = NetworkPrivacy.Handler(NetworkFeature.Weather);
        var delegating = Assert.IsAssignableFrom<DelegatingHandler>(privacyWrapper);
        var innerSockets = Assert.IsType<SocketsHttpHandler>(delegating.InnerHandler);
        Assert.False(innerSockets.AllowAutoRedirect);
        Assert.False(innerSockets.UseCookies);
    }

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
    public async Task RedirectsSurfaceByDefaultAndFollowOnlyWhenAllowed()
    {
        int seenBefore = Volatile.Read(ref _requestsSeen);
        using var manual = NewClient();
        using var surfaced = await manual.GetAsync(_prefix + "redirect", _stop.Token);
        Assert.Equal(HttpStatusCode.Found, surfaced.StatusCode);
        Assert.Equal("/echo", surfaced.Headers.Location?.OriginalString);
        Assert.Equal(seenBefore + 1, Volatile.Read(ref _requestsSeen));

        using var following = NewClient(allowAutoRedirect: true);
        using var followed = await following.GetAsync(_prefix + "redirect", _stop.Token);
        Assert.Equal(HttpStatusCode.OK, followed.StatusCode);
    }

    [Fact]
    public async Task SetCookieIsNotEchoedWhileExplicitCookieHeaderStillTransmits()
    {
        using var client = NewClient();
        using (var setCookie = await client.GetAsync(_prefix + "set-cookie", _stop.Token))
        {
            Assert.Equal(HttpStatusCode.OK, setCookie.StatusCode);
            Assert.True(setCookie.Headers.Contains("Set-Cookie"));
        }

        using (var subsequent = await client.GetAsync(_prefix + "check-cookie", _stop.Token))
        {
            Assert.Equal(string.Empty, await subsequent.Content.ReadAsStringAsync(_stop.Token));
        }

        using var manualCookieRequest = new HttpRequestMessage(HttpMethod.Get, _prefix + "check-cookie");
        manualCookieRequest.Headers.Add("Cookie", "sp_dc=explicit-session");
        using var explicitResponse = await client.SendAsync(manualCookieRequest, _stop.Token);
        Assert.Equal("sp_dc=explicit-session", await explicitResponse.Content.ReadAsStringAsync(_stop.Token));
    }

    [Fact]
    public async Task PrivacyRevocationCancelsActiveResponseStreamAndBlocksSubsequentRequests()
    {
        var policy = new NetworkPrivacy();
        using var client = new HttpClient(policy.CreateHandler(NetworkFeature.Weather))
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        // Default policy starts in local-only mode and blocks network access before reaching the socket.
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(_prefix + "echo", _stop.Token));

        policy.Apply(new NotchSettings
        {
            EnableLocalOnlyMode = false,
            AllowOnlineWeather = true
        });

        using var tcpServer = new TcpListener(IPAddress.Loopback, 0);
        tcpServer.Start();
        int streamPort = ((IPEndPoint)tcpServer.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await tcpServer.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            await using var net = accepted.GetStream();
            byte[] reqBuf = new byte[1024];
            _ = await net.ReadAsync(reqBuf, _stop.Token).ConfigureAwait(false);

            byte[] headerAndPrefix = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Length: 1024\r\nContent-Type: text/plain\r\n\r\nhello");
            await net.WriteAsync(headerAndPrefix, _stop.Token).ConfigureAwait(false);
            await net.FlushAsync(_stop.Token).ConfigureAwait(false);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Disposed at test teardown.
            }
        }, _stop.Token);

        using var response = await client.GetAsync(
            $"http://127.0.0.1:{streamPort}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            _stop.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(_stop.Token);

        byte[] initial = new byte[5];
        int firstRead = await stream.ReadAsync(initial, _stop.Token);
        Assert.Equal(5, firstRead);
        Assert.Equal("hello", Encoding.ASCII.GetString(initial));

        byte[] pending = new byte[64];
        var blockedRead = stream.ReadAsync(pending, _stop.Token).AsTask();

        var sw = Stopwatch.StartNew();
        policy.Apply(new NotchSettings
        {
            EnableLocalOnlyMode = false,
            AllowOnlineWeather = false
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => blockedRead.WaitAsync(TimeSpan.FromSeconds(4), _stop.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(_prefix + "echo", _stop.Token));
        tcpServer.Stop();
        _ = serverTask.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);
    }

    [Fact]
    public async Task HttpRequestHeadersRejectsCrlfInjection()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _prefix + "echo");
        Assert.Throws<FormatException>(() => request.Headers.Add("X-Custom", "valid\r\nX-Injected: bad"));
        Assert.Throws<FormatException>(() => request.Headers.Add("X-Custom\r\nBad", "valid"));
        Assert.False(request.Headers.TryAddWithoutValidation("X-Custom\r\nBad", "valid"));

        var policy = new NetworkPrivacy();
        policy.Apply(new NotchSettings { EnableLocalOnlyMode = false, AllowOnlineWeather = true });
        using var client = new HttpClient(policy.CreateHandler(NetworkFeature.Weather)) { Timeout = TimeSpan.FromSeconds(20) };
        request.Headers.TryAddWithoutValidation("X-Custom", "valid\r\nX-Injected: bad");
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request, _stop.Token));
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
    public async Task CompressedBodiesAreDecodedByTheTransport()
    {
        using var client = NewClient();
        using var gzipResponse = await client.GetAsync(_prefix + "gzip", _stop.Token);
        Assert.Equal(new string('a', 5000), await gzipResponse.Content.ReadAsStringAsync(_stop.Token));

        using var brotliResponse = await client.GetAsync(_prefix + "brotli", _stop.Token);
        Assert.Equal(new string('b', 5000), await brotliResponse.Content.ReadAsStringAsync(_stop.Token));
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
        var started = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync(_prefix + "slow", cts.Token));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(4), "cancellation must not wait for the server");
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
