using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using VNotch.Models;
using VNotch.Services.Spotlight;
using VNotch.Services.Spotlight.Providers;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection(SpotlightWindowAnimationCollection.Name)]
public sealed class SpotlightIconHydrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _usagePath;

    public SpotlightIconHydrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "SpotlightIconHydrationTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _usagePath = Path.Combine(_tempDir, "spotlight-usage.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void SpotlightSearchItem_RaisesPropertyChanged_WhenIconIsSet()
    {
        var item = new SpotlightSearchItem("test:1", SpotlightResultKind.Application, "Test App", "App", @"C:\test.exe");
        string? changedProperty = null;

        item.PropertyChanged += (s, e) =>
        {
            changedProperty = e.PropertyName;
        };

        var bitmap = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, new byte[4], 4);
        bitmap.Freeze();

        item.Icon = bitmap;

        Assert.Equal(nameof(SpotlightSearchItem.Icon), changedProperty);
        Assert.Same(bitmap, item.Icon);
    }

    [Fact]
    public void SpotlightSearchService_Merge_ReturnsResultsWithoutBlockingOnIcons()
    {
        var item = new SpotlightSearchItem("app:notepad", SpotlightResultKind.Application, "Notepad", "App", @"C:\Windows\notepad.exe", @"C:\Windows\notepad.exe")
        {
            Score = 100
        };

        var merged = SpotlightSearchService.Merge(new[] { new[] { item } }, 10);

        Assert.Single(merged);
        Assert.Equal("Notepad", merged[0].Title);
        // Merge should return immediately without eager icon loading
        Assert.Null(merged[0].Icon);
    }

    [Fact]
    public async Task SpotlightViewModel_PreservesSelection_WhenResultsArePublished()
    {
        var item1 = new SpotlightSearchItem("app:1", SpotlightResultKind.Application, "App One", "App", @"C:\app1.exe") { Score = 10 };
        var item2 = new SpotlightSearchItem("app:2", SpotlightResultKind.Application, "App Two", "App", @"C:\app2.exe") { Score = 5 };

        var provider = new TestProvider(item1, item2);
        var service = new SpotlightSearchService(new[] { provider });
        var usage = new SpotlightUsageStore(_usagePath, () => DateTime.UtcNow);
        using var vm = new SpotlightViewModel(service, usage);

        await vm.SearchAsync("app");

        Assert.Equal(2, vm.Results.Count);
        Assert.Equal("app:1", vm.SelectedResult?.Id);

        // Select the second item
        vm.SelectedResult = vm.Results[1];
        Assert.Equal("app:2", vm.SelectedResult.Id);

        // Simulate icon update on item 2
        var bitmap = BitmapSource.Create(1, 1, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, new byte[4], 4);
        bitmap.Freeze();
        vm.Results[1].Icon = bitmap;

        // Selection must remain on item 2
        Assert.Equal("app:2", vm.SelectedResult.Id);
        Assert.Same(bitmap, vm.Results[1].Icon);
    }

    private sealed class TestProvider : ISpotlightProvider
    {
        private readonly SpotlightSearchItem[] _items;
        public bool IsAvailable => true;
        public bool IsInstant => true;

        public TestProvider(params SpotlightSearchItem[] items)
        {
            _items = items;
        }

        public Task<IReadOnlyList<SpotlightSearchItem>> SearchAsync(string query, int limit, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SpotlightSearchItem>>(_items);
        }
    }
}
