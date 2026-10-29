using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardTrayLoadTests
{
    [Fact]
    public void SelectAllIncludesTwentyThousandUnrealizedFilesAndPendingNewResults() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var entries = Enumerable.Range(0, 20_000).Select(index => new ClipboardEntry
        { Title = $"Project document {index}.pdf", Kind = ClipboardKind.File }).ToArray();
        Invoke(tray, "ReconcilePreparedCards", (object)entries, null);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ClipboardCardListBox)tray.FindName("Cards");
            int changes = 0;
            list.SelectionChanged += (_, _) => changes++;
            Assert.True((bool)Invoke(tray, "SelectAllTrayItems", new object?[] { null })!);
            Assert.Equal(entries.Length, list.SelectedItems.Count);
            Assert.Equal(1, changes);
            Assert.InRange(Field<HashSet<ListBoxItem>>(tray, "_selectionContainers").Count, 1, 32);
            Assert.Empty(Field<System.Collections.IDictionary>(tray, "_thumbnailLoads"));

            var latest = entries.Append(new ClipboardEntry { Kind = ClipboardKind.File, Title = "New arrival.pdf" }).ToArray();
            Invoke(tray, "ReconcilePreparedCards", (object)latest, null);
            SetField(tray, "_pendingSelectAll", ("All", ""));
            Invoke(tray, "CompletePendingSelectAll");
            Assert.Equal(latest.Length, list.SelectedItems.Count);
            Assert.Contains(list.SelectedItems.Cast<ClipboardCardViewModel>(), card => card.Entry.Id == latest[^1].Id);
            Assert.Null(Field<object?>(tray, "_pendingSelectAll"));
            Assert.Empty(Field<System.Collections.IDictionary>(tray, "_thumbnailLoads"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void StoreNotificationBurstQueuesOneCallbackAndRefreshesTheLatestState() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = System.IO.Directory.CreateTempSubdirectory("vnotch-notification-burst-").FullName;
        try
        {
            using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
            using var tray = new ClipboardTray();
            tray.Attach(controller);
            var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Field<UiScheduledTimer>(tray, "_searchTimer").Stop();
                SetField(tray, "_queryDirty", false);
                var entries = Field<ConcurrentDictionary<Guid, ClipboardEntry>>(store, "_entries");
                for (int i = 0; i < 3; i++)
                {
                    var entry = new ClipboardEntry { Kind = ClipboardKind.File, Title = $"Latest {i}.pdf" };
                    entries[entry.Id] = entry;
                }
                int queued = CountBurst(tray, 10_000);
                Assert.Equal(1, queued);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, Field<int>(tray, "_storeRefreshQueued"));
                Field<UiScheduledTimer>(tray, "_searchTimer").Stop();
                await (Task)Invoke(tray, "RefreshAsync")!;
                var list = (ListBox)tray.FindName("Cards");
                Assert.Equal(3, list.Items.Count);
                Assert.Equal(3, Field<Dictionary<string, int>>(tray, "_categoryCounts")["Files"]);

                var next = new ClipboardEntry { Kind = ClipboardKind.File, Title = "After the burst.pdf" };
                entries[next.Id] = next;
                Assert.Equal(1, CountBurst(tray, 1));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Field<UiScheduledTimer>(tray, "_searchTimer").Stop();
                await (Task)Invoke(tray, "RefreshAsync")!;
                Assert.Equal(4, list.Items.Count);
                tray.Dispose();
                Assert.Equal(0, CountBurst(tray, 10_000));
                Assert.Empty(list.Items);
            }
            finally { window.Close(); }
        }
        finally { System.IO.Directory.Delete(root, true); }
    });

    [Fact]
    public void NotificationQueuedBeforeDisposeDoesNotRestartTrayWork() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        Assert.Equal(1, CountBurst(tray, 10_000));
        tray.Dispose();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(0, Field<int>(tray, "_storeRefreshQueued"));
        Assert.False(Field<UiScheduledTimer>(tray, "_searchTimer").IsEnabled);
        Assert.Empty(((ListBox)tray.FindName("Cards")).Items);
        Assert.Equal(0, CountBurst(tray, 10_000));
    });

    private static int CountBurst(ClipboardTray tray, int count)
    {
        var changed = typeof(ClipboardTray).GetMethod("StoreChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<Action>(tray);
        int queued = 0;
        void Posted(object? sender, DispatcherHookEventArgs args)
        { if (args.Operation.Priority == DispatcherPriority.Background) queued++; }
        tray.Dispatcher.Hooks.OperationPosted += Posted;
        try { for (int i = 0; i < count; i++) changed(); }
        finally { tray.Dispatcher.Hooks.OperationPosted -= Posted; }
        return queued;
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
