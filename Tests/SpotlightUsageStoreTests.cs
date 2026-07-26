using System.IO;
using VNotch.Models;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightUsageStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;
    private DateTime _now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    public SpotlightUsageStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("vnotch-usage-").FullName;
        _path = Path.Combine(_directory, "usage.json");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private SpotlightUsageStore CreateStore() => new(_path, () => _now);

    private static SpotlightSearchItem Item(string id, string target,
        SpotlightResultKind kind = SpotlightResultKind.Application) =>
        new(id, kind, $"Title {id}", "subtitle", target);

    [Fact]
    public void GetBoost_IsZeroForUnknownItemsAndGrowsWithUse()
    {
        var store = CreateStore();
        var item = Item("app:one", "shell:AppsFolder\\One!App");

        Assert.Equal(0, store.GetBoost("app:one"));

        store.RecordLaunch(item);
        double once = store.GetBoost("app:one");
        store.RecordLaunch(item);
        double twice = store.GetBoost("app:one");

        Assert.True(once > 0);
        Assert.True(twice > once);
    }

    [Fact]
    public void GetBoost_IsCappedAndDecaysWithAge()
    {
        var store = CreateStore();
        var item = Item("app:one", "shell:AppsFolder\\One!App");
        for (int i = 0; i < 10_000; i++) store.RecordLaunch(item);

        double fresh = store.GetBoost("app:one");
        // The cap keeps habitual items below a full ranking tier (tiers are
        // spaced 50-100 apart in SpotlightRanker).
        Assert.InRange(fresh, 1, 125);

        _now = _now.AddDays(45);
        double aged = store.GetBoost("app:one");
        Assert.True(aged < fresh);
    }

    [Fact]
    public void GetRecentItems_ReturnsNewestFirstDropsStaleTargetsAndHonorsLimit()
    {
        string liveFile = Path.Combine(_directory, "live.txt");
        File.WriteAllText(liveFile, "x");
        var store = CreateStore();

        store.RecordLaunch(Item("file:gone", Path.Combine(_directory, "missing.txt"), SpotlightResultKind.File));
        _now = _now.AddMinutes(1);
        store.RecordLaunch(Item("file:live", liveFile, SpotlightResultKind.File));
        _now = _now.AddMinutes(1);
        store.RecordLaunch(Item("app:one", "shell:AppsFolder\\One!App"));

        var recents = store.GetRecentItems(10);

        Assert.Equal(["app:one", "file:live"], recents.Select(item => item.Id));
        Assert.All(recents, item => Assert.True(item.IsRecent));

        Assert.Single(store.GetRecentItems(1));
        Assert.Empty(store.GetRecentItems(0));
    }

    [Fact]
    public void RecordLaunch_PersistsAcrossInstances()
    {
        var store = CreateStore();
        store.RecordLaunch(Item("app:one", "shell:AppsFolder\\One!App"));

        // Saves are fire-and-forget; wait for the file to land.
        Assert.True(SpinWait.SpinUntil(() => File.Exists(_path), TimeSpan.FromSeconds(5)));

        var reloaded = CreateStore();
        Assert.True(reloaded.GetBoost("app:one") > 0);
    }

    [Fact]
    public void CorruptStoreFileIsTreatedAsEmpty()
    {
        File.WriteAllText(_path, "{not json");
        var store = CreateStore();

        Assert.Equal(0, store.GetBoost("app:one"));
        Assert.Empty(store.GetRecentItems(5));
    }

    [Fact]
    public void RecordLaunch_IgnoresCalculationsAndTrimsToCapacity()
    {
        var store = CreateStore();
        store.RecordLaunch(Item("calc:1+1", "2", SpotlightResultKind.Calculation));
        Assert.Equal(0, store.GetBoost("calc:1+1"));

        for (int i = 0; i < 150; i++)
        {
            store.RecordLaunch(Item($"app:{i}", $"shell:AppsFolder\\App{i}!App"));
            _now = _now.AddSeconds(1);
        }

        // The oldest entries fall off; the newest survive.
        Assert.Equal(0, store.GetBoost("app:0"));
        Assert.True(store.GetBoost("app:149") > 0);
    }
}
