using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
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
public sealed class ClipboardTraySafetyTests
{
    [Fact]
    public void CancelDeleteRestoresListFocusAndAllowsDeletingTheSameSelectionAgain() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-delete-again-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            await store.ImportAsync(new ClipboardCapture { Text = "first" });
            await store.ImportAsync(new ClipboardCapture { Text = "second" });
            tray.Attach(controller);
            window.Show();
            await InvokeTask(tray, "RefreshAsync");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ListBox)tray.FindName("Cards");
            list.SelectAll();
            Assert.Equal(2, list.SelectedItems.Count);
            var selection = list.SelectedItems.Cast<ClipboardCardViewModel>().ToArray();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var pending = (Task)Invoke(tray, "ConfirmDeleteAsync", (object)selection)!;
                Assert.False(pending.IsCompleted);
                FocusManager.SetFocusedElement(FocusManager.GetFocusScope(list), (Button)tray.FindName("CancelDeleteButton"));
                ((Button)tray.FindName("CancelDeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await pending;
                Assert.Same(list, FocusManager.GetFocusedElement(FocusManager.GetFocusScope(list)));
                Assert.Equal(2, list.SelectedItems.Count);
                Assert.All(selection, card => Assert.NotNull(store.GetEntry(card.Entry.Id)));
            }
        }
        finally
        {
            window.Close(); tray.Dispose(); controller.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    private static Task InvokeTask(object target, string name) => (Task)Invoke(target, name)!;

    [Fact]
    public void StatusMessagesOverlayWithoutResizingOrBlockingTheCards() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        cards.Add(new ClipboardCardViewModel(new ClipboardEntry { Title = "A card" }));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            window.UpdateLayout();
            var viewport = (FrameworkElement)tray.FindName("CardsViewport");
            double originalHeight = viewport.ActualHeight;
            Invoke(tray, "StatusChanged", "Could not save this copy.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var status = (FrameworkElement)tray.FindName("StatusPanel");
            Assert.Equal(Visibility.Visible, status.Visibility);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)tray.FindName("EmptyText")).Visibility);
            Assert.Equal(originalHeight, viewport.ActualHeight);
            Assert.False(status.IsHitTestVisible);
            Assert.True(status.ActualWidth < viewport.ActualWidth);
            Assert.Equal(TimeSpan.FromSeconds(2), Field<DispatcherTimer>(tray, "_statusTimer").Interval);
            Invoke(tray, "StatusTimer_Tick", null, EventArgs.Empty);
            await WpfFrameWaiter.UntilAsync(() => status.Visibility == Visibility.Collapsed, "status toast dismissed", CancellationToken.None);
            Assert.Equal(Visibility.Collapsed, status.Visibility);
            Assert.Single(cards);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbandoningPersonalCancelsBothQueuedFilesAndCards(bool collapse) => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        Set(tray, "_category", "Personal");
        Set(tray, "_pendingPersonalFiles", new[] { @"C:\old-file.txt" });
        Set(tray, "_pendingPersonalDrop", new[] { new ClipboardEntry() });
        if (collapse) tray.BeginExit(); else Invoke(tray, "SelectCategory", "Text");
        Assert.Empty(Field<string[]>(tray, "_pendingPersonalFiles"));
        Assert.Empty(Field<ClipboardEntry[]>(tray, "_pendingPersonalDrop"));
    });

    [Theory]
    [InlineData("CancelDelete_Click", false)]
    [InlineData("ConfirmDelete_Click", true)]
    [InlineData("BeginExit", false)]
    public void DeleteConfirmationStaysResponsiveAndRequiresExplicitConsent(string action, bool deletes) => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-delete-banner-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        try
        {
            await store.InitializeAsync(); tray.Attach(controller);
            var entry = (await store.ImportAsync(new ClipboardCapture { Text = "keep until confirmed" })).Entry!;
            Task pending = (Task)Invoke(tray, "ConfirmDeleteAsync", (object)new[] { new ClipboardCardViewModel(entry) })!;
            Assert.False(pending.IsCompleted);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)tray.FindName("DeleteConfirmationPanel")).Visibility);
            bool rendered = false;
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => rendered = true, DispatcherPriority.Render);
            Assert.True(rendered);
            Assert.NotNull(store.GetEntry(entry.Id));
            if (action == "BeginExit") tray.BeginExit(); else Invoke(tray, action, null, new RoutedEventArgs());
            await pending;
            Assert.Equal(deletes, store.GetEntry(entry.Id) == null);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)tray.FindName("DeleteConfirmationPanel")).Visibility);
        }
        finally { tray.Dispose(); controller.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    });

    private static object? Invoke(object target, string name, params object?[] args)
        => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Field<T>(object target, string name)
        => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object value)
        => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
