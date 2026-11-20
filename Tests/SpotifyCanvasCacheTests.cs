using System.Collections;
using System.Net.Http;
using System.Reflection;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyCanvasCacheTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Uri Canvas = new("https://canvaz.scdn.co/test.mp4");
    private static readonly MethodInfo Store = typeof(SpotifyCanvasService).GetMethod("CacheCanvas", Private)!;

    [Fact]
    public void ReplacingAnEntryAtCapacityKeepsOtherEntriesAndOverflowEvictsTheOldest()
    {
        using var http = new HttpClient();
        using var service = new SpotifyCanvasService(http);
        for (int i = 0; i < 128; i++) Put(service, $"key{i}");
        IDictionary cache = Cache(service);
        Put(service, "key127");
        Assert.Equal(128, cache.Count);
        Assert.True(cache.Contains("key0"));
        Put(service, "overflow");
        Assert.Equal(128, cache.Count);
        Assert.False(cache.Contains("key0"));
        Assert.True(cache.Contains("overflow"));
        service.ClearCache();
        Assert.Empty(cache);
    }

    [Fact]
    public async Task ParallelStoresKeepTheCacheBounded()
    {
        using var http = new HttpClient();
        using var service = new SpotifyCanvasService(http);
        await Task.WhenAll(Enumerable.Range(0, 256).Select(i => Task.Run(() => Put(service, $"key{i}"))));
        Put(service, "latest");
        IDictionary cache = Cache(service);
        Assert.Equal(128, cache.Count);
        Assert.True(cache.Contains("latest"));
    }

    [Fact]
    public void ExpiredEntriesAreRemovedBeforeEvictingLiveEntries()
    {
        using var http = new HttpClient();
        using var service = new SpotifyCanvasService(http);
        for (int i = 0; i < 128; i++) Put(service, $"key{i}");
        IDictionary cache = Cache(service);
        Type entry = typeof(SpotifyCanvasService).GetNestedType("CacheEntry", BindingFlags.NonPublic)!;
        cache["key127"] = Activator.CreateInstance(entry, Canvas, DateTimeOffset.UtcNow.AddMinutes(-1));
        Put(service, "latest");
        Assert.Equal(128, cache.Count);
        Assert.True(cache.Contains("key0"));
        Assert.False(cache.Contains("key127"));
    }

    [Fact]
    public async Task EvictedLocalFileIsNotReturnedFromMemory()
    {
        using var directory = new TempFolder();
        using var http = new HttpClient(new OfflineHandler());
        using var service = new SpotifyCanvasService(http);
        var uri = new Uri(System.IO.Path.Combine(directory.Path, "canvas.mp4"));
        System.IO.File.WriteAllBytes(uri.LocalPath, [1]);
        Store.Invoke(service, ["track|artist|5", uri]);
        Task<Uri?> Fetch() => service.FetchCanvasAsync("track", "artist", TimeSpan.FromSeconds(5), "cookie");
        Assert.Equal(uri, await Fetch());
        System.IO.File.Delete(uri.LocalPath);
        Assert.Null(await Fetch());
        Assert.Empty(Cache(service));
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
    }

    private static void Put(SpotifyCanvasService service, string key) => Store.Invoke(service, [key, Canvas]);
    private static IDictionary Cache(SpotifyCanvasService service) => (IDictionary)typeof(SpotifyCanvasService).GetField("_cache", Private)!.GetValue(service)!;
}
