using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Security;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardFixRegressionTests
{
    [Theory]
    [InlineData("Ctrl+DefinitelyNotAKey")]
    [InlineData("Ctrl+999999999")]
    [InlineData("Ctrl+Shift+invalid-key")]
    public void InvalidHotkeysAreRejectedWithoutThrowing(string gesture) => SharedStaTestRunner.Run(() =>
        Assert.False(ClipboardHotkeyController.TryParse(gesture, out _, out _)));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyPlainTextPreservesRichFormats(string? text) => SharedStaTestRunner.Run(() =>
    {
        var data = new DataObject();
        ClipboardHistoryController.SetTextFormats(data, new ClipboardContent
        {
            Text = text!,
            Html = "<b>rich only</b>",
            Rtf = @"{\rtf1 rich only}"
        });
        Assert.Equal(string.Empty, data.GetData(DataFormats.UnicodeText));
        Assert.Equal("<b>rich only</b>", data.GetData(DataFormats.Html));
        Assert.Equal(@"{\rtf1 rich only}", data.GetData(DataFormats.Rtf));
    });

    [Fact]
    public void EmptyTextSelectionBuildsClipboardDataWithoutThrowing() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new StoreFixture();
        using var store = fixture.Store();
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        await store.InitializeAsync();
        var entry = (await store.ImportAsync(new ClipboardCapture { Html = "<b>rich only</b>" })).Entry!;
        var pending = (Task<(DataObject Data, bool Personal)>)Invoke(controller, "CreateSelectionDataAsync",
            new[] { entry }, new List<string>())!;
        var result = await pending;
        Assert.Equal(string.Empty, result.Data.GetData(DataFormats.UnicodeText));
        Assert.Equal("<b>rich only</b>", result.Data.GetData(DataFormats.Html));
        Assert.False(result.Personal);
    });

    [Theory]
    [InlineData(ClipboardKind.Text, "Text")]
    [InlineData(ClipboardKind.Code, "Code")]
    [InlineData(ClipboardKind.Link, "Links")]
    [InlineData(ClipboardKind.Image, "Images")]
    [InlineData(ClipboardKind.Color, "Colors")]
    [InlineData(ClipboardKind.File, "Files")]
    public void ArchivedKindsAreHiddenFromTypeAndGroupTabs(ClipboardKind kind, string category)
    {
        var entry = new ClipboardEntry { Kind = kind, IsArchived = true, IsPinned = true, Groups = ["Work"] };
        Assert.False(ClipboardHistoryStore.MatchesCategory(entry, category));
        Assert.False(ClipboardHistoryStore.MatchesCategory(entry, "Work"));
        Assert.True(ClipboardHistoryStore.MatchesCategory(entry, "Archive"));
        Assert.True(ClipboardHistoryStore.MatchesCategory(entry, "Pin"));
    }

    [Fact]
    public async Task StartupRetainsOldArchiveAndItsContent()
    {
        using var fixture = new StoreFixture();
        ClipboardEntry entry;
        using (var store = fixture.Store())
        {
            await store.InitializeAsync();
            entry = (await store.ImportAsync(new ClipboardCapture { Text = "keep in archive" })).Entry!;
            await store.UpdateAsync(entry.Id, archived: true);
            entry = store.GetEntry(entry.Id)! with { CopiedUtc = DateTime.UtcNow.AddDays(-60) };
        }
        File.WriteAllText(fixture.ItemPath(entry.Id, "entry.json"), JsonSerializer.Serialize(entry));
        using var reopened = fixture.Store();
        await reopened.InitializeAsync();
        Assert.Equal(entry.Id, Assert.Single(await reopened.SearchAsync("", "Archive")).Id);
        Assert.Equal("keep in archive", (await reopened.GetContentAsync(entry.Id)).Text);
    }

    [Fact]
    public async Task RepeatedFullTextSearchDoesNotReopenContentFiles()
    {
        using var fixture = new StoreFixture();
        Guid id;
        using (var store = fixture.Store())
        {
            await store.InitializeAsync();
            id = (await store.ImportAsync(new ClipboardCapture { Text = new string('a', 1200) + " needle" })).Entry!.Id;
        }
        using var reopened = fixture.Store();
        await reopened.InitializeAsync();
        Assert.Single(await reopened.SearchAsync("needle", "Text"));
        using var lockedContent = new FileStream(fixture.ItemPath(id, "content.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        foreach (string query in new[] { "need", "needle", "NEEDLE", "needle @text" })
            Assert.Single(await reopened.SearchAsync(query, "Text"));
        Assert.Empty(await reopened.SearchAsync("missing", "Text"));
    }

    [Fact]
    public async Task LockingPersonalDiscardsCachedFullText()
    {
        using var fixture = new StoreFixture();
        using var store = fixture.Store();
        await store.InitializeAsync();
        using var pin = new SecureString();
        foreach (char digit in "123456") pin.AppendChar(digit);
        Assert.True(await store.UnlockPersonalAsync(pin, create: true));
        var entry = (await store.ImportAsync(new ClipboardCapture
        {
            Text = new string('a', 1200) + " private-tail",
            TargetCategory = "Personal"
        })).Entry!;
        Assert.Single(await store.SearchAsync("private-tail", "Personal"));
        await store.LockPersonalAsync();
        Assert.Empty(await store.SearchAsync("private-tail", "Personal"));
        Assert.True(await store.UnlockPersonalAsync(pin));
        using (var lockedContent = new FileStream(fixture.ItemPath(entry.Id, "content.json.enc", personal: true), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Empty(await store.SearchAsync("private-tail", "Personal"));
        Assert.Single(await store.SearchAsync("private-tail", "Personal"));
    }

    [Fact]
    public void SearchCacheHonorsMemoryBudgetAndRecentUse()
    {
        var cache = new ClipboardSearchTextCache(maxCharacters: 10, maxEntries: 2);
        Guid first = Guid.NewGuid(), second = Guid.NewGuid(), third = Guid.NewGuid();
        cache.Set(first, "12345"); cache.Set(second, "67890");
        Assert.True(cache.TryGet(first, out _));
        cache.Set(third, "abcde");
        Assert.True(cache.TryGet(first, out _));
        Assert.False(cache.TryGet(second, out _));
        cache.Set(third, new string('x', 11));
        Assert.False(cache.TryGet(third, out _));
        cache.Clear();
        Assert.False(cache.TryGet(first, out _));
    }

    [Fact]
    public void DetachedMenuVisualDoesNotThrow() => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var menu = new ContextMenu();
        ((HashSet<ContextMenu>)Field(tray, "_openMenus")!).Add(menu);
        Assert.False(tray.ContainsMenuPoint(new Point(100, 100)));
        Assert.False((bool)typeof(ClipboardTray).GetMethod("ContainsVisualPoint", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { new Border(), new Point(100, 100) })!);
    });

    [Fact]
    public void CorruptImageDetailsShowFallbackWithoutGenericError() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var fixture = new StoreFixture();
        using var store = fixture.Store();
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = [137, 80, 78, 71, 0] })).Entry!;
            tray.Attach(controller);
            ((DispatcherTimer)Field(tray, "_searchTimer")!).Stop();
            typeof(ClipboardTray).GetField("_queryDirty", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tray, false);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await (Task)Invoke(tray, "ShowDetailsAsync", new ClipboardCardViewModel(entry))!;
            Assert.Equal(Visibility.Visible, ((FrameworkElement)tray.FindName("DetailPanel")).Visibility);
            Assert.False(string.IsNullOrEmpty(((TextBox)tray.FindName("DetailText")).Text));
            Assert.Null(((Image)tray.FindName("DetailImage")).Source);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)tray.FindName("StatusPanel")).Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void SingleClickRequestsCopyAndMultiSelectionDoesNot() => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var cards = (ObservableCollection<ClipboardCardViewModel>)Field(tray, "_cards")!;
        var first = new ClipboardCardViewModel(new ClipboardEntry());
        var second = new ClipboardCardViewModel(new ClipboardEntry());
        cards.Add(first); cards.Add(second);
        var visual = new Border { DataContext = first };
        Invoke(tray, "Card_Down", visual, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = visual });
        Assert.True((bool)Field(tray, "_copyOnRelease")!);
        var list = (ListBox)tray.FindName("Cards");
        list.SelectedItems.Add(second);
        Invoke(tray, "Card_Down", visual, new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = visual });
        Assert.False((bool)Field(tray, "_copyOnRelease")!);
    });

    private static object? Invoke(object target, string name, params object?[] args) => target.GetType()
        .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Field(object target, string name) => target.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "vnotch-clipboard-fixes-" + Guid.NewGuid().ToString("N"));
        internal ClipboardHistoryStore Store() => new(_root);
        internal string ItemPath(Guid id, string file, bool personal = false) => Path.Combine(_root, personal ? "personal" : "items", id.ToString("N"), file);
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
