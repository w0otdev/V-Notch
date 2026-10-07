using System.Collections.ObjectModel;
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
public sealed class ClipboardTrayPersonalTransitionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CategoryIndicatorRetargetsAndSettlesOnLatestLabel(bool reduced) => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduced);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var offset = (System.Windows.Media.TranslateTransform)tray.FindName("CategorySelectionOffset");
            Invoke(tray, "SelectCategory", "Text");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(60);
            double renderedX = offset.X;
            Invoke(tray, "SelectCategory", "Links");
            if (!reduced) Assert.Equal(renderedX, offset.X, 2);
            await Task.Delay(350);
            var bounds = Field<Rect>(tray, "_categorySelectionBounds");
            Assert.Equal(bounds.X, offset.X, 2);
            Assert.Equal(bounds.Width, ((Border)tray.FindName("CategorySelection")).ActualWidth, 2);
            Assert.Equal(1, ((System.Windows.Media.ScaleTransform)tray.FindName("CategorySelectionScale")).ScaleX, 2);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void PersonalFocusIsQueuedAndCancelledWhenUserLeaves() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var window = new Window { Content = tray, Width = 780, Height = 300, ShowInTaskbar = false };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var pin = (PasswordBox)tray.FindName("PasscodeBox");
            Invoke(tray, "SelectCategory", "Personal");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(pin.IsKeyboardFocused);
            var caret = (Border)tray.FindName("PinCaret");
            Assert.True(caret.IsVisible);
            pin.Password = "123456";
            Assert.False(caret.IsVisible);
            pin.Password = "12345";
            Assert.True(caret.IsVisible);

            Invoke(tray, "SelectCategory", "Text");
            Invoke(tray, "SelectCategory", "Personal");
            Invoke(tray, "SelectCategory", "Text");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(pin.IsKeyboardFocused);
            Assert.False(caret.IsVisible);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingPersonalHidesOldContentBeforeAsyncResultsAndRejectsStaleQueries(bool reduceMotion) => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduceMotion);
        string root = Path.Combine(Path.GetTempPath(), "vnotch-personal-transition-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? blocker = null;
        void BlockQueue() { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
        try
        {
            await store.InitializeAsync();
            var publicEntry = (await store.ImportAsync(new ClipboardCapture { Text = "Public file" })).Entry!;
            var privateEntry = (await store.ImportAsync(new ClipboardCapture { Text = "Private file" })).Entry!;
            using var pin = new SecureString();
            foreach (char digit in "123456") pin.AppendChar(digit);
            pin.MakeReadOnly();
            Assert.True(await store.UnlockPersonalAsync(pin, create: true));
            await store.MovePersonalAsync(privateEntry.Id, true);
            Assert.True(store.GetEntry(privateEntry.Id)!.IsPersonal);
            tray.Attach(controller);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            StopRefresh(tray);
            await (Task)Invoke(tray, "RefreshAsync")!;
            var list = (ListBox)tray.FindName("Cards");
            var lockPanel = (Grid)tray.FindName("LockPanel");
            var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
            Assert.Equal(publicEntry.Id, Assert.Single(cards).Entry.Id);

            Invoke(tray, "SelectCategory", "Personal");
            StopRefresh(tray);
            Assert.Empty(cards);
            Assert.False(list.IsVisible);
            Assert.False(lockPanel.IsVisible); // Unlocked Personal waits for its own results.
            await (Task)Invoke(tray, "RefreshAsync")!;
            Assert.True(list.IsVisible);
            Assert.True(Assert.Single(cards).Entry.IsPersonal);
            Assert.False(lockPanel.IsVisible);

            // Hold disk work so the test observes the entire blank interval,
            // not only a fast query that could hide a single-frame flash.
            store.Changed += BlockQueue;
            blocker = store.ImportAsync(new ClipboardCapture { Text = "Another public file" });
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            Invoke(tray, "SelectCategory", "Text");
            StopRefresh(tray);
            Assert.Empty(cards);
            Assert.False(list.IsVisible);
            Assert.False(lockPanel.IsVisible);
            Assert.False(store.IsPersonalUnlocked);
            var stalePublicQuery = (Task)Invoke(tray, "RefreshAsync")!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(list.IsVisible);
            Assert.False(lockPanel.IsVisible);
            Invoke(tray, "SelectCategory", "Personal");
            StopRefresh(tray);
            Assert.True(lockPanel.IsVisible);
            Assert.False(list.IsVisible);
            Assert.Empty(cards);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(lockPanel.IsVisible);
            Assert.False(list.IsVisible);
            release.Set();
            await blocker;
            await stalePublicQuery;
            StopRefresh(tray);
            Assert.True(lockPanel.IsVisible);
            Assert.False(list.IsVisible);
            Assert.Empty(cards);

            Invoke(tray, "SelectCategory", "Text");
            Assert.False(lockPanel.IsHitTestVisible);
            Assert.False(lockPanel.IsEnabled);
            // Reverse an exit before its completion callback can collapse the panel.
            Invoke(tray, "SelectCategory", "Personal");
            Assert.True(lockPanel.IsHitTestVisible);
            Assert.True(lockPanel.IsEnabled);
            await Task.Delay(450);
            Assert.True(lockPanel.IsVisible);
            Assert.Equal(1, lockPanel.Opacity, 2);
            Invoke(tray, "SelectCategory", "Text");
            Assert.False(list.IsVisible);
            await WpfFrameWaiter.UntilAsync(() => list.IsVisible && cards.Count == 2, "public category loads after locking finishes");
            await WpfFrameWaiter.UntilAsync(() => !lockPanel.IsVisible, "PIN panel finishes its exit");
            Assert.True(list.IsVisible);
            Assert.False(lockPanel.IsVisible);
            Assert.Equal(2, cards.Count);
            Assert.All(cards, card => Assert.False(card.Entry.IsPersonal));
        }
        finally
        {
            release.Set();
            store.Changed -= BlockQueue;
            if (blocker != null) await blocker;
            window.Close();
            tray.Dispose(); controller.Dispose(); store.Dispose();
            AnimationConfig.SetReduceMotion(previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    private static void StopRefresh(ClipboardTray tray) => Field<DispatcherTimer>(tray, "_searchTimer").Stop();
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
