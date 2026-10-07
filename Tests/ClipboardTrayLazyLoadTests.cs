using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
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
public sealed class ClipboardTrayLazyLoadTests
{
    [Fact]
    public void MissingPreviewIsAttemptedOncePerViewportVisit() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-preview-miss-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = [1, 2, 3, 4] })).Entry!;
            tray.Attach(controller);
            Field<DispatcherTimer>(tray, "_searchTimer").Stop();
            SetField(tray, "_queryDirty", false);
            Invoke(tray, "ReconcileCards", (object)new[] { entry });
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => Field<IDictionary>(tray, "_completedContentLoads").Count == 1, "failed decode remembered");
            var element = Field<HashSet<FrameworkElement>>(tray, "_realizedCardElements").Single();
            var slots = Field<SemaphoreSlim>(tray, "_thumbnailSlots");
            await slots.WaitAsync();
            await slots.WaitAsync();
            try
            {
                for (int i = 0; i < 100; i++)
                    Assert.True(((Task)Invoke(tray, "LoadCardContentAsync", element)!).IsCompletedSuccessfully);
                Assert.Empty(Field<IDictionary>(tray, "_thumbnailLoads"));
            }
            finally { slots.Release(2); }
            tray.Visibility = Visibility.Collapsed;
            Assert.Empty(Field<IDictionary>(tray, "_completedContentLoads"));
        }
        finally
        {
            window.Close();
            tray.Dispose();
            await controller.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void EntranceDefersPreviewWorkAndReopeningResumesIt() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        string root = Path.Combine(Path.GetTempPath(), "vnotch-entrance-preview-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = ImageBytes(255) })).Entry!;
            tray.Attach(controller);
            Field<DispatcherTimer>(tray, "_searchTimer").Stop();
            SetField(tray, "_queryDirty", false);
            Invoke(tray, "ReconcileCards", (object)new[] { entry });
            tray.BeginEntrance();
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var card = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Single();
            Assert.True(Field<DispatcherTimer>(tray, "_entranceWorkTimer").IsEnabled);
            Assert.Empty(Field<IDictionary>(tray, "_thumbnailLoads"));
            Assert.Null(card.Image);
            tray.BeginExit();
            Assert.False(Field<DispatcherTimer>(tray, "_entranceWorkTimer").IsEnabled);
            tray.BeginEntrance();
            await WpfFrameWaiter.UntilAsync(() => card.Image != null, "preview resumes after reopening");
            Assert.False(Field<DispatcherTimer>(tray, "_entranceWorkTimer").IsEnabled);
            tray.BeginEntrance();
            tray.Visibility = Visibility.Collapsed;
            Assert.False(Field<DispatcherTimer>(tray, "_entranceWorkTimer").IsEnabled);
            Assert.Null(card.Image);
        }
        finally
        {
            window.Close();
            tray.Dispose();
            await controller.DisposeAsync();
            AnimationConfig.SetReduceMotion(previous);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void SelectAllIncludesUnrealizedItemsWithoutLoadingTheirPreviews() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var entries = Enumerable.Range(0, 5000).Select(i => new ClipboardEntry { Title = $"File {i}", Kind = ClipboardKind.File }).ToArray();
        Invoke(tray, "ReconcileCards", (object)entries);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ClipboardCardListBox)tray.FindName("Cards");
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(4999));
            Assert.True((bool)Invoke(tray, "SelectAllTrayItems", tray.FindName("CategoryPanel"))!);
            Assert.Equal(5000, list.SelectedItems.Count);
            Assert.Null(list.ItemContainerGenerator.ContainerFromIndex(4999));
            Assert.All(list.SelectedItems.Cast<ClipboardCardViewModel>(), card => Assert.Null(card.Image));
            list.UnselectAll();
            Assert.False((bool)Invoke(tray, "SelectAllTrayItems", tray.FindName("SearchBox"))!);
            Assert.Empty(list.SelectedItems);

            // A pending request selects the completed result set, not just the old page.
            typeof(ClipboardTray).GetField("_pendingSelectAll", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(tray, ("All", ""));
            Invoke(tray, "ReconcileCards", (object)entries.Concat(new[] { new ClipboardEntry { Kind = ClipboardKind.File } }).ToArray());
            Invoke(tray, "CompletePendingSelectAll");
            Assert.Equal(5001, list.SelectedItems.Count);
            list.UnselectAll();
            typeof(ClipboardTray).GetField("_pendingSelectAll", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(tray, ("Files", "different query"));
            Invoke(tray, "CompletePendingSelectAll");
            Assert.Empty(list.SelectedItems);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void LargeResultsBatchChangesAndKeepSelectionAndScroll() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        var entries = Enumerable.Range(0, 5000).Select(i => new ClipboardEntry { Title = $"File {i}", Kind = ClipboardKind.File }).ToArray();
        var notifications = new List<NotifyCollectionChangedAction>();
        cards.CollectionChanged += (_, e) => notifications.Add(e.Action);
        Invoke(tray, "ReconcileCards", (object)entries);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifications);
        Assert.Equal(5000, cards.Count);
        Assert.Same(cards[0].CardBrush, cards[^1].CardBrush);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ClipboardCardListBox)tray.FindName("Cards");
            var scroll = Descendants(list).OfType<ScrollViewer>().First();
            var selected = new[] { cards[0], cards[100] };
            list.ApplySelection(selected);
            scroll.ScrollToHorizontalOffset(6000);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            double offset = scroll.HorizontalOffset;
            Assert.True(offset > 0);
            Assert.InRange(Field<HashSet<ListBoxItem>>(tray, "_selectionContainers").Count, 1, 30);
            notifications.Clear();
            Invoke(tray, "ReconcileCards", (object)entries.Reverse().ToArray());
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifications);
            Assert.True(selected.ToHashSet().SetEquals(list.SelectedItems.Cast<ClipboardCardViewModel>()));
            Assert.Same(selected[0], cards[^1]);
            Assert.Equal(offset, scroll.HorizontalOffset, 2);
            notifications.Clear();
            // A large rotation has just one descending index boundary, but
            // moving each item individually would still be quadratic.
            var rotated = cards.Skip(2500).Concat(cards.Take(2500)).Select(card => card.Entry).ToArray();
            Invoke(tray, "ReconcileCards", (object)rotated);
            Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifications);
            // A normal live insertion still retains the small-change path.
            notifications.Clear();
            Invoke(tray, "ReconcileCards", (object)new[] { new ClipboardEntry() }.Concat(rotated).ToArray());
            Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, notifications);
            Assert.Equal(5001, cards.Count);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void OnlyViewportPreviewsLoadAndScrollingOrHidingReleasesThem() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-lazy-ui-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var slots = Field<SemaphoreSlim>(tray, "_thumbnailSlots");
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        bool held = false;
        try
        {
            await store.InitializeAsync();
            var entries = new List<ClipboardEntry>();
            for (int i = 0; i < 24; i++)
                entries.Add((await store.ImportAsync(new ClipboardCapture { ImagePng = ImageBytes((byte)i) })).Entry!);
            entries.AddRange(Enumerable.Range(0, 976).Select(i => new ClipboardEntry { Kind = ClipboardKind.File, Title = $"File {i}" }));
            tray.Attach(controller);
            Field<DispatcherTimer>(tray, "_searchTimer").Stop();
            SetField(tray, "_queryDirty", false);
            Invoke(tray, "ReconcileCards", entries);
            await slots.WaitAsync();
            await slots.WaitAsync();
            held = true;
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
            var list = (ListBox)tray.FindName("Cards");
            var scroll = Descendants(list).OfType<ScrollViewer>().First();
            var visible = VisibleCards(tray);
            Assert.InRange(visible.Length, 1, 5);
            Assert.InRange(Field<IDictionary>(tray, "_thumbnailLoads").Count, 1, 5);
            Assert.All(cards, card => Assert.Null(card.Image));
            slots.Release(2);
            held = false;
            await WpfFrameWaiter.UntilAsync(() => visible.All(card => card.Image != null), "visible previews load");
            Assert.All(cards.Except(visible), card => Assert.Null(card.Image));
            var firstImage = visible[0].Image;
            visible[0].SourceIcon = firstImage;
            scroll.ScrollToHorizontalOffset(2200);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await WpfFrameWaiter.UntilAsync(() => visible.All(card => card.Image == null && card.SourceIcon == null), "old previews are released");
            var next = VisibleCards(tray);
            await WpfFrameWaiter.UntilAsync(() => next.All(card => card.Image != null), "next previews load");
            Assert.InRange(cards.Count(card => card.Image != null), 1, 5);
            scroll.ScrollToHorizontalOffset(0);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await WpfFrameWaiter.UntilAsync(() => visible.All(card => card.Image != null), "returning previews reload");
            Assert.NotSame(firstImage, visible[0].Image);
            tray.Visibility = Visibility.Collapsed;
            Assert.All(cards, card => { Assert.Null(card.Image); Assert.Null(card.SourceIcon); });
            Assert.Empty(Field<IDictionary>(tray, "_cardViewportStates"));
        }
        finally
        {
            if (held) slots.Release(2);
            window.Close();
            tray.Dispose();
            controller.Dispose();
            store.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewportExitReversesWithoutLosingAReadyPreviewAndHonorsReduceMotion(bool reduceMotion) => SharedStaTestRunner.RunAsync(async () =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduceMotion);
        using var tray = new ClipboardTray();
        var card = new ClipboardCardViewModel(new ClipboardEntry { Kind = ClipboardKind.Image });
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 255, 255 }, 4);
        image.Freeze();
        card.Image = card.SourceIcon = image;
        Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(card);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var border = Descendants(tray).OfType<Border>().First(b => b.Width == 210 && ReferenceEquals(b.DataContext, card));
            var list = (ListBox)tray.FindName("Cards");
            var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(card);
            var visual = (Grid)border.Child;
            await WpfFrameWaiter.UntilAsync(() => visual.Opacity == 1, "card entrance settles");
            container.RenderTransform = new TranslateTransform(-5000, 0);
            container.UpdateLayout();
            Assert.False((bool)Invoke(tray, "IsCardInViewport", border)!);
            Invoke(tray, "RefreshCardViewport", border);
            if (reduceMotion)
            {
                Assert.Equal(0, visual.Opacity);
                Assert.Null(card.Image);
            }
            else
            {
                await WpfFrameWaiter.UntilAsync(() => visual.Opacity > 0 && visual.Opacity < .9, "card fades out");
                double opacity = visual.Opacity;
                Assert.Same(image, card.Image);
                container.RenderTransform = Transform.Identity;
                container.UpdateLayout();
                Invoke(tray, "RefreshCardViewport", border);
                await WpfFrameWaiter.NextAsync();
                Assert.InRange(visual.Opacity, opacity - .1, 1);
                await WpfFrameWaiter.UntilAsync(() => visual.Opacity == 1, "reversed entrance settles");
                Assert.Same(image, card.Image);
                container.RenderTransform = new TranslateTransform(-5000, 0);
                container.UpdateLayout();
                Invoke(tray, "RefreshCardViewport", border);
                Assert.Same(image, card.Image);
                await WpfFrameWaiter.UntilAsync(() => card.Image == null, "exit releases its preview");
                Assert.Equal(0, visual.Opacity);
                Assert.Null(card.SourceIcon);
            }
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void TallImagePreviewHasABoundedDecodeSize() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-tall-preview-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { ImagePng = ImageBytes(255, 256, 4096) })).Entry!;
            var card = new ClipboardCardViewModel(entry);
            tray.Attach(controller);
            Field<DispatcherTimer>(tray, "_searchTimer").Stop();
            SetField(tray, "_queryDirty", false);
            Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(card);
            window.Show();
            await WpfFrameWaiter.UntilAsync(() => card.Image != null, "portrait preview loads");
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(card.Image);
            Assert.InRange(bitmap.PixelWidth, 1, 420);
            Assert.InRange(bitmap.PixelHeight, 1, 420);
            Assert.True(bitmap.IsFrozen);
        }
        finally
        {
            window.Close();
            tray.Dispose();
            controller.Dispose();
            store.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void SearchAndEmptyMessageReversePendingDismissals() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var search = (TextBox)tray.FindName("SearchBox");
            var clear = (Button)tray.FindName("ClearSearchButton");
            var empty = (TextBlock)tray.FindName("EmptyText");
            search.Text = "@code";
            search.Clear();
            search.Text = "@image";
            Invoke(tray, "UpdateEmptyMessage", true, "First query");
            Invoke(tray, "UpdateEmptyMessage", false, "First query");
            Invoke(tray, "UpdateEmptyMessage", true, "Latest query");
            await WpfFrameWaiter.UntilAsync(() => empty.Opacity == 1 && clear.Opacity == 1, "search transitions settle");
            Assert.Equal(Visibility.Visible, clear.Visibility);
            Assert.True(clear.IsHitTestVisible);
            Assert.Equal(Visibility.Visible, empty.Visibility);
            Assert.Equal("Latest query", empty.Text);
            Invoke(tray, "UpdateEmptyMessage", false, "Latest query");
            search.Clear();
            await WpfFrameWaiter.UntilAsync(() => empty.Visibility == Visibility.Collapsed && clear.Visibility == Visibility.Collapsed, "search dismissals finish");
        }
        finally { window.Close(); }
    });

    private static ClipboardCardViewModel[] VisibleCards(ClipboardTray tray) => Field<HashSet<FrameworkElement>>(tray, "_realizedCardElements")
        .Where(element => (bool)Invoke(tray, "IsCardInViewport", element)!).Select(element => (ClipboardCardViewModel)element.DataContext).ToArray();

    private static byte[] ImageBytes(byte red, int width = 1, int height = 1)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i + 2] = red; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
