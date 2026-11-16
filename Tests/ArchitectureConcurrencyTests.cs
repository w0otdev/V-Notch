using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using VNotch.ApplicationServices;
using VNotch.Contracts;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

public sealed class ArchitectureConcurrencyTests
{
    [Fact]
    public async Task ClipboardPagingKeepsStableTiesAndReflectsChanges()
    {
        string root = Directory.CreateTempSubdirectory("vnotch-paging-").FullName;
        try
        {
            await using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            var stamp = DateTime.UtcNow;
            for (int i = 0; i < 37; i++)
                await store.ImportAsync(new ClipboardCapture { Text = $"paging item {i}", CopiedUtc = stamp });
            var ids = new List<Guid>();
            int offset = 0;
            ClipboardSearchPage page;
            do
            {
                page = await store.SearchPageAsync("paging", "All", offset, 7);
                Assert.InRange(page.Entries.Count, 1, 7);
                ids.AddRange(page.Entries.Select(e => e.Id));
                offset = page.NextOffset;
            } while (page.HasMore);
            Assert.Equal(37, ids.Distinct().Count());
            Assert.Equal(37, ids.Count);
            await store.UpdateAsync(ids[0], archived: true);
            Assert.DoesNotContain((await store.SearchPageAsync("paging", "All", 0, 100)).Entries, e => e.Id == ids[0]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClipboardQueriesDoNotWaitForABlockedImportAndDeletionEvictsCachedText()
    {
        string root = Directory.CreateTempSubdirectory("vnotch-query-isolation-").FullName;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool block = false;
        try
        {
            await using var store = new ClipboardHistoryStore(root, () =>
            {
                if (Volatile.Read(ref block))
                {
                    entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                return long.MaxValue;
            });
            await store.InitializeAsync();
            var original = (await store.ImportAsync(new ClipboardCapture { Text = new string('z', 1200) + " searchable-tail" })).Entry!;
            Volatile.Write(ref block, true);
            var import = store.ImportAsync(new ClipboardCapture { Text = "later import" });
            try
            {
                Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
                var page = await store.SearchPageAsync("searchable-tail", "All").WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(original.Id, Assert.Single(page.Entries).Id);
                Assert.False(import.IsCompleted);
            }
            finally { release.Set(); }
            await import;
            var search = store.SearchPageAsync("searchable-tail", "All");
            await store.DeleteAsync(original.Id);
            await search;
            var cache = (ClipboardSearchTextCache)typeof(ClipboardHistoryStore)
                .GetField("_searchText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
            Assert.False(cache.TryGet(original.Id, out _));
            Assert.Empty((await store.SearchPageAsync("searchable-tail", "All")).Entries);
        }
        finally { release.Set(); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ForecastCacheExpiresAndRetryAfterSuppressesRepeatedRequests()
    {
        var clock = new TestClock();
        using var handler = new WeatherHandler();
        using var client = new HttpClient(handler);
        using var weather = new WeatherService(client, clock);
        Assert.NotNull(await weather.GetCurrentWeatherAsync("Hanoi"));
        Assert.NotNull(await weather.GetCurrentWeatherAsync("Hanoi"));
        Assert.Equal(2, handler.Requests);
        clock.Now += TimeSpan.FromMinutes(16);
        handler.RateLimit = true;
        Assert.Null(await weather.GetCurrentWeatherAsync("Hanoi"));
        int rateLimitedCount = handler.Requests;
        Assert.Null(await weather.GetCurrentWeatherAsync("Hanoi"));
        Assert.Equal(rateLimitedCount, handler.Requests);
        clock.Now += TimeSpan.FromMinutes(3);
        handler.RateLimit = false;
        Assert.NotNull(await weather.GetCurrentWeatherAsync("Hanoi"));
        Assert.True(handler.Requests > rateLimitedCount);
    }

    [Fact]
    public void WeatherUsesInjectedSchedulerAndJittersStartupAndPolling()
    {
        var scheduler = new Scheduler();
        using var module = new WeatherModule(new NoWeather(), new FakeSettingsService(new NotchSettings()), scheduler, () => .5);
        module.Start();
        Assert.True(scheduler.Timer.Running);
        Assert.Equal(TimeSpan.FromSeconds(30.5), scheduler.Timer.Interval);
        scheduler.Timer.Fire();
        Assert.Equal(TimeSpan.FromMinutes(16.5), scheduler.Timer.Interval);
        module.Stop();
        Assert.False(scheduler.Timer.Running);
    }

    [Fact]
    public async Task ProviderCapabilitiesDriveFallbackAndSurfacePartialResults()
    {
        var index = new Provider(FileSearchRole.AuthoritativeIndex, SearchCompleteness.Unavailable);
        var fallback = new Provider(FileSearchRole.Fallback, SearchCompleteness.Partial);
        var service = new SpotlightSearchService([index, fallback]);
        SpotlightSearchService.SearchUpdate last = default;
        await foreach (var update in service.SearchUpdatesAsync("needle", 10, default)) last = update;
        Assert.True(last.IsComplete);
        Assert.Equal(SearchCompleteness.Partial, last.FileCompleteness);
        Assert.Equal(1, fallback.Calls);
        index.State = SearchCompleteness.Complete;
        await foreach (var update in service.SearchUpdatesAsync("needle", 10, default)) last = update;
        Assert.Equal(SearchCompleteness.Complete, last.FileCompleteness);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public void CoreAndApplicationCannotReferenceWpfOrDesktopAssembly()
    {
        foreach (var assembly in new[] { typeof(INotchModule).Assembly, typeof(ChatSession).Assembly })
        {
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name =>
                name.Name is "PresentationFramework" or "PresentationCore" or "WindowsBase" or "V-Notch" or "VNotch.Infrastructure.Windows");
            Assert.DoesNotContain(assembly.GetTypes(), type => type.Name == "App");
        }
    }

    private sealed class Provider(FileSearchRole role, SearchCompleteness state) : ISpotlightProvider
    {
        public int Calls { get; private set; }
        public SearchCompleteness State { get; set; } = state;
        public bool IsAvailable => State != SearchCompleteness.Unavailable;
        public FileSearchRole FileRole => role;
        public Task<SearchBatch> SearchBatchAsync(string query, int limit, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new SearchBatch([], State)); }
        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The orchestrator must use the completeness contract.");
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class WeatherHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool RateLimit { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (RateLimit)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return Task.FromResult(response);
            }
            string json = request.RequestUri!.Host == "geocoding-api.open-meteo.com"
                ? "{\"results\":[{\"name\":\"Hanoi\",\"latitude\":21.02,\"longitude\":105.84}]}"
                : "{\"current\":{\"temperature_2m\":30,\"weather_code\":2,\"is_day\":1},\"daily\":{\"temperature_2m_max\":[34],\"temperature_2m_min\":[27]}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
    private sealed class NoWeather : IWeatherService
    {
        public Task<WeatherInfo?> GetCurrentWeatherAsync(string? manualCity = null, CancellationToken cancellationToken = default) => Task.FromResult<WeatherInfo?>(null);
    }
    private sealed class Scheduler : IModuleScheduler
    {
        public Timer Timer { get; } = new();
        public IModuleTimer CreateTimer() => Timer;
    }
    private sealed class Timer : IModuleTimer
    {
        public TimeSpan Interval { get; set; }
        public bool Running { get; private set; }
        public event EventHandler? Tick;
        public void Start() => Running = true;
        public void Stop() => Running = false;
        public void Dispose() => Stop();
        public void Fire() => Tick?.Invoke(this, EventArgs.Empty);
    }
}
