using System.IO;
using System.Reflection;
using System.Security;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardEmptyTrayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyTrayPreservesEveryProtectionCombinationOnDisk(bool lockPersonal)
    {
        using var fixture = new Fixture();
        var entries = new List<ClipboardEntry>();
        using (var store = new ClipboardHistoryStore(fixture.Root))
        {
            await store.InitializeAsync();
            using var pin = Pin();
            Assert.True(await store.UnlockPersonalAsync(pin, create: true));
            for (int flags = 0; flags < 8; flags++)
            {
                var entry = (await store.ImportAsync(new ClipboardCapture { Text = $"protection combination {flags}" })).Entry!;
                entries.Add(entry);
                await store.UpdateAsync(entry.Id, pinned: (flags & 1) != 0, archived: (flags & 2) != 0);
                if ((flags & 4) != 0) await store.MovePersonalAsync(entry.Id, true);
            }
            if (lockPersonal) await store.LockPersonalAsync();
            int notifications = 0;
            store.Changed += () => notifications++;
            await store.EmptyTrayAsync(entries.Select(entry => entry.Id).ToArray());
            Assert.Equal(1, notifications);
            Assert.False(Directory.Exists(Path.Combine(fixture.Root, "items", entries[0].Id.ToString("N"))));
        }

        using var reopened = new ClipboardHistoryStore(fixture.Root);
        await reopened.InitializeAsync();
        using var reopenPin = Pin();
        Assert.True(await reopened.UnlockPersonalAsync(reopenPin));
        Assert.Null(reopened.GetEntry(entries[0].Id));
        foreach (var entry in entries.Skip(1))
            Assert.Equal(entry.Preview, (await reopened.GetContentAsync(entry.Id)).Text);
        // A recopy must create an intact item rather than find a deleted fingerprint.
        var recopy = (await reopened.ImportAsync(new ClipboardCapture { Text = "protection combination 0" })).Entry!;
        Assert.NotEqual(entries[0].Id, recopy.Id);
        Assert.Equal("protection combination 0", (await reopened.GetContentAsync(recopy.Id)).Text);
    }

    [Fact]
    public async Task EmptyTrayRechecksQueuedProtectionChangesAndOnlyDeletesConfirmedIds()
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        await store.InitializeAsync();
        var entries = new List<ClipboardEntry>();
        for (int i = 0; i < 5; i++)
            entries.Add((await store.ImportAsync(new ClipboardCapture { Text = $"snapshot item {i}" })).Entry!);
        var confirmedIds = entries.Select(entry => entry.Id).ToArray();
        var later = (await store.ImportAsync(new ClipboardCapture { Text = "arrived after confirmation opened" })).Entry!;
        using var pin = Pin();
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        var pinned = store.UpdateAsync(entries[1].Id, pinned: true);
        var archived = store.UpdateAsync(entries[2].Id, archived: true);
        var personal = store.MovePersonalAsync(entries[3].Id, true);
        var alreadyDeleted = store.DeleteAsync(entries[4].Id);
        await store.EmptyTrayAsync(confirmedIds);
        await Task.WhenAll(pinned, archived, personal, alreadyDeleted);
        Assert.Null(store.GetEntry(entries[0].Id));
        Assert.True(store.GetEntry(entries[1].Id)!.IsPinned);
        Assert.True(store.GetEntry(entries[2].Id)!.IsArchived);
        Assert.True(store.GetEntry(entries[3].Id)!.IsPersonal);
        Assert.NotNull(store.GetEntry(later.Id));
        await store.EmptyTrayAsync(confirmedIds); // Missing and protected items are safe to retry.
    }

    [Fact]
    public void HeaderButtonIsVisibleAndOpensConfirmationThenIgnoresProtectedOnlyHistory()
        => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { Text = "header button test" })).Entry!;
            tray.Attach(controller);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var button = (Button)tray.FindName("EmptyTrayButton");
            Assert.True(button.IsVisible);
            Assert.True(button.ActualWidth > 0);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var panel = (FrameworkElement)tray.FindName("DeleteConfirmationPanel");
            Assert.Equal(Visibility.Visible, panel.Visibility);
            ((Button)tray.FindName("CancelDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WpfFrameWaiter.UntilAsync(() => panel.Visibility == Visibility.Collapsed,
                "empty tray confirmation dismissed", CancellationToken.None);
            Assert.NotNull(store.GetEntry(entry.Id));
            await store.UpdateAsync(entry.Id, pinned: true);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            Assert.NotNull(store.GetEntry(entry.Id));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData("CancelDelete_Click", false)]
    [InlineData("ConfirmDelete_Click", true)]
    [InlineData("BeginExit", false)]
    public void EmptyTrayConfirmsAcrossSearchAndCategoriesAndKeepsProtectedItems(string action, bool deletes)
        => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var store = new ClipboardHistoryStore(fixture.Root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        Loc.SetLanguage("en");
        await store.InitializeAsync();
        var plain = (await store.ImportAsync(new ClipboardCapture { Text = "ordinary text" })).Entry!;
        var link = (await store.ImportAsync(new ClipboardCapture { Text = "https://example.com" })).Entry!;
        var starred = (await store.ImportAsync(new ClipboardCapture { Text = "keep starred" })).Entry!;
        var archive = (await store.ImportAsync(new ClipboardCapture { Text = "keep archived" })).Entry!;
        var personal = (await store.ImportAsync(new ClipboardCapture { Text = "keep private" })).Entry!;
        await store.UpdateAsync(starred.Id, pinned: true);
        await store.UpdateAsync(archive.Id, archived: true);
        using var pin = Pin();
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        await store.MovePersonalAsync(personal.Id, true);
        tray.Attach(controller);
        Invoke(tray, "SelectCategory", "Text");
        ((TextBox)tray.FindName("SearchBox")).Text = "no matching items";
        await (Task)Invoke(tray, "RefreshAsync")!;
        Assert.Empty(((ListBox)tray.FindName("Cards")).Items);
        var button = (Button)tray.FindName("EmptyTrayButton");
        Assert.Equal(Visibility.Visible, button.Visibility);
        Assert.Equal(Loc.Get("clipboard.emptyTray"), AutomationProperties.GetName(button));

        var pending = (Task)Invoke(tray, "ConfirmEmptyTrayAsync")!;
        Assert.False(pending.IsCompleted);
        Assert.Equal(Loc.Get("clipboard.emptyTrayConfirm", 2), ((TextBlock)tray.FindName("DeleteConfirmationText")).Text);
        Assert.NotNull(store.GetEntry(plain.Id));
        var later = (await store.ImportAsync(new ClipboardCapture { Text = "new copy during confirmation" })).Entry!;
        Loc.SetLanguage("vi");
        tray.ApplyLocalization();
        Assert.Equal(Loc.Get("clipboard.emptyTrayConfirm", 2), ((TextBlock)tray.FindName("DeleteConfirmationText")).Text);
        Assert.Equal(Loc.Get("clipboard.emptyTray"), ((Button)tray.FindName("ConfirmDeleteButton")).Content);
        if (action == "BeginExit") tray.BeginExit();
        else Invoke(tray, action, null, new RoutedEventArgs());
        await pending;
        Assert.Equal(deletes, store.GetEntry(plain.Id) == null);
        Assert.Equal(deletes, store.GetEntry(link.Id) == null);
        Assert.NotNull(store.GetEntry(starred.Id));
        Assert.NotNull(store.GetEntry(archive.Id));
        Assert.NotNull(store.GetEntry(later.Id));
        // BeginExit locks the vault; reopen it to verify the private content survived.
        Assert.True(await store.UnlockPersonalAsync(pin));
        Assert.Equal("keep private", (await store.GetContentAsync(personal.Id)).Text);
        Loc.SetLanguage("en");
    });

    private static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private static SecureString Pin()
    {
        var pin = new SecureString();
        foreach (char digit in "123456") pin.AppendChar(digit);
        pin.MakeReadOnly();
        return pin;
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vnotch-empty-tray-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
