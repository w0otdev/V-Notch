using System.IO;
using System.Text.Json;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardCategoryOrderTests
{
    [Fact]
    public async Task DefaultOrderKeepsTypeTabsAndExistingLabelCatalogPositions()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        var label = await store.AddLabelAsync("Project");
        await store.MoveLabelAsync(label.Id, -int.MaxValue);
        var labels = store.GetLabels().Select(item => item.Id).ToList();
        labels.Insert(labels.IndexOf("Important"), "Personal");
        Assert.Equal(new[] { "All", "Pin", "Links", "Text", "Code", "Images", "Colors", "Files" }
            .Concat(labels).Append("Archive"), store.GetCategoryOrder());
    }

    [Fact]
    public async Task AllTypesOfTagsCanMoveFirstAndLastAndSurviveRestart()
    {
        using var fixture = new Fixture();
        string[] expected;
        string[] labelIds;
        using (var store = new ClipboardHistoryStore(fixture.Root))
        {
            await store.InitializeAsync();
            var custom = await store.AddLabelAsync("Project");
            labelIds = store.GetLabels().Select(label => label.Id).ToArray();
            foreach (string id in new[] { "All", "Pin", "Files", "Personal", "Archive", "Work", custom.Id })
            {
                await store.ReorderCategoryAsync(id, null);
                Assert.Equal(id, store.GetCategoryOrder()[^1]);
                string first = store.GetCategoryOrder()[0];
                await store.ReorderCategoryAsync(id, first);
                Assert.Equal(id, store.GetCategoryOrder()[0]);
            }
            expected = store.GetCategoryOrder().ToArray();
            Assert.Equal(labelIds, store.GetLabels().Select(label => label.Id));
        }
        using var reopened = new ClipboardHistoryStore(fixture.Root);
        await reopened.InitializeAsync();
        Assert.Equal(expected, reopened.GetCategoryOrder());
        Assert.Equal(labelIds, reopened.GetLabels().Select(label => label.Id));
    }

    [Fact]
    public async Task NewLabelsAppendWithoutChangingAnExistingArrangement()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        await store.ReorderCategoryAsync("Archive", "All");
        var before = store.GetCategoryOrder().ToArray();
        var custom = await store.AddLabelAsync("Later project");
        Assert.Equal(before.Append(custom.Id), store.GetCategoryOrder());
        await store.ReorderCategoryAsync(custom.Id, "Text");
        Assert.Equal(custom.Id, store.GetCategoryOrder()[store.GetCategoryOrder().ToList().IndexOf("Text") - 1]);
    }

    [Fact]
    public async Task UnknownAndUnchangedMovesDoNotWriteOrPublish()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        int notifications = 0;
        store.Changed += () => notifications++;
        var before = store.GetCategoryOrder().ToArray();
        await store.ReorderCategoryAsync("unknown", "All");
        await store.ReorderCategoryAsync("All", "unknown");
        await store.ReorderCategoryAsync("All", "All");
        await store.ReorderCategoryAsync("All", "Pin");
        await store.ReorderCategoryAsync("Archive", null);
        Assert.Equal(before, store.GetCategoryOrder());
        Assert.Equal(0, notifications);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "category-order.json")));
        await store.ReorderCategoryAsync("All", null);
        Assert.Equal(1, notifications);
        Assert.Equal(store.GetCategoryOrder(), JsonSerializer.Deserialize<string[]>(
            await File.ReadAllBytesAsync(Path.Combine(fixture.Root, "category-order.json"))));
    }

    [Fact]
    public async Task SavedOrderDropsStaleAndDuplicateIdsAndRestoresMissingTags()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "category-order.json"),
            "[\"files\",\"Files\",null,\"unknown\",\"label:00000000000000000000000000000000\",\"All\"]");
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        var order = store.GetCategoryOrder();
        Assert.Equal(new[] { "Files", "All", "Pin" }, order.Take(3));
        Assert.Equal(16, order.Count);
        Assert.Equal(order.Count, order.Distinct().Count());
        Assert.Contains("Personal", order);
        Assert.Equal("Archive", order[^1]);
    }

    [Fact]
    public async Task UnreadableOrderFallsBackWithoutOverwritingTheFile()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Root);
        string path = Path.Combine(fixture.Root, "category-order.json");
        await File.WriteAllTextAsync(path, "{ malformed");
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        Assert.Equal("All", store.GetCategoryOrder()[0]);
        Assert.Equal(16, store.GetCategoryOrder().Count);
        Assert.Equal("{ malformed", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task QueuedMovesComposeAndSnapshotsCannotMutateTheStoredOrder()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        await Task.WhenAll(store.ReorderCategoryAsync("Images", "All"),
            store.ReorderCategoryAsync("Text", "Images"), store.ReorderCategoryAsync("Images", null));
        Assert.Equal("Text", store.GetCategoryOrder()[0]);
        Assert.Equal("Images", store.GetCategoryOrder()[^1]);
        ((string[])store.GetCategoryOrder())[0] = "corrupt";
        Assert.Equal("Text", store.GetCategoryOrder()[0]);
    }

    [Fact]
    public async Task FailedWriteDoesNotPublishOrChangeTheInMemoryOrder()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "category-order.json"));
        int notifications = 0;
        store.Changed += () => notifications++;
        var before = store.GetCategoryOrder().ToArray();
        var error = await Record.ExceptionAsync(() => store.ReorderCategoryAsync("Images", "All"));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal(before, store.GetCategoryOrder());
        Assert.Equal(0, notifications);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-category-order-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
