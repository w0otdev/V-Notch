using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
public sealed class ClipboardCategoryReorderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReorderingToBothEndsPreservesTheSelectedFilterAndSurvivesRestart(bool reduced) => RunWithTray(async (tray, store, root) =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduced);
        try
        {
            var entry = (await store.ImportAsync(new ClipboardCapture { Text = "still in Text" })).Entry!;
            Invoke(tray, "SelectCategory", "Text");
            await (Task)Invoke(tray, "RefreshAsync")!;
            string[] original = store.GetCategoryOrder().ToArray();
            int writes = 0;
            store.Changed += CountWrite;
            void CountWrite() { writes += 1; }
            try
            {
                StageDrag(tray, "All");
                await Complete(tray, EndPoint(tray));
                Assert.Equal(original.Skip(1).Append("All"), store.GetCategoryOrder());
                Assert.Equal(store.GetCategoryOrder(), VisibleOrder(tray));
                Assert.Equal("Text", tray.CurrentCategory);
                Assert.Equal("Text", Assert.Single(Categories(tray), category => category.IsSelected).Name);
                Assert.Equal(1, writes);

                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                StageDrag(tray, "All");
                await Complete(tray, new Point(1, Scroll(tray).ActualHeight / 2));
                Assert.Equal(original, store.GetCategoryOrder());
                Assert.Equal(original, VisibleOrder(tray));
                Assert.Equal("Text", tray.CurrentCategory);
                Assert.Equal(2, writes);
                Assert.Equal(entry.Id, Assert.Single(await store.SearchAsync("", "Text")).Id);
                using var reopened = new ClipboardHistoryStore(root);
                await reopened.InitializeAsync();
                Assert.Equal(original, reopened.GetCategoryOrder());
            }
            finally { store.Changed -= CountWrite; }
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void CustomTagCanMoveAmongTypeTagsWithoutChangingItsMembership() => RunWithTray(async (tray, store, root) =>
    {
        var label = await store.AddLabelAsync("Release notes with a longer title");
        var entry = (await store.ImportAsync(new ClipboardCapture { Text = "release detail" })).Entry!;
        await store.UpdateAsync(entry.Id, groups: [label.Id]);
        await (Task)Invoke(tray, "RefreshAsync")!;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        string[] original = store.GetCategoryOrder().ToArray();
        var target = ButtonFor(tray, "Text");
        Point insertion = target.TranslatePoint(new Point(1, target.ActualHeight / 2), Scroll(tray));
        StageDrag(tray, label.Id);
        await Complete(tray, insertion);

        var expected = original.Where(id => id != label.Id).ToList();
        expected.Insert(expected.IndexOf("Text"), label.Id);
        Assert.Equal(expected, store.GetCategoryOrder());
        Assert.Equal(expected, VisibleOrder(tray));
        Assert.Equal("All", tray.CurrentCategory);
        Assert.Equal(entry.Id, Assert.Single(await store.SearchAsync("", label.Id)).Id);
        using var reopened = new ClipboardHistoryStore(root);
        await reopened.InitializeAsync();
        Assert.Equal(expected, reopened.GetCategoryOrder());
    });

    [Theory]
    [InlineData("outside")]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("hide")]
    public void CancelledReorderDoesNotWriteOrSelectAnotherTag(string cancel) => RunWithTray(async (tray, store, root) =>
    {
        string[] original = store.GetCategoryOrder().ToArray();
        int writes = 0;
        store.Changed += CountWrite;
        void CountWrite() { writes += 1; }
        try
        {
            StageDrag(tray, "Personal");
            Invoke(tray, "UpdateCategoryReorder", EndPoint(tray));
            if (cancel == "outside") await Complete(tray, new Point(20, Scroll(tray).ActualHeight + 30));
            else if (cancel == "hide") Window.GetWindow(tray)!.Hide();
            else if (cancel == "escape") Assert.True(tray.TryDismissOverlay());
            else Invoke(tray, "CancelCategoryReorder");
            Assert.Equal(original, store.GetCategoryOrder());
            Assert.Equal(original, VisibleOrder(tray));
            Assert.Equal("All", tray.CurrentCategory);
            Assert.False(Field<bool>(tray, "_categoryReordering"));
            Assert.Null(Field<Button?>(tray, "_categoryReorderSource"));
            Assert.Equal(0, writes);
            Assert.False(File.Exists(Path.Combine(root, "category-order.json")));
        }
        finally { store.Changed -= CountWrite; }
    });

    [Fact]
    public void PressWithoutDragLeavesNormalSelectionAvailableAndDoesNotWrite() => RunWithTray(async (tray, store, root) =>
    {
        var button = ButtonFor(tray, "Images");
        var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), Scroll(tray));
        Invoke(tray, "CategoryReorder_Press", button, point);
        Assert.False((bool)Invoke(tray, "TryBeginCategoryReorder", point + new Vector(1, 1))!);
        Assert.False(Field<bool>(tray, "_categoryReordering"));
        await Complete(tray, point);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal("Images", tray.CurrentCategory);
        Assert.False(File.Exists(Path.Combine(root, "category-order.json")));
    });

    [Fact]
    public void CountRefreshAndLocalizationDuringDragKeepTheSourceAndSelectedFilter() => RunWithTray(async (tray, store, _) =>
    {
        var source = ButtonFor(tray, "Work");
        StageDrag(tray, "Work");
        SetField(tray, "_categoryCounts", new Dictionary<string, int> { ["Work"] = 42 });
        Invoke(tray, "RebuildCategories");
        tray.ApplyLocalization();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(source, Field<Button?>(tray, "_categoryReorderSource"));
        Assert.Equal("All", tray.CurrentCategory);
        Assert.Equal(42, Categories(tray).Single(category => category.Name == "Work").Count);
        await Complete(tray, new Point(1, Scroll(tray).ActualHeight / 2));
        Assert.Equal("Work", store.GetCategoryOrder()[0]);
        Assert.Equal(store.GetCategoryOrder(), VisibleOrder(tray));
    });

    [Fact]
    public void CatalogChangeCancelsDragBeforeReplacingItsButtons() => RunWithTray(async (tray, store, root) =>
    {
        var source = ButtonFor(tray, "Work");
        StageDrag(tray, "Work");
        Invoke(tray, "UpdateCategoryReorder", new Point(1, Scroll(tray).ActualHeight / 2));
        var label = await store.AddLabelAsync("Added while arranging");
        await (Task)Invoke(tray, "RefreshAsync")!;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await Complete(tray, new Point(1, Scroll(tray).ActualHeight / 2));
        Assert.False(Field<bool>(tray, "_categoryReordering"));
        Assert.Null(Field<Button?>(tray, "_categoryReorderSource"));
        Assert.Equal(1, source.Opacity);
        Assert.Equal("All", store.GetCategoryOrder()[0]);
        Assert.Contains(label.Id, VisibleOrder(tray));
        Assert.Equal(store.GetCategoryOrder(), VisibleOrder(tray));
        Assert.False(File.Exists(Path.Combine(root, "category-order.json")));
    });

    [Fact]
    public void ReleaseAfterScrollingUsesTheLatestViewportGeometry() => RunWithTray(async (tray, store, _) =>
    {
        var window = Window.GetWindow(tray)!;
        window.Width = 680;
        tray.UpdateLayout();
        string[] original = store.GetCategoryOrder().ToArray();
        Assert.True(Scroll(tray).ScrollableWidth > 0);
        StageDrag(tray, "All");
        Scroll(tray).ScrollToHorizontalOffset(Scroll(tray).ScrollableWidth);
        tray.UpdateLayout();
        Assert.True(Scroll(tray).HorizontalOffset > 0);
        await Complete(tray, EndPoint(tray));
        Assert.Equal(original.Skip(1).Append("All"), store.GetCategoryOrder());
        Assert.Equal(store.GetCategoryOrder(), VisibleOrder(tray));
        Assert.Equal("All", tray.CurrentCategory);
    });

    [Theory]
    [InlineData("button")]
    [InlineData("icon")]
    [InlineData("text")]
    public void PressingAnyPartOfATagStagesThatTagWithoutSelectingIt(string part) => RunWithTray((tray, store, root) =>
    {
        var button = ButtonFor(tray, "Images");
        UIElement hit = part switch
        {
            "icon" => Descendants(button).OfType<TrayIcon>().First(),
            "text" => Descendants(button).OfType<TextBlock>().First(),
            _ => button
        };
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        hit.RaiseEvent(press);
        Assert.Same(button, Field<Button?>(tray, "_categoryReorderSource"));
        Assert.False(press.Handled);
        Assert.False(Field<bool>(tray, "_categoryReordering"));
        Assert.Equal("All", tray.CurrentCategory);
        Assert.False(File.Exists(Path.Combine(root, "category-order.json")));
        Invoke(tray, "CancelCategoryReorder");
        return Task.CompletedTask;
    });

    [Fact]
    public void QueuedWritesKeepOptimisticMovesAndDoNotStealLaterFocusOrScroll() => RunWithTray(async (tray, store, _) =>
    {
        Window.GetWindow(tray)!.Width = 680;
        tray.UpdateLayout();
        string[] original = store.GetCategoryOrder().ToArray();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void BlockQueue() { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
        store.Changed += BlockQueue;
        Task blocker = store.ImportAsync(new ClipboardCapture { Text = "queue blocked while arranging tags" });
        Task? first = null, second = null;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            store.Changed -= BlockQueue;
            StageDrag(tray, "All");
            Scroll(tray).ScrollToHorizontalOffset(Scroll(tray).ScrollableWidth);
            tray.UpdateLayout();
            first = (Task)Invoke(tray, "CompleteCategoryReorderAsync", EndPoint(tray))!;
            Assert.False(first.IsCompleted);
            Assert.Equal(original.Skip(1).Append("All"), VisibleOrder(tray));

            Scroll(tray).ScrollToHorizontalOffset(0);
            tray.UpdateLayout();
            StageDrag(tray, "Pin");
            Scroll(tray).ScrollToHorizontalOffset(Scroll(tray).ScrollableWidth);
            tray.UpdateLayout();
            second = (Task)Invoke(tray, "CompleteCategoryReorderAsync", EndPoint(tray))!;
            Assert.False(second.IsCompleted);
            Assert.Equal(original.Skip(2).Append("All").Append("Pin"), VisibleOrder(tray));

            var search = (TextBox)tray.FindName("SearchBox");
            var scope = FocusManager.GetFocusScope(search);
            FocusManager.SetFocusedElement(scope, search);
            search.Focus();
            Scroll(tray).ScrollToHorizontalOffset(100);
            tray.UpdateLayout();
            double offset = Scroll(tray).HorizontalOffset;
            release.Set();
            await Task.WhenAll(blocker, first, second);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(original.Skip(2).Append("All").Append("Pin"), store.GetCategoryOrder());
            Assert.Equal(store.GetCategoryOrder(), VisibleOrder(tray));
            Assert.Same(search, FocusManager.GetFocusedElement(scope));
            Assert.Equal(offset, Scroll(tray).HorizontalOffset, 2);
            Assert.Equal("All", tray.CurrentCategory);
        }
        finally
        {
            release.Set();
            store.Changed -= BlockQueue;
            await blocker;
            if (first != null) await first;
            if (second != null) await second;
        }
    });

    [Fact]
    public void SlidingNeighborsDoNotChangeTheDropTargetAndCancellationRestoresThem() => RunWithTray(async (tray, store, _) =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            var buttons = Categories(tray).Select(category => ButtonFor(tray, category.Name)).ToArray();
            var transforms = buttons.ToDictionary(button => button, button => button.RenderTransform);
            var source = ButtonFor(tray, "Work");
            var neighbor = ButtonFor(tray, "Text");
            var point = neighbor.TranslatePoint(new Point(1, neighbor.ActualHeight / 2), Scroll(tray));
            SaveDragSnapshot(tray, "clipboard-tags-00-idle.png");
            StageDrag(tray, "Work");
            Invoke(tray, "UpdateCategoryReorder", point);
            Assert.Equal("Text", Field<string?>(tray, "_categoryReorderBeforeId"));
            SaveDragSnapshot(tray, "clipboard-tags-01-lift.png");
            await Task.Delay(80);
            SaveDragSnapshot(tray, "clipboard-tags-02-shift.png");
            await Task.Delay(150);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.InRange(neighbor.RenderTransform.Value.OffsetX, source.ActualWidth + 5, source.ActualWidth + 7);
            Invoke(tray, "UpdateCategoryReorder", point);
            Assert.Equal("Text", Field<string?>(tray, "_categoryReorderBeforeId"));
            Assert.Equal(Visibility.Visible, ((FrameworkElement)tray.FindName("CategoryReorderMarker")).Visibility);
            Assert.Equal(0, source.Opacity);
            SaveDragSnapshot(tray, "clipboard-tags-03-hover.png");

            Invoke(tray, "CancelCategoryReorder");
            tray.UpdateLayout();
            foreach (var button in buttons.Where(button => button != source)) Assert.Same(transforms[button], button.RenderTransform);
            Assert.Equal(1, source.Opacity);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)tray.FindName("CategoryReorderOverlay")).Visibility);
            Assert.Equal("All", tray.CurrentCategory);
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR")))
            {
                StageDrag(tray, "Work");
                Invoke(tray, "UpdateCategoryReorder", point);
                await Task.Delay(180);
                await Complete(tray, point);
                SaveDragSnapshot(tray, "clipboard-tags-04-landing.png");
                await Task.Delay(80);
                SaveDragSnapshot(tray, "clipboard-tags-05-landing-mid.png");
                await Task.Delay(120);
                SaveDragSnapshot(tray, "clipboard-tags-06-settled.png");
            }
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ReducedMotionPlacesTheGapAndNeighborsImmediatelyWithoutAnimationClocks() => RunWithTray((tray, store, _) =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        try
        {
            var source = ButtonFor(tray, "Work");
            var neighbor = ButtonFor(tray, "Text");
            var point = neighbor.TranslatePoint(new Point(1, neighbor.ActualHeight / 2), Scroll(tray));
            StageDrag(tray, "Work");
            Invoke(tray, "UpdateCategoryReorder", point);
            Assert.Equal(source.ActualWidth + 6, neighbor.RenderTransform.Value.OffsetX, 2);
            foreach (var transform in ((TransformGroup)neighbor.RenderTransform).Children)
                Assert.False(transform.HasAnimatedProperties);
            Assert.False(((ScaleTransform)tray.FindName("CategoryReorderGhostScale")).HasAnimatedProperties);
            Assert.False(((TranslateTransform)tray.FindName("CategoryReorderGapOffset")).HasAnimatedProperties);
            Invoke(tray, "CancelCategoryReorder");
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
        return Task.CompletedTask;
    });

    [Fact]
    public void FinishingAnOlderLandingCannotHideANewerDrag() => RunWithTray(async (tray, store, _) =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        try
        {
            StageDrag(tray, "Work");
            await Complete(tray, new Point(1, Scroll(tray).ActualHeight / 2));
            StageDrag(tray, "Text");
            Invoke(tray, "UpdateCategoryReorder", EndPoint(tray));
            await Task.Delay(240);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(Field<bool>(tray, "_categoryReordering"));
            Assert.Equal(Visibility.Visible, ((FrameworkElement)tray.FindName("CategoryReorderOverlay")).Visibility);
            var ghost = (FrameworkElement)tray.FindName("CategoryReorderGhost");
            Assert.Equal("Text", Assert.IsType<ClipboardCategoryViewModel>(ghost.DataContext).Name);
            Assert.Equal("Text", (Field<Button?>(tray, "_categoryReorderSource")!.DataContext as ClipboardCategoryViewModel)?.Name);
            Invoke(tray, "CancelCategoryReorder");
        }
        finally { AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(24)]
    [InlineData(64)]
    public void LandingUsesTheFinalScrolledSlotForWideTags(int labelLength) => RunWithTray(async (tray, store, _) =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task? blocker = null, pending = null;
        void BlockQueue() { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); }
        try
        {
            var label = await store.AddLabelAsync(new string('W', labelLength));
            await (Task)Invoke(tray, "RefreshAsync")!;
            Window.GetWindow(tray)!.Width = 680;
            tray.UpdateLayout();
            Scroll(tray).ScrollToHorizontalOffset(Scroll(tray).ScrollableWidth);
            tray.UpdateLayout();
            Assert.True(Scroll(tray).HorizontalOffset > 0);
            store.Changed += BlockQueue;
            blocker = store.ImportAsync(new ClipboardCapture { Text = "hold wide-tag order write" });
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(3))));
            store.Changed -= BlockQueue;
            StageDrag(tray, label.Id);
            Scroll(tray).ScrollToHorizontalOffset(0);
            tray.UpdateLayout();
            Point releasePoint = new(Scroll(tray).ActualWidth - 14, Scroll(tray).ActualHeight / 2);
            pending = (Task)Invoke(tray, "CompleteCategoryReorderAsync", releasePoint)!;
            Assert.False(pending.IsCompleted);
            tray.UpdateLayout();
            var destination = ButtonFor(tray, label.Id);
            var ghost = (FrameworkElement)tray.FindName("CategoryReorderGhost");
            Point slot = destination.TranslatePoint(new Point(), Scroll(tray));
            Assert.Equal(slot.X, (double)ghost.GetAnimationBaseValue(Canvas.LeftProperty), 2);
            Assert.Equal(slot.Y, (double)ghost.GetAnimationBaseValue(Canvas.TopProperty), 2);
            release.Set();
            await Task.WhenAll(blocker, pending);
            await Task.Delay(220);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, ButtonFor(tray, label.Id).Opacity);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)tray.FindName("CategoryReorderOverlay")).Visibility);
            Assert.Equal(store.GetCategoryOrder(), VisibleOrder(tray));
            Assert.Equal("All", tray.CurrentCategory);
        }
        finally
        {
            release.Set();
            store.Changed -= BlockQueue;
            if (blocker != null) await blocker;
            if (pending != null) await pending;
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    private static void RunWithTray(Func<ClipboardTray, ClipboardHistoryStore, string, Task> body) => SharedStaTestRunner.RunAsync(async () =>
    {
        Loc.SetLanguage("en");
        string root = Directory.CreateTempSubdirectory("vnotch-category-reorder-").FullName;
        try
        {
            using var store = new ClipboardHistoryStore(root);
            await store.InitializeAsync();
            await using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
            using var tray = new ClipboardTray();
            tray.Attach(controller);
            var window = new BackgroundWindow { Content = tray, Width = 2400, Height = 340 };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                tray.UpdateLayout();
                await body(tray, store, root);
            }
            finally { window.Close(); }
        }
        finally { Directory.Delete(root, true); }
    });

    private static void StageDrag(ClipboardTray tray, string id)
    {
        var button = ButtonFor(tray, id);
        Point point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), Scroll(tray));
        Invoke(tray, "CategoryReorder_Press", button, point);
        // BackgroundWindow deliberately rejects native capture; exercise the same
        // drag update/commit path after the threshold without stealing the real mouse.
        SetField(tray, "_categoryReordering", true);
        Invoke(tray, "BeginCategoryReorderVisuals");
    }

    private static async Task Complete(ClipboardTray tray, Point point)
    {
        await (Task)Invoke(tray, "CompleteCategoryReorderAsync", point)!;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        tray.UpdateLayout();
    }

    private static void SaveDragSnapshot(ClipboardTray tray, string filename)
    {
        string? directory = Environment.GetEnvironmentVariable("VNOTCH_QA_ARTIFACT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tray.ActualWidth), (int)Math.Ceiling(tray.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            var bounds = new Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight);
            drawing.DrawRectangle(Brushes.Black, null, bounds);
            drawing.DrawRectangle(new VisualBrush(tray), null, bounds);
        }
        bitmap.Render(surface);
        var crop = new CroppedBitmap(bitmap, new Int32Rect(0, 0, Math.Min(bitmap.PixelWidth, 1100), Math.Min(bitmap.PixelHeight, 115)));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(crop));
        using var file = File.Create(Path.Combine(directory, filename));
        encoder.Save(file);
    }

    private static ScrollViewer Scroll(ClipboardTray tray) => (ScrollViewer)tray.FindName("CategoryScroll");
    private static Point EndPoint(ClipboardTray tray) => new(Scroll(tray).ActualWidth - 2, Scroll(tray).ActualHeight / 2);
    private static IEnumerable<ClipboardCategoryViewModel> Categories(ClipboardTray tray) => ((ItemsControl)tray.FindName("CategoryPanel")).Items.Cast<ClipboardCategoryViewModel>();
    private static string[] VisibleOrder(ClipboardTray tray) => Categories(tray).Select(category => category.Name).ToArray();
    private static Button ButtonFor(ClipboardTray tray, string id) => Descendants((ItemsControl)tray.FindName("CategoryPanel")).OfType<Button>().Single(button => Equals(button.Tag, id));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
