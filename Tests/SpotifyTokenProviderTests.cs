using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyTokenProviderTests
{
    [Fact]
    public async Task EqualCookieContentsReuseTheTokenAndChangesOrClearRefreshIt()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var provider = new SpotifyTokenProvider(http);
        Assert.Equal("token-1", await provider.GetAccessTokenAsync("fixture-cookie", default));
        Assert.Equal("token-1", await provider.GetAccessTokenAsync(new string("fixture-cookie".ToCharArray()), default));
        Assert.Equal(1, handler.TokenRequests);
        Assert.Equal("token-2", await provider.GetAccessTokenAsync("Fixture-cookie", default));
        provider.ClearCache();
        Assert.Equal("token-3", await provider.GetAccessTokenAsync("Fixture-cookie", default));
        Assert.Equal(3, handler.TokenRequests);
    }

    [Fact]
    public async Task TokensNearExpiryAreRefreshed()
    {
        using var handler = new Handler { Lifetime = TimeSpan.FromSeconds(30) };
        using var http = new HttpClient(handler);
        using var provider = new SpotifyTokenProvider(http);
        Assert.Equal("token-1", await provider.GetAccessTokenAsync("fixture-cookie", default));
        Assert.Equal("token-2", await provider.GetAccessTokenAsync("fixture-cookie", default));
    }

    [Fact]
    public async Task CachedCookieComparisonsStayWithinTheTaskAllocationBudget()
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler);
        using var provider = new SpotifyTokenProvider(http);
        string cookie = new('a', 1000);
        string sameCookie = new(cookie.ToCharArray());
        await provider.GetAccessTokenAsync(cookie, default);
        for (int i = 0; i < 100; i++) await provider.GetAccessTokenAsync(sameCookie, default);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) await provider.GetAccessTokenAsync(sameCookie, default);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 128_000);
        Assert.Equal(1, handler.TokenRequests);
    }

    [Fact]
    public void FixedSecretMatchesVersion61AndCopiesCannotChangeTheProtocol()
    {
        byte[] encoded = [44, 55, 47, 42, 70, 40, 34, 114, 76, 74, 50, 111, 120, 97, 75, 76, 94, 102, 43, 69, 49, 120, 118, 80, 64, 78];
        string expected = string.Concat(encoded.Select((value, index) => (value ^ ((index % 33) + 9)).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(expected, Encoding.UTF8.GetString(SpotifyWebPlayerProtocol.TotpSecret));
        var copy = SpotifyWebPlayerProtocol.CreateTotpSecret();
        Array.Clear(copy);
        Assert.Equal(expected, Encoding.UTF8.GetString(SpotifyWebPlayerProtocol.TotpSecret));
    }

    private sealed class Handler : HttpMessageHandler
    {
        internal int TokenRequests { get; private set; }
        internal TimeSpan Lifetime { get; init; } = TimeSpan.FromMinutes(30);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload = request.RequestUri!.AbsolutePath == "/api/server-time"
                ? new { serverTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }
                : (object)new { accessToken = "token-" + ++TokenRequests, accessTokenExpirationTimestampMs = (DateTimeOffset.UtcNow + Lifetime).ToUnixTimeMilliseconds() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) });
        }
    }
}
