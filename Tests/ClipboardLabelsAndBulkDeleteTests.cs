using System.IO;
using System.Reflection;
using System.Security;
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
public sealed class ClipboardLabelsAndBulkDeleteTests
{
    [Fact]
    public async Task CustomLabelsAndOrderSurviveRestartWithMoreThanFiveLabelsAndItems()
    {
        using var fixture = new Fixture();
        ClipboardLabel[] labels;
        Guid[] entryIds;
        using (var store = new ClipboardHistoryStore(fixture.Root))
        {
            await store.InitializeAsync();
            for (int i = 0; i < 24; i++) await store.AddLabelAsync($"Nhãn dự án {i}");
            labels = store.GetLabels().ToArray();
            var last = labels[^1];
            await store.MoveLabelAsync(last.Id, -int.MaxValue);
            labels = store.GetLabels().ToArray();
            Assert.Equal(last.Id, labels[0].Id);
            Assert.Equal(30, labels.Length);
            var entries = new List<Guid>();
            for (int i = 0; i < 16; i++)
            {
                var entry = (await store.ImportAsync(new ClipboardCapture { Text = $"project note {i}" })).Entry!;
                await store.UpdateAsync(entry.Id, groups: labels.Select(label => label.Id).ToArray());
                entries.Add(entry.Id);
            }
            entryIds = entries.ToArray();
            Assert.Equal(16, store.GetCategoryCounts()[last.Id]);
            Assert.Equal(16, (await store.SearchAsync("", last.Id)).Count);
        }

        using var reopened = new ClipboardHistoryStore(fixture.Root);
        await reopened.InitializeAsync();
        Assert.Equal(labels, reopened.GetLabels());
        Assert.Equal(16, (await reopened.SearchAsync("", labels[0].Id)).Count);
        foreach (var id in entryIds) Assert.Equal(labels.Select(label => label.Id), reopened.GetEntry(id)!.Groups);
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.AddLabelAsync("nhãn dự án 0"));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.AddLabelAsync(" "));
    }

    [Fact]
    public async Task SearchMatchesCustomLabelDisplayNamesAfterRestartAndRespectsMembershipAndArchive()
    {
        using var fixture = new Fixture();
        Guid taggedId;
        using (var store = new ClipboardHistoryStore(fixture.Root))
        {
            await store.InitializeAsync();
            var label = await store.AddLabelAsync("Nhãn dự án");
            taggedId = (await store.ImportAsync(new ClipboardCapture { Text = "ginkgo seed" })).Entry!.Id;
            await store.ImportAsync(new ClipboardCapture { Text = "unrelated content" });
            await store.UpdateAsync(taggedId, groups: [label.Id]);
        }

        using var reopened = new ClipboardHistoryStore(fixture.Root);
        await reopened.InitializeAsync();
        Assert.Equal(taggedId, Assert.Single(await reopened.SearchAsync("DỰ ÁN", "All")).Id);
        Assert.Equal(taggedId, Assert.Single(await reopened.SearchAsync("ginkgo nhãn", "All")).Id);
        Assert.Empty(await reopened.SearchAsync("unknown label", "All"));
        await reopened.UpdateAsync(taggedId, archived: true);
        Assert.Empty(await reopened.SearchAsync("dự", "All"));
        Assert.Equal(taggedId, Assert.Single(await reopened.SearchAsync("dự", "Archive")).Id);
        await reopened.UpdateAsync(taggedId, groups: []);
        Assert.Empty(await reopened.SearchAsync("dự", "Archive"));
    }

    [Fact]
    public async Task BulkDeletionUsesConfirmedSnapshotAndPublishesOnceWhileLockedPrivateItemsSurvive()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        var first = (await store.ImportAsync(new ClipboardCapture { Text = "first selected note" })).Entry!;
        var second = (await store.ImportAsync(new ClipboardCapture { Text = "second selected note" })).Entry!;
        var keep = (await store.ImportAsync(new ClipboardCapture { Text = "unselected note" })).Entry!;
        var personal = (await store.ImportAsync(new ClipboardCapture { Text = "private note" })).Entry!;
        using var pin = new SecureString();
        foreach (char digit in "123456") pin.AppendChar(digit);
        pin.MakeReadOnly();
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        await store.MovePersonalAsync(personal.Id, true);
        await store.LockPersonalAsync();
        await store.UpdateAsync(first.Id, pinned: true);
        await store.UpdateAsync(second.Id, archived: true);
        int notifications = 0;
        store.Changed += () => notifications++;
        await store.DeleteManyAsync([first.Id, second.Id, first.Id, Guid.NewGuid(), personal.Id]);
        Assert.Equal(1, notifications);
        Assert.Null(store.GetEntry(first.Id));
        Assert.Null(store.GetEntry(second.Id));
        Assert.NotNull(store.GetEntry(keep.Id));
        Assert.True(await store.UnlockPersonalAsync(pin));
        Assert.Equal("private note", (await store.GetContentAsync(personal.Id)).Text);
        var recopy = (await store.ImportAsync(new ClipboardCapture { Text = "first selected note" })).Entry!;
        Assert.NotEqual(first.Id, recopy.Id);
    }

    [Fact]
    public void LabelCreationAndVisibleBulkActionsWorkWithinTheSelectedCategory() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 900, Height = 340 };
        try
        {
            await store.InitializeAsync();
            tray.Attach(controller);
            var initialCategories = ((ItemsControl)tray.FindName("CategoryPanel")).Items.Cast<ClipboardCategoryViewModel>().ToArray();
            Assert.True(Array.FindIndex(initialCategories, category => category.Name == "Personal") <
                Array.FindIndex(initialCategories, category => category.Name == "Important"));
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            ((Button)tray.FindName("AddLabelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((TextBox)tray.FindName("LabelNameBox")).Text = "Release notes";
            await (Task)Invoke(tray, "SaveLabelAsync")!;
            var label = Assert.Single(store.GetLabels().Where(label => label.IsCustom));
            var keep = (await store.ImportAsync(new ClipboardCapture { Text = "outside label" })).Entry!;
            for (int i = 0; i < 8; i++)
            {
                var entry = (await store.ImportAsync(new ClipboardCapture { Text = $"release note {i}" })).Entry!;
                await controller.AssignCategoryAsync(entry, label.Id);
            }
            await (Task)Invoke(tray, "RefreshAsync")!;
            var categories = (ItemsControl)tray.FindName("CategoryPanel");
            Assert.Contains(categories.Items.Cast<ClipboardCategoryViewModel>(), category => category.Name == label.Id && category.Label == "Release notes");
            var cards = (ListBox)tray.FindName("Cards");
            Assert.Equal(8, cards.Items.Count);
            ((Button)tray.FindName("SelectAllButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(8, cards.SelectedItems.Count);
            var delete = (Button)tray.FindName("DeleteSelectedButton");
            Assert.True(delete.IsVisible);
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, ((FrameworkElement)tray.FindName("DeleteConfirmationPanel")).Visibility);
            ((Button)tray.FindName("ConfirmDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WpfFrameWaiter.UntilAsync(() => store.GetCategoryCounts().GetValueOrDefault(label.Id) == 0,
                "selected items deleted", CancellationToken.None);
            Assert.NotNull(store.GetEntry(keep.Id));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LabelEditorClosesWhenTransientOverlaysResetOrTrayHides() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 900, Height = 340 };
        try
        {
            await store.InitializeAsync();
            tray.Attach(controller);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            var addLabelButton = (Button)tray.FindName("AddLabelButton");
            var labelEditorPanel = (FrameworkElement)tray.FindName("LabelEditorPanel");

            addLabelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Visible, labelEditorPanel.Visibility);
            Assert.True(tray.IsContextMenuOpen);

            Invoke(tray, "HidePrivateContent");
            Assert.Equal(Visibility.Collapsed, labelEditorPanel.Visibility);
            Assert.False(tray.IsContextMenuOpen);
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task LabelsCategoryOrderAndVaultWriteAtomicallyWithoutLeavingTempFiles()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();

        var label = await store.AddLabelAsync("Atomic label");
        await store.ReorderCategoryAsync(label.Id, "All");

        using var pin = new SecureString();
        foreach (char digit in "123456") pin.AppendChar(digit);
        pin.MakeReadOnly();
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));

        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp", SearchOption.AllDirectories));

        var labelsJson = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "labels.json"));
        var orderJson = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "category-order.json"));
        var vaultJson = await File.ReadAllTextAsync(Path.Combine(fixture.Root, "personal-key.json"));

        using var labelsDoc = System.Text.Json.JsonDocument.Parse(labelsJson);
        using var orderDoc = System.Text.Json.JsonDocument.Parse(orderJson);
        using var vaultDoc = System.Text.Json.JsonDocument.Parse(vaultJson);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, labelsDoc.RootElement.ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Array, orderDoc.RootElement.ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, vaultDoc.RootElement.ValueKind);
    }

    private static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-labels-bulk-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
