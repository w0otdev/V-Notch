using System.Collections.ObjectModel;
using System.Diagnostics;
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
public sealed class ClipboardTrayInteractionTests
{
    [Fact]
    public void AgeNotifiesOnlyWhenLabelChangesAndSharesFont() => SharedStaTestRunner.Run(() =>
    {
        Loc.SetLanguage("en");
        DateTime copied = DateTime.UtcNow;
        var card = new ClipboardCardViewModel(new ClipboardEntry { CopiedUtc = copied });
        int notifications = 0;
        card.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(card.Age)) notifications++; };
        card.RefreshAge(copied.AddSeconds(30));
        Assert.Equal("Just now", card.Age);
        Assert.Equal(0, notifications);
        card.RefreshAge(copied.AddMinutes(3));
        Assert.Equal("3 min", card.Age);
        card.RefreshAge(copied.AddMinutes(3).AddSeconds(30));
        Assert.Equal(1, notifications);
        card.RefreshAge(copied.AddHours(3));
        Assert.Equal("3 h", card.Age);
        card.RefreshAge(copied.AddDays(3));
        Assert.Equal("3 d", card.Age);
        Assert.Same(card.ContentFont, new ClipboardCardViewModel(new ClipboardEntry()).ContentFont);
    });

    [Fact]
    public void SelectionBatchProducesOneNotificationAndNoneWhenUnchanged() => SharedStaTestRunner.Run(() =>
    {
        var list = new ClipboardCardListBox { SelectionMode = SelectionMode.Extended };
        object first = new(), second = new(), third = new();
        list.ItemsSource = new[] { first, second, third };
        int notifications = 0;
        list.SelectionChanged += (_, _) => notifications++;
        list.ApplySelection(new[] { first, second });
        Assert.Equal(1, notifications);
        list.ApplySelection(new[] { second, third });
        Assert.Equal(2, notifications);
        list.ApplySelection(new[] { second, third });
        Assert.Equal(2, notifications);
        Assert.Equal(new[] { second, third }, list.SelectedItems.Cast<object>());
    });

    [Fact]
    public void LassoUsesRealizedCardsAndInvalidatesBoundsAfterScrolling() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        for (int i = 0; i < 1000; i++) cards.Add(new ClipboardCardViewModel(new ClipboardEntry { Title = i.ToString() }));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            tray.UpdateLayout();
            var list = (ClipboardCardListBox)tray.FindName("Cards");
            var surface = (Grid)tray.FindName("SelectionSurface");
            var realized = Field<HashSet<ListBoxItem>>(tray, "_selectionContainers");
            Assert.InRange(realized.Count, 1, 30);
            SetField(tray, "_selectionStart", new Point(0, 0));
            SetField(tray, "_selectionBoundsDirty", true);
            Invoke(tray, "QueueSelectionPointer", new Point(surface.ActualWidth, surface.ActualHeight));
            Assert.Empty(list.SelectedItems);
            Invoke(tray, "ApplyPendingSelection");
            Assert.InRange(list.SelectedItems.Count, 1, 5);
            var original = list.SelectedItems.Cast<ClipboardCardViewModel>().ToHashSet();
            var bounds = Field<Dictionary<ClipboardCardViewModel, Rect>>(tray, "_selectionBounds");
            Assert.InRange(bounds.Count, 1, 5);
            Invoke(tray, "QueueSelectionPointer", new Point(0, 0));
            Invoke(tray, "QueueSelectionPointer", new Point(surface.ActualWidth, surface.ActualHeight));
            Invoke(tray, "ApplyPendingSelection");
            Assert.True(original.SetEquals(list.SelectedItems.Cast<ClipboardCardViewModel>()));
            var scroll = Descendants(list).OfType<ScrollViewer>().First();
            scroll.ScrollToHorizontalOffset(3000);
            list.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Invoke(tray, "ApplyPendingSelection");
            Assert.True(original.IsSubsetOf(list.SelectedItems.Cast<ClipboardCardViewModel>()));
            Assert.InRange(list.SelectedItems.Count, 14, 20);
            double before = scroll.HorizontalOffset;
            Invoke(tray, "ScrollSelection", 0.05);
            list.UpdateLayout();
            Invoke(tray, "ApplyPendingSelection");
            Assert.True(scroll.HorizontalOffset > before);
            Assert.Contains(cards[0], list.SelectedItems.Cast<ClipboardCardViewModel>());
            Invoke(tray, "QueueSelectionPointer", new Point(0, surface.ActualHeight));
            before = scroll.HorizontalOffset;
            Invoke(tray, "ScrollSelection", 0.05);
            list.UpdateLayout();
            Assert.True(scroll.HorizontalOffset < before);
            scroll.ScrollToHorizontalOffset(0);
            list.UpdateLayout();
            Invoke(tray, "QueueSelectionPointer", new Point(100, surface.ActualHeight));
            Invoke(tray, "ApplyPendingSelection");
            Assert.Single(list.SelectedItems);
            Assert.Same(cards[0], list.SelectedItems[0]);
            Invoke(tray, "EndSelection");
            before = scroll.HorizontalOffset;
            Invoke(tray, "ScrollSelection", 0.05);
            Assert.Equal(before, scroll.HorizontalOffset);
        }
        finally { Invoke(tray, "EndSelection"); window.Close(); }
    });

    [Fact]
    public void LassoAcceptsPressesInEmptyHeaderSpaceAndCardGutters() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(new ClipboardCardViewModel(new ClipboardEntry()));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            tray.UpdateLayout();
            var surface = (Grid)tray.FindName("SelectionSurface");
            var list = (ListBox)tray.FindName("Cards");
            var origin = list.TranslatePoint(new Point(), surface);
            foreach (var point in new[] { new Point(3, 16), new Point(3, 55),
                new Point(100, origin.Y - 5), new Point(100, origin.Y + list.ActualHeight + 6) })
            {
                var hit = Assert.IsAssignableFrom<UIElement>(surface.InputHitTest(point));
                SetField(tray, "_copyOnRelease", true);
                var press = Press(hit);
                Assert.True(press.Handled);
                // Background test windows reject native mouse capture. The routed
                // press still reaches BeginSelection and cancels card-copy intent.
                Assert.False(Field<bool>(tray, "_copyOnRelease"));
                Invoke(tray, "EndSelection");
            }
        }
        finally { Invoke(tray, "EndSelection"); window.Close(); }
    });

    [Fact]
    public void ExpandedLassoLeavesControlsAndModalPanelsInteractive() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(new ClipboardCardViewModel(new ClipboardEntry()));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            foreach (string name in new[] { "SearchSurface", "SearchBox", "CategoryScroll" })
            {
                SetField(tray, "_copyOnRelease", true);
                Press((UIElement)tray.FindName(name));
                Assert.True(Field<bool>(tray, "_copyOnRelease"));
                Assert.Null(Field<Point?>(tray, "_selectionStart"));
            }
            foreach (var button in Descendants(tray).OfType<Button>().Where(button => button.IsVisible))
            {
                SetField(tray, "_copyOnRelease", true);
                Press(button);
                Assert.Equal(!Equals(button.Tag, "more"), Field<bool>(tray, "_copyOnRelease"));
                Assert.Null(Field<Point?>(tray, "_selectionStart"));
            }
            var surface = (Grid)tray.FindName("SelectionSurface");
            foreach (string name in new[] { "LockPanel", "DetailPanel", "ApprovalPanel", "DeleteConfirmationPanel" })
            {
                var panel = (UIElement)tray.FindName(name);
                panel.Visibility = Visibility.Visible;
                SetField(tray, "_copyOnRelease", true);
                Press(surface);
                Assert.True(Field<bool>(tray, "_copyOnRelease"));
                Assert.Null(Field<Point?>(tray, "_selectionStart"));
                panel.Visibility = Visibility.Collapsed;
            }
        }
        finally { Mouse.Capture(null); Invoke(tray, "EndSelection"); window.Close(); }
    });

    [Fact]
    public void LassoFromAboveCardsUsesTrayCoordinates() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        for (int i = 0; i < 3; i++) cards.Add(new ClipboardCardViewModel(new ClipboardEntry()));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            tray.UpdateLayout();
            var surface = (Grid)tray.FindName("SelectionSurface");
            var list = (ListBox)tray.FindName("Cards");
            var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var origin = second.TranslatePoint(new Point(), surface);
            var start = new Point(0, 20);
            SetField(tray, "_selectionStart", start);
            SetField(tray, "_selectionBoundsDirty", true);
            Invoke(tray, "QueueSelectionPointer", new Point(origin.X + second.ActualWidth / 2, origin.Y + second.ActualHeight / 2));
            Invoke(tray, "ApplyPendingSelection");
            Assert.Equal(cards.Take(2), list.SelectedItems.Cast<ClipboardCardViewModel>());
            var rectangle = (Border)tray.FindName("SelectionRectangle");
            Assert.Equal(start.Y, Canvas.GetTop(rectangle));
            Assert.True(rectangle.Height > origin.Y - start.Y);
            Assert.Same(surface, ((Canvas)tray.FindName("SelectionOverlay")).Parent);
        }
        finally { Invoke(tray, "EndSelection"); window.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StraightGutterLassoSelectsCardsInBothDirectionsAndShrinks(bool below, bool reverse)
        => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        for (int i = 0; i < 3; i++) cards.Add(new ClipboardCardViewModel(new ClipboardEntry()));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            tray.UpdateLayout();
            var surface = (Grid)tray.FindName("SelectionSurface");
            var list = (ListBox)tray.FindName("Cards");
            var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var origin = first.TranslatePoint(new Point(), surface);
            double pitch = first.ActualWidth + first.Margin.Left + first.Margin.Right;
            double firstX = origin.X + first.ActualWidth / 2;
            double y = below ? origin.Y + first.ActualHeight + 12 : origin.Y - 8;
            var start = new Point(firstX + (reverse ? 2 * pitch : 0), y);
            SetField(tray, "_selectionStart", start);
            SetField(tray, "_selectionBoundsDirty", true);
            Invoke(tray, "QueueSelectionPointer", new Point(firstX + (reverse ? 0 : 2 * pitch), y));
            Invoke(tray, "ApplyPendingSelection");
            Assert.Equal(cards, list.SelectedItems.Cast<ClipboardCardViewModel>());
            var rectangle = (Border)tray.FindName("SelectionRectangle");
            Assert.Equal(Visibility.Visible, rectangle.Visibility);
            Assert.True(Canvas.GetTop(rectangle) <= origin.Y);
            Assert.True(Canvas.GetTop(rectangle) + rectangle.Height >= origin.Y + first.ActualHeight);

            Invoke(tray, "QueueSelectionPointer", new Point(firstX + pitch, y));
            Invoke(tray, "ApplyPendingSelection");
            Assert.Equal(reverse ? cards.Skip(1) : cards.Take(2), list.SelectedItems.Cast<ClipboardCardViewModel>());

            // The search/header row never becomes a selection lane.
            Invoke(tray, "EndSelection");
            SetField(tray, "_selectionStart", new Point(firstX, 20));
            SetField(tray, "_selectionBoundsDirty", true);
            Invoke(tray, "QueueSelectionPointer", new Point(firstX + 2 * pitch, 20));
            Invoke(tray, "ApplyPendingSelection");
            Assert.Empty(list.SelectedItems);
        }
        finally { Invoke(tray, "EndSelection"); window.Close(); }
    });

    [Fact]
    public void HoldingGutterAtEdgeDoesNotScrollOrSelectUntilDragging() => SharedStaTestRunner.RunAsync(async () =>
    {
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        for (int i = 0; i < 100; i++) cards.Add(new ClipboardCardViewModel(new ClipboardEntry()));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            tray.UpdateLayout();
            var surface = (Grid)tray.FindName("SelectionSurface");
            var list = (ListBox)tray.FindName("Cards");
            var scroll = Descendants(list).OfType<ScrollViewer>().First();
            var start = new Point(surface.ActualWidth, surface.ActualHeight - 12);
            SetField(tray, "_selectionStart", start);
            SetField(tray, "_selectionBoundsDirty", true);
            Invoke(tray, "QueueSelectionPointer", start);
            Invoke(tray, "ApplyPendingSelection");
            Invoke(tray, "ScrollSelection", 0.05);
            list.UpdateLayout();
            Assert.Equal(0, scroll.HorizontalOffset);
            Assert.Empty(list.SelectedItems);
            Assert.Equal(Visibility.Collapsed, ((Border)tray.FindName("SelectionRectangle")).Visibility);

            Invoke(tray, "QueueSelectionPointer", new Point(start.X + 12, start.Y));
            Invoke(tray, "ScrollSelection", 0.05);
            list.UpdateLayout();
            Invoke(tray, "ApplyPendingSelection");
            Assert.True(scroll.HorizontalOffset > 0);
            Assert.NotEmpty(list.SelectedItems);
        }
        finally { Invoke(tray, "EndSelection"); window.Close(); }
    });

    [Fact]
    public void ChildCaptureChangesDoNotCancelLassoAndSurfaceCaptureLossCleansUp() => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var surface = (Grid)tray.FindName("SelectionSurface");
        var start = new Point(40, 100);
        SetField(tray, "_selectionStart", start);
        surface.Cursor = Cursors.Cross;
        ((TextBox)tray.FindName("SearchBox")).RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Assert.Equal(start, Field<Point?>(tray, "_selectionStart"));
        Assert.Same(Cursors.Cross, surface.Cursor);

        surface.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
        { RoutedEvent = Mouse.LostMouseCaptureEvent });
        Assert.Null(Field<Point?>(tray, "_selectionStart"));
        Assert.Same(DependencyProperty.UnsetValue, surface.ReadLocalValue(FrameworkElement.CursorProperty));
    });

    [Theory]
    [InlineData(0, 780, -720)]
    [InlineData(24, 780, -180)]
    [InlineData(390, 780, 0)]
    [InlineData(756, 780, 180)]
    [InlineData(780, 780, 720)]
    [InlineData(900, 780, 720)]
    [InlineData(0, 0, 0)]
    public void LassoEdgeSpeedIsBoundedAndIncreasesNearEdge(double x, double width, double expected)
        => Assert.Equal(expected, ClipboardTray.SelectionScrollSpeed(x, width), 5);

    [Fact]
    public void VisibleCardAgesRefreshAndTimerStopsWhenHiddenOrDetached() => SharedStaTestRunner.RunAsync(async () =>
    {
        Loc.SetLanguage("en");
        using var tray = new ClipboardTray();
        DateTime copied = DateTime.UtcNow.AddMinutes(-2);
        var card = new ClipboardCardViewModel(new ClipboardEntry { CopiedUtc = copied });
        Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(card);
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var timer = Field<DispatcherTimer>(tray, "_ageTimer");
            Assert.True(timer.IsEnabled);
            Invoke(tray, "ClearSelectionContainers");
            Assert.Empty(Field<HashSet<ListBoxItem>>(tray, "_selectionContainers"));
            card.RefreshAge(copied.AddSeconds(30));
            Invoke(tray, "AgeTimer_Tick", null, EventArgs.Empty);
            Assert.Equal("2 min", card.Age);
            window.Hide();
            Assert.False(timer.IsEnabled);
            card.RefreshAge(copied.AddSeconds(30));
            window.Show();
            Assert.True(timer.IsEnabled);
            Assert.Equal("2 min", card.Age);
            window.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.False(timer.IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void GroupClicksKeepLastStateAfterCloseAndReopen() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-group-ui-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            await store.InitializeAsync();
            var entry = (await store.ImportAsync(new ClipboardCapture { Text = "test" })).Entry!;
            tray.Attach(controller);
            window.Show();
            await (Task)Invoke(tray, "ShowGroupChoicesAsync", new ClipboardCardViewModel(entry))!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var panel = (ItemsControl)tray.FindName("GroupChoices");
            var choices = Descendants(panel).OfType<CheckBox>().ToArray();
            var work = choices.First(choice => Equals(choice.Tag, "Work"));
            var ideas = choices.First(choice => Equals(choice.Tag, "Ideas"));
            Click(work, true);
            Click(ideas, true);
            Click(work, false);
            Invoke(tray, "CloseDetails");
            Assert.Empty(panel.Items);
            Assert.All(choices, choice => Assert.Null(choice.Tag));
            await (Task)Invoke(tray, "ShowGroupChoicesAsync", new ClipboardCardViewModel(entry))!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(new[] { "Ideas" }, Assert.Single(store.Snapshot()).Groups);
            var reopened = Descendants(panel).OfType<CheckBox>().ToArray();
            Assert.False(reopened.First(choice => Equals(choice.Tag, "Work")).IsChecked);
            Assert.True(reopened.First(choice => Equals(choice.Tag, "Ideas")).IsChecked);
            // Detached controls must not affect the newly opened editor.
            work.Tag = "Work";
            Click(work, true);
            await Field<Task>(tray, "_groupSave");
            Assert.Equal(new[] { "Ideas" }, Assert.Single(store.Snapshot()).Groups);
        }
        finally
        {
            window.Close();
            tray.Dispose();
            controller.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void RecycledCardCancelsOldLoadAndLoadsNewImage() => SharedStaTestRunner.RunAsync(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "vnotch-recycled-ui-" + Guid.NewGuid().ToString("N"));
        using var store = new ClipboardHistoryStore(root);
        using var controller = new ClipboardHistoryController(Dispatcher.CurrentDispatcher, store);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        var slots = Field<SemaphoreSlim>(tray, "_thumbnailSlots");
        bool held = false;
        try
        {
            await store.InitializeAsync();
            var firstEntry = (await store.ImportAsync(new ClipboardCapture { ImagePng = ImageBytes(255, 0) })).Entry!;
            var secondEntry = (await store.ImportAsync(new ClipboardCapture { ImagePng = ImageBytes(0, 255) })).Entry!;
            var first = new ClipboardCardViewModel(firstEntry);
            var second = new ClipboardCardViewModel(secondEntry);
            await slots.WaitAsync();
            await slots.WaitAsync();
            held = true;
            tray.Attach(controller);
            Field<DispatcherTimer>(tray, "_searchTimer").Stop();
            SetField(tray, "_queryDirty", false);
            Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards").Add(first);
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var border = Descendants(tray).OfType<Border>().First(element => element.Width == 210 && ReferenceEquals(element.DataContext, first));
            border.DataContext = second;
            slots.Release(2);
            held = false;
            var watch = Stopwatch.StartNew();
            while (second.Image == null && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            Assert.NotNull(second.Image);
            Assert.Null(first.Image);
            Assert.True(second.Image.IsFrozen);
        }
        finally
        {
            if (held) slots.Release(2);
            window.Close();
            tray.Dispose();
            controller.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void CategoryOverscrollIsBoundedAndStopsWhenHidden() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var tray = new ClipboardTray();
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var panel = (ItemsControl)tray.FindName("CategoryPanel");
            Invoke(tray, "SetCategoryOverscroll", 10000d);
            Assert.InRange(panel.RenderTransform.Value.OffsetX, -20, -19);
            Invoke(tray, "SetCategoryOverscroll", -10000d);
            Assert.InRange(panel.RenderTransform.Value.OffsetX, 19, 20);
            Invoke(tray, "EndCategoryDrag");
            Assert.True(Field<bool>(tray, "_categoryScrollActive"));
            window.Hide();
            Assert.False(Field<bool>(tray, "_categoryScrollActive"));
            Assert.Equal(0, panel.RenderTransform.Value.OffsetX);
            AnimationConfig.SetReduceMotion(true);
            Invoke(tray, "SetCategoryOverscroll", 10000d);
            Assert.Equal(0, panel.RenderTransform.Value.OffsetX);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reduced); }
    });

    [Fact]
    public void CancelingCardRemovalRestoresVisibleContainers() => SharedStaTestRunner.RunAsync(async () =>
    {
        bool reduced = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        using var tray = new ClipboardTray();
        var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
        cards.Add(new ClipboardCardViewModel(new ClipboardEntry()));
        cards.Add(new ClipboardCardViewModel(new ClipboardEntry()));
        var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            tray.UpdateLayout();
            SetField(tray, "_animateResults", false);
            using var cancellation = new CancellationTokenSource();
            var pending = (Task)Invoke(tray, "AnimateCollectionChangesAsync",
                new[] { cards[1].Entry }, cancellation.Token)!;
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            var list = (ClipboardCardListBox)tray.FindName("Cards");
            foreach (var card in cards)
            {
                var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(card);
                Assert.Equal(1, container.Opacity);
                Assert.True(container.RenderTransform.Value.IsIdentity);
            }
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(reduced); }
    });

    [Fact]
    public void PinDotsAnimateOnPasswordChangeAndReset() => SharedStaTestRunner.Run(() =>
    {
        bool originalReduce = AnimationConfig.ReduceMotion;
        try
        {
            using var tray = new ClipboardTray();
            var passcodeBox = (PasswordBox)tray.FindName("PasscodeBox");
            var dot0 = (Border)tray.FindName("PinDot0");
            var dot1 = (Border)tray.FindName("PinDot1");
            var dot3 = (Border)tray.FindName("PinDot3");

            Assert.NotNull(passcodeBox);
            Assert.NotNull(dot0);
            Assert.NotNull(dot1);
            Assert.NotNull(dot3);

            // Initially no dots entered
            Assert.Equal(0, dot0.Opacity);

            // Typing 4 digits with animated motion triggers animation on active dots
            passcodeBox.Password = "1234";
            Assert.True(dot0.HasAnimatedProperties);
            Assert.True(dot1.HasAnimatedProperties);
            Assert.True(dot3.HasAnimatedProperties);

            // With reduce motion, sets values immediately
            AnimationConfig.SetReduceMotion(true);
            passcodeBox.Password = "12";
            Assert.Equal(1.0, dot0.Opacity);
            Assert.Equal(1.0, dot1.Opacity);
            Assert.Equal(0.0, dot3.Opacity);

            // Clearing resets dots immediately
            Invoke(tray, "ResetPinDots", true);
            Assert.Equal(0.0, dot0.Opacity);
            Assert.Equal(0.0, dot3.Opacity);
        }
        finally
        {
            AnimationConfig.SetReduceMotion(originalReduce);
        }
    });

    [Fact]
    public void ForgotPinButtonAndDialogBordersFollowNewVisualStyles() => SharedStaTestRunner.Run(() =>
    {
        using var tray = new ClipboardTray();
        var forgotButton = (Button)tray.FindName("ForgotPinButton");
        var confirmBorder = (Border)tray.FindName("DeleteConfirmationBorder");

        Assert.NotNull(forgotButton);
        Assert.Equal(0, forgotButton.BorderThickness.Left);
        Assert.Equal(0, forgotButton.BorderThickness.Top);

        Assert.NotNull(confirmBorder);
        Assert.True(confirmBorder.BorderThickness.Left >= 1.5);
    });

    private static MouseButtonEventArgs Press(UIElement element)
    {
        var press = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Mouse.PreviewMouseDownEvent };
        element.RaiseEvent(press);
        return press;
    }

    private static byte[] ImageBytes(byte red, byte blue)
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { blue, 0, red, 255 }, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void Click(CheckBox choice, bool value)
    {
        choice.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, value);
        choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    private static object? Invoke(ClipboardTray tray, string method, params object?[] args) =>
        typeof(ClipboardTray).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, args);

    private static T Field<T>(ClipboardTray tray, string name) =>
        (T)typeof(ClipboardTray).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;

    private static void SetField(ClipboardTray tray, string name, object value) =>
        typeof(ClipboardTray).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tray, value);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
