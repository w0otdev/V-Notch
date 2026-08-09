using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyCanvasServiceTests
{
    private const string TrackId = "3OHfY25tqY28d16oZczHc8";
    private const string CanvasUrl = "https://canvaz.scdn.co/upload/licensor/video/test.cnvs.mp4";
    private const string DefaultFindTracksHash = "903df2a65d8121e27d73a2be03c01e88ebe6021bb6d4eb82a389e35d87e51d27";
    private const string DefaultCanvasHash = "575138ab27cd5c1b3e54da54d0a7cc8d85485402de26340c2145f0f6bb5e7a9f";

    [Fact]
    public async Task FetchCanvasAsync_UsesPathfinderCanvasAsPrimary()
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
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal))
                return PathfinderResponse();
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal))
                return CanvasPathfinderResponse(CanvasUrl);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Equal(5, requests.Count);
        Assert.Contains(requests, request =>
            request.Uri.Host == "open.spotify.com" && request.Cookie == "sp_dc=session-cookie");
        RequestInfo pathfinderRequest = Assert.Single(requests.Where(request =>
            request.Uri.Host == "api-partner.spotify.com" &&
            request.Uri.AbsolutePath.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal)));
        Assert.Equal("POST", pathfinderRequest.Method);
        Assert.Equal("Bearer spotify-token", pathfinderRequest.Authorization);
        Assert.Equal("application/json", pathfinderRequest.ContentType);
        using (var payload = JsonDocument.Parse(pathfinderRequest.Body!))
        {
            JsonElement root = payload.RootElement;
            Assert.Equal("findTracks", root.GetProperty("operationName").GetString());
            Assert.Equal("Kill Bill SZA", root.GetProperty("variables").GetProperty("query").GetString());
            Assert.Equal(5, root.GetProperty("variables").GetProperty("limit").GetInt32());
            Assert.Equal(0, root.GetProperty("variables").GetProperty("offset").GetInt32());
        }
        RequestInfo canvasRequest = Assert.Single(requests.Where(request =>
            request.Uri.Host == "api-partner.spotify.com" &&
            request.Uri.AbsolutePath.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal)));
        Assert.Equal("GET", canvasRequest.Method);
        Assert.Equal("Bearer spotify-token", canvasRequest.Authorization);
        Assert.Equal("WebPlayer", canvasRequest.AppPlatform);
        Assert.Equal("canvas", GetQueryParameter(canvasRequest.Uri, "operationName"));
        using (var variables = JsonDocument.Parse(GetQueryParameter(canvasRequest.Uri, "variables")))
            Assert.Equal("spotify:track:" + TrackId, variables.RootElement.GetProperty("trackUri").GetString());
        Assert.Equal(DefaultCanvasHash, GetPersistedQueryHashFromUri(canvasRequest.Uri));
        Assert.DoesNotContain(requests, request => request.Uri.Host == "spclient.wg.spotify.com");
        Assert.DoesNotContain(requests, request => request.Uri.Host == "apic-desktop.musixmatch.com");
    }

    [Fact]
    public async Task FetchCanvasAsync_SpotifyCatalogUnavailable_FallsBackToMusixmatch()
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
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal))
                return CanvasPathfinderResponse(CanvasUrl);
            if (host == "apic-desktop.musixmatch.com" && path.EndsWith("/token.get", StringComparison.Ordinal))
                return JsonResponse("{\"message\":{\"body\":{\"user_token\":\"musixmatch-token\"}}}");
            if (host == "apic-desktop.musixmatch.com" && path.EndsWith("/macro.subtitles.get", StringComparison.Ordinal))
            {
                return JsonResponse("{\"message\":{\"body\":{\"macro_calls\":{\"matcher.track.get\":{\"message\":{\"body\":{\"track\":{" +
                    "\"track_spotify_id\":\"" + TrackId + "\",\"track_name\":\"Kill Bill\"," +
                    "\"artist_name\":\"SZA\",\"track_length\":153}}}}}}}}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Equal(7, requests.Count);
        Assert.Contains(requests, request => request.Uri.Host == "api-partner.spotify.com");
        Assert.Contains(requests, request =>
            request.Uri.Host == "apic-desktop.musixmatch.com" &&
            request.Uri.AbsolutePath.EndsWith("/macro.subtitles.get", StringComparison.Ordinal));
        Assert.Contains(requests, request =>
            request.Uri.Host == "api-partner.spotify.com" &&
            request.Uri.AbsolutePath.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal));
        Assert.DoesNotContain(requests, request => request.Uri.Host == "spclient.wg.spotify.com");
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(412)]
    public async Task FetchCanvasAsync_StalePathfinderHash_RefreshesMetadataAndRetries(int statusCode)
    {
        int pathfinderRequests = 0;
        int metadataPageRequests = 0;
        int metadataScriptRequests = 0;
        var pathfinderPayloads = new List<string>();
        var handler = new StubHandler(request =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;

            if (host == "raw.githubusercontent.com")
                return JsonResponse("{\"42\":[99,111,47,88,49,56,118,65]}");
            if (host == "open.spotify.com" && path.EndsWith("/server-time", StringComparison.Ordinal))
                return JsonResponse("{\"serverTime\":1700000000}");
            if (host == "open.spotify.com" && path.EndsWith("/token", StringComparison.Ordinal))
                return JsonResponse("{\"accessToken\":\"spotify-token\",\"accessTokenExpirationTimestampMs\":4102444800000}");
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal))
            {
                pathfinderRequests++;
                pathfinderPayloads.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return pathfinderRequests == 1
                    ? new HttpResponseMessage((HttpStatusCode)statusCode)
                    : PathfinderResponse();
            }
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal))
                return CanvasPathfinderResponse(CanvasUrl);
            if (host == "open.spotify.com" && path == "/")
            {
                metadataPageRequests++;
                return JsonResponse(
                    "<script src=\"https://open.spotifycdn.com/cdn/build/mobile-web-player/mobile-web-player.abcdef12.js\"></script>");
            }
            if (host == "open.spotifycdn.com")
            {
                metadataScriptRequests++;
                return JsonResponse(
                    "new Query( 'findTracks' , 'query' , 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' )");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Equal(2, pathfinderRequests);
        Assert.Equal(1, metadataPageRequests);
        Assert.Equal(1, metadataScriptRequests);
        Assert.Equal(DefaultFindTracksHash, GetPersistedQueryHash(pathfinderPayloads[0]));
        Assert.Equal(new string('a', 64), GetPersistedQueryHash(pathfinderPayloads[1]));
    }

    [Fact]
    public async Task FetchCanvasAsync_RefreshedPathfinderHashIsRejected_DoesNotRetryAgain()
    {
        int pathfinderRequests = 0;
        int metadataPageRequests = 0;
        int metadataScriptRequests = 0;
        var handler = new StubHandler(request =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;

            if (host == "raw.githubusercontent.com")
                return JsonResponse("{\"42\":[99,111,47,88,49,56,118,65]}");
            if (host == "open.spotify.com" && path.EndsWith("/server-time", StringComparison.Ordinal))
                return JsonResponse("{\"serverTime\":1700000000}");
            if (host == "open.spotify.com" && path.EndsWith("/token", StringComparison.Ordinal))
                return JsonResponse("{\"accessToken\":\"spotify-token\",\"accessTokenExpirationTimestampMs\":4102444800000}");
            if (host == "api-partner.spotify.com")
            {
                pathfinderRequests++;
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }
            if (host == "open.spotify.com" && path == "/")
            {
                metadataPageRequests++;
                return JsonResponse(
                    "<script src=\"https://open.spotifycdn.com/cdn/build/mobile-web-player/mobile-web-player.abcdef12.js\"></script>");
            }
            if (host == "open.spotifycdn.com")
            {
                metadataScriptRequests++;
                return JsonResponse(
                    "new Query(\"findTracks\",\"query\",\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\")");
            }
            if (host == "apic-desktop.musixmatch.com")
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Null(result);
        Assert.Equal(2, pathfinderRequests);
        Assert.Equal(1, metadataPageRequests);
        Assert.Equal(1, metadataScriptRequests);
    }

    [Fact]
    public async Task FetchCanvasAsync_PathfinderCanvasNull_DoesNotCallLegacyEndpoint()
    {
        var requests = new List<RequestInfo>();
        var handler = new StubHandler(request =>
        {
            requests.Add(RequestInfo.From(request));
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            HttpResponseMessage? bootstrap = BootstrapResponse(host, path);
            if (bootstrap != null)
                return bootstrap;
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal))
                return PathfinderResponse();
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal))
                return CanvasPathfinderResponse(null);
            if (host == "spclient.wg.spotify.com")
                return ProtobufResponse(BuildCanvasResponse(CanvasUrl));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Null(result);
        Assert.Single(requests.Where(request =>
            request.Uri.Host == "api-partner.spotify.com" &&
            request.Uri.AbsolutePath.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal)));
        Assert.DoesNotContain(requests, request => request.Uri.Host == "spclient.wg.spotify.com");
    }

    [Fact]
    public async Task FetchCanvasAsync_PathfinderCanvasTransportUnavailable_FallsBackToLegacyEndpoint()
    {
        var requests = new List<RequestInfo>();
        var handler = new StubHandler(request =>
        {
            requests.Add(RequestInfo.From(request));
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            HttpResponseMessage? bootstrap = BootstrapResponse(host, path);
            if (bootstrap != null)
                return bootstrap;
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal))
                return PathfinderResponse();
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            if (host == "spclient.wg.spotify.com")
                return ProtobufResponse(BuildCanvasResponse(CanvasUrl));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Single(requests.Where(request =>
            request.Uri.Host == "api-partner.spotify.com" &&
            request.Uri.AbsolutePath.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal)));
        Assert.Single(requests.Where(request => request.Uri.Host == "spclient.wg.spotify.com"));
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(412, false)]
    [InlineData(200, true)]
    public async Task FetchCanvasAsync_StaleCanvasHash_RefreshesAndRetries(
        int statusCode,
        bool persistedQueryError)
    {
        int canvasRequests = 0;
        int metadataPageRequests = 0;
        int metadataScriptRequests = 0;
        var canvasUris = new List<Uri>();
        var handler = new StubHandler(request =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            HttpResponseMessage? bootstrap = BootstrapResponse(host, path);
            if (bootstrap != null)
                return bootstrap;
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal))
                return PathfinderResponse();
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal))
            {
                canvasRequests++;
                canvasUris.Add(request.RequestUri);
                if (canvasRequests == 1)
                {
                    return persistedQueryError
                        ? JsonResponse("{\"errors\":[{\"message\":\"PersistedQueryNotFound\"}]}")
                        : new HttpResponseMessage((HttpStatusCode)statusCode);
                }
                return CanvasPathfinderResponse(CanvasUrl);
            }
            if (host == "open.spotify.com" && path == "/")
            {
                metadataPageRequests++;
                return JsonResponse(
                    "<script src=\"https://open.spotifycdn.com/cdn/build/web-player/web-player.abcdef12.js\"></script>");
            }
            if (host == "open.spotifycdn.com")
            {
                metadataScriptRequests++;
                return JsonResponse(
                    "new Query( 'canvas' , 'query' , 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' )");
            }
            if (host == "spclient.wg.spotify.com")
                throw new InvalidOperationException("Legacy Canvas endpoint must not be called after a successful retry");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Equal(2, canvasRequests);
        Assert.Equal(1, metadataPageRequests);
        Assert.Equal(1, metadataScriptRequests);
        Assert.Equal(DefaultCanvasHash, GetPersistedQueryHashFromUri(canvasUris[0]));
        Assert.Equal(new string('b', 64), GetPersistedQueryHashFromUri(canvasUris[1]));
    }

    [Fact]
    public async Task FetchCanvasAsync_RefreshedCanvasHashRejected_RetriesOnceThenFallsBackToLegacy()
    {
        int canvasRequests = 0;
        int metadataPageRequests = 0;
        int metadataScriptRequests = 0;
        int legacyRequests = 0;
        var handler = new StubHandler(request =>
        {
            string host = request.RequestUri!.Host;
            string path = request.RequestUri.AbsolutePath;
            HttpResponseMessage? bootstrap = BootstrapResponse(host, path);
            if (bootstrap != null)
                return bootstrap;
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v2/query", StringComparison.Ordinal))
                return PathfinderResponse();
            if (host == "api-partner.spotify.com" && path.EndsWith("/pathfinder/v1/query", StringComparison.Ordinal))
            {
                canvasRequests++;
                return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
            }
            if (host == "open.spotify.com" && path == "/")
            {
                metadataPageRequests++;
                return JsonResponse(
                    "<script src=\"https://open.spotifycdn.com/cdn/build/web-player/web-player.abcdef12.js\"></script>");
            }
            if (host == "open.spotifycdn.com")
            {
                metadataScriptRequests++;
                return JsonResponse(
                    "new Query(\"canvas\",\"query\",\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\")");
            }
            if (host == "spclient.wg.spotify.com")
            {
                legacyRequests++;
                return ProtobufResponse(BuildCanvasResponse(CanvasUrl));
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var service = new SpotifyCanvasService(new HttpClient(handler));

        Uri? result = await service.FetchCanvasAsync(
            "Kill Bill", "SZA", TimeSpan.FromSeconds(153), "session-cookie");

        Assert.Equal(CanvasUrl, result?.AbsoluteUri);
        Assert.Equal(2, canvasRequests);
        Assert.Equal(1, metadataPageRequests);
        Assert.Equal(1, metadataScriptRequests);
        Assert.Equal(1, legacyRequests);
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
    public void ParsePathfinderTrackId_UsesNestedSchemaAndMatchesAnyArtist()
    {
        const string expectedId = "6oyL4r6wyZs084UIGsMhr8";
        string json = "{\"data\":{\"searchV2\":{\"tracksV2\":{\"items\":[" +
            "{\"item\":{\"data\":{\"__typename\":\"Track\",\"id\":\"7j8Cn6JwRm76xGvnuLpBG0\"," +
            "\"uri\":\"spotify:track:7j8Cn6JwRm76xGvnuLpBG0\",\"name\":\"LOVELY\"," +
            "\"artists\":{\"items\":[{\"profile\":{\"name\":\"Someone Else\"}}]}}}}," +
            "{\"item\":{\"data\":{\"__typename\":\"Track\",\"id\":\"" + expectedId + "\"," +
            "\"uri\":\"spotify:track:" + expectedId + "\",\"name\":\"LOVELY\"," +
            "\"artists\":{\"items\":[{\"profile\":{\"name\":\"VCC Left Hand\"}}," +
            "{\"profile\":{\"name\":\"kidsai\"}}]}}}}]}}}}";

        string? result = SpotifyCanvasService.ParsePathfinderTrackId(json, "LOVELY", "kidsai");

        Assert.Equal(expectedId, result);
    }

    [Fact]
    public void ParsePathfinderTrackId_DoesNotAcceptWrongArtist()
    {
        string? result = SpotifyCanvasService.ParsePathfinderTrackId(
            PathfinderJson(TrackId, "Kill Bill", "Someone Else"),
            "Kill Bill",
            "SZA");

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

    private static HttpResponseMessage PathfinderResponse() =>
        JsonResponse(PathfinderJson(TrackId, "Kill Bill", "SZA"));

    private static HttpResponseMessage CanvasPathfinderResponse(string? canvasUrl) =>
        JsonResponse(canvasUrl == null
            ? "{\"data\":{\"trackUnion\":{\"canvas\":null}}}"
            : "{\"data\":{\"trackUnion\":{\"canvas\":{\"url\":\"" + canvasUrl + "\"}}}}");

    private static HttpResponseMessage? BootstrapResponse(string host, string path)
    {
        if (host == "raw.githubusercontent.com")
            return JsonResponse("{\"42\":[99,111,47,88,49,56,118,65]}");
        if (host == "open.spotify.com" && path.EndsWith("/server-time", StringComparison.Ordinal))
            return JsonResponse("{\"serverTime\":1700000000}");
        if (host == "open.spotify.com" && path.EndsWith("/token", StringComparison.Ordinal))
        {
            return JsonResponse(
                "{\"accessToken\":\"spotify-token\",\"accessTokenExpirationTimestampMs\":4102444800000}");
        }
        return null;
    }

    private static string PathfinderJson(string trackId, string trackName, string artistName) =>
        "{\"data\":{\"searchV2\":{\"tracksV2\":{\"items\":[{\"item\":{\"data\":{" +
        "\"__typename\":\"Track\",\"id\":\"" + trackId + "\",\"uri\":\"spotify:track:" + trackId + "\"," +
        "\"name\":\"" + trackName + "\",\"artists\":{\"items\":[{\"profile\":{\"name\":\"" +
        artistName + "\"}}]}}}}]}}}}";

    private static string GetPersistedQueryHash(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement
            .GetProperty("extensions")
            .GetProperty("persistedQuery")
            .GetProperty("sha256Hash")
            .GetString()!;
    }

    private static string GetPersistedQueryHashFromUri(Uri uri)
    {
        using var document = JsonDocument.Parse(GetQueryParameter(uri, "extensions"));
        return document.RootElement
            .GetProperty("persistedQuery")
            .GetProperty("sha256Hash")
            .GetString()!;
    }

    private static string GetQueryParameter(Uri uri, string name)
    {
        foreach (string component in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = component.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals(name, StringComparison.Ordinal))
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
        }
        throw new InvalidOperationException($"Query parameter '{name}' was not present.");
    }

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

    private sealed record RequestInfo(
        Uri Uri,
        string Method,
        string? Authorization,
        string? Cookie,
        string? ContentType,
        string? AppPlatform,
        string? Body)
    {
        public static RequestInfo From(HttpRequestMessage request) => new(
            request.RequestUri!,
            request.Method.Method,
            request.Headers.Authorization?.ToString(),
            request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.SingleOrDefault() : null,
            request.Content?.Headers.ContentType?.MediaType,
            request.Headers.TryGetValues("app-platform", out var platforms) ? platforms.SingleOrDefault() : null,
            request.Content?.ReadAsStringAsync().GetAwaiter().GetResult());
    }
}
