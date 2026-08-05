using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyCanvasServiceTests
{
    private const string TrackId = "3OHfY25tqY28d16oZczHc8";
    private const string CanvasUrl = "https://canvaz.scdn.co/upload/licensor/video/test.cnvs.mp4";

    [Fact]
    public async Task FetchCanvasAsync_UsesSpotifySessionAndParsesProtobuf()
    {
        var requests = new List<RequestInfo>();
        var handler = new StubHandler(request =>
        {
            requests.Add(RequestInfo.From(request));
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;

            if (host == "raw.githubusercontent.com")
                return JsonResponse("{\"42\":[99,111,47,88,49,56,118,65]}");
            if (host == "open.spotify.com" && path.EndsWith("/server-time", StringComparison.Ordinal))
                return JsonResponse("{\"serverTime\":1700000000}");
            if (host == "open.spotify.com" && path.EndsWith("/token", StringComparison.Ordinal))
                return JsonResponse("{\"accessToken\":\"spotify-token\",\"accessTokenExpirationTimestampMs\":4102444800000}");
            if (host == "api.spotify.com" && path.StartsWith("/v1/search", StringComparison.Ordinal))
            {
                return JsonResponse("{\"tracks\":{\"items\":[{\"id\":\"" + TrackId + "\",\"name\":\"Kill Bill\",\"artists\":[{\"name\":\"SZA\"}]}]}}");
            }
            if (host == "spclient.wg.spotify.com")
                return ProtobufResponse(BuildCanvasResponse(CanvasUrl));

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Equal(5, requests.Count);
        Assert.Contains(requests, request =>
            request.Uri.Host == "open.spotify.com" && request.Cookie == "sp_dc=session-cookie");
        Assert.Contains(requests, request =>
            request.Uri.Host == "spclient.wg.spotify.com" &&
            request.Authorization == "Bearer spotify-token" &&
            request.ContentType == "application/x-www-form-urlencoded");
        Assert.Contains(requests, request =>
            request.Uri.Host == "api.spotify.com" &&
            request.Uri.AbsolutePath.StartsWith("/v1/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FetchCanvasAsync_MissingSessionFallsBackWithoutRequest()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("No request expected"));
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "");

        Assert.Null(result);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public void ParseTrackId_MismatchedSearchResultReturnsNull()
    {
        string? result = SpotifyCanvasService.ParseTrackId(
            "{\"tracks\":{\"items\":[{\"id\":\"" + TrackId +
            "\",\"name\":\"Another Song\",\"artists\":[{\"name\":\"Someone Else\"}]}]}}",
            "Kill Bill",
            "SZA",
            TimeSpan.FromSeconds(153));

        Assert.Null(result);
    }

    [Fact]
    public void ParseCanvasResponse_RejectsUntrustedVideoUrl()
    {
        Uri? result = SpotifyCanvasService.ParseCanvasResponse(
            BuildCanvasResponse("https://example.com/untrusted.mp4"));

        Assert.Null(result);
    }

    [Fact]
    public void BuildCanvasRequest_IncludesSpotifyTrackUri()
    {
        byte[] request = SpotifyCanvasService.BuildCanvasRequest(TrackId);

        Assert.Contains("spotify:track:" + TrackId, Encoding.UTF8.GetString(request), StringComparison.Ordinal);
    }

    private static byte[] BuildCanvasResponse(string canvasUrl)
    {
        byte[] url = Encoding.UTF8.GetBytes(canvasUrl);
        using var canvas = new MemoryStream();
        canvas.WriteByte(0x12); // Canvas.canvas_url, field 2.
        WriteVarint(canvas, (ulong)url.Length);
        canvas.Write(url);

        byte[] canvasBytes = canvas.ToArray();
        using var response = new MemoryStream();
        response.WriteByte(0x0A); // CanvasResponse.canvases, field 1.
        WriteVarint(response, (ulong)canvasBytes.Length);
        response.Write(canvasBytes);
        return response.ToArray();
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ProtobufResponse(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/protobuf");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return Task.FromResult(reply(request));
        }
    }

    private sealed record RequestInfo(Uri Uri, string Method, string? Authorization, string? Cookie, string? ContentType)
    {
        public static RequestInfo From(HttpRequestMessage request) => new(
            request.RequestUri!,
            request.Method.Method,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.SingleOrDefault() : null,
            request.Content?.Headers.ContentType?.MediaType);
    }
}
