using VNotch.Controls;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardCategoryCountTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(9, "9")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(1234, "99+")]
    public void CountLabelCapsDisplayWithoutChangingCount(int count, string expected)
    {
        var category = new ClipboardCategoryViewModel(TrayIconKind.Files) { Count = count };
        Assert.Equal(expected, category.CountLabel);
        Assert.Equal(count, category.Count);
    }

    [Fact]
    public async Task ConcurrentGetCategoryCountsAndGetEntryDuringMutationsSucceedWithoutExceptions()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-cat-concurrency-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(root);
        try
        {
            using var store = new VNotch.Services.Clipboard.ClipboardHistoryStore(root);
            await store.InitializeAsync();

            var seed = (await store.ImportAsync(new VNotch.Models.ClipboardCapture { Text = "seed-item" })).Entry!;
            using var cts = new CancellationTokenSource();

            Task reader = Task.Run(() =>
            {
                while (!cts.IsCancellationRequested)
                {
                    var counts = store.GetCategoryCounts();
                    _ = counts.TryGetValue("All", out _);
                    _ = store.GetEntry(seed.Id);
                    _ = store.Snapshot();
                }
            });

            for (int i = 0; i < 40; i++)
            {
                var added = (await store.ImportAsync(new VNotch.Models.ClipboardCapture { Text = $"item-{i}" })).Entry!;
                _ = store.GetEntry(added.Id);
                if (i % 2 == 0)
                    await store.DeleteAsync(added.Id);
            }

            cts.Cancel();
            await reader;

            var finalCounts = store.GetCategoryCounts();
            Assert.Equal(21, finalCounts["All"]);
            Assert.Equal(21, store.Snapshot().Count);
            Assert.NotNull(store.GetEntry(seed.Id));
        }
        finally
        {
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
        }
    }

    [Fact]
    public void AboveLimitDoesNotAnimateUnchangedLabelAndDroppingBelowUpdatesIt()
    {
        var category = new ClipboardCategoryViewModel(TrayIconKind.Files) { Count = 100 };
        int updates = 0;
        category.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(category.CountLabel)) updates++; };
        category.Count = 101;
        Assert.Equal(0, updates);
        category.Count = 99;
        Assert.Equal(1, updates);
        Assert.Equal("99", category.CountLabel);
        category.Count = 100;
        Assert.Equal(2, updates);
        Assert.Equal("99+", category.CountLabel);
    }
}
