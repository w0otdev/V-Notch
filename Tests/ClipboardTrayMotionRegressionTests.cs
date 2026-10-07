using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.ViewModels;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ClipboardTrayMotionRegressionTests
{
    [Fact]
    public void SmallScrollStepsSettleEvenWhenLayoutRoundsTheOffset() => RunWithCards(20, async (tray, list, cards) =>
    {
        AnimationConfig.SetReduceMotion(false);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        scroll.ScrollToHorizontalOffset(0);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        typeof(ClipboardTray).GetField("_cardsScrollTarget", flags)!.SetValue(tray, 1d);
        Invoke(tray, "StartCardsScrollFrames");
        var active = typeof(ClipboardTray).GetField("_cardsScrollActive", flags)!;
        var clock = typeof(ClipboardTray).GetField("_cardsFrameTime", flags)!;
        for (int frame = 0; frame < 100 && (bool)active.GetValue(tray)!; frame++)
        {
            clock.SetValue(tray, System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency / 1000);
            Invoke(tray, "Cards_ScrollFrame", null, EventArgs.Empty);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False((bool)active.GetValue(tray)!);
        Assert.InRange(scroll.HorizontalOffset, 0.5, 1.5);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScrollEdgesFadeAwayAtTheirRespectiveEndpoints(bool reducedMotion) => RunWithCards(20, async (tray, list, cards) =>
    {
        AnimationConfig.SetReduceMotion(reducedMotion);
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        async Task ScrollTo(double offset)
        {
            scroll.ScrollToHorizontalOffset(offset);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Invoke(tray, "UpdateEdgeFade", list);
        }

        await ScrollTo(scroll.ScrollableWidth / 2);
        await Task.Delay(220);
        await NextRenderFrame();
        var left = (Border)tray.FindName("CardsFadeLeft");
        var right = (Border)tray.FindName("CardsFadeRight");
        Assert.Null(list.OpacityMask);
        Assert.Equal(1d, left.Opacity);
        Assert.Equal(1d, right.Opacity);

        await ScrollTo(0);
        Assert.Null(list.OpacityMask);
        if (!reducedMotion) Assert.True(left.HasAnimatedProperties);
        await Task.Delay(220);
        await NextRenderFrame();
        Assert.Equal(0d, left.Opacity);
        Assert.Equal(1d, right.Opacity);

        await ScrollTo(scroll.ScrollableWidth);
        await Task.Delay(220);
        await NextRenderFrame();
        Assert.Null(list.OpacityMask);
        Assert.Equal(1d, left.Opacity);
        Assert.Equal(0d, right.Opacity);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CardLifecycleSettlesWithoutChangingLayoutAndCanReverse(bool reducedMotion) => RunWithCards(2, async (tray, list, cards) =>
    {
        AnimationConfig.SetReduceMotion(reducedMotion);
        var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(cards[0]);
        var size = item.RenderSize;
        var animate = typeof(ClipboardTray).GetMethod("AnimateCardLifecycle", BindingFlags.Static | BindingFlags.NonPublic)!;
        animate.Invoke(null, new object[] { item, true, true });
        await Task.Delay(300);
        await NextRenderFrame();
        Assert.Equal(1, item.Opacity);
        Assert.Equal(Matrix.Identity, item.RenderTransform.Value);
        Assert.Equal(size, item.RenderSize);

        animate.Invoke(null, new object[] { item, false, false });
        await Task.Delay(60);
        await NextRenderFrame();
        Assert.InRange(item.Opacity, 0, 0.99);
        if (reducedMotion) Assert.Equal(Matrix.Identity, item.RenderTransform.Value);
        animate.Invoke(null, new object[] { item, true, false });
        await Task.Delay(300);
        await NextRenderFrame();
        Assert.Equal(1, item.Opacity);
        Assert.Equal(Matrix.Identity, item.RenderTransform.Value);
        Assert.Equal(size, item.RenderSize);
    });

    [Fact]
    public void RemovingCardsMovesSurvivorsOnlyAfterTheirLayoutChanges() => RunWithCards(3, async (tray, list, cards) =>
    {
        var survivor = cards[2];
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(survivor);
        var originalMotion = container.RenderTransform;
        var remaining = new[] { survivor.Entry };

        await (Task)Invoke(tray, "AnimateCollectionChangesAsync", remaining, CancellationToken.None)!;
        Assert.Same(originalMotion, container.RenderTransform);
        Assert.Equal(0, container.RenderTransform.Value.OffsetX);
        Assert.Equal(1, container.Opacity);

        var positions = (Dictionary<Guid, double>)Invoke(tray, "CaptureCardPositions")!;
        Invoke(tray, "ReconcileCards", (object)remaining);
        Invoke(tray, "AnimateCardPositions", positions);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(survivor);
        var motion = Assert.IsType<TranslateTransform>(container.RenderTransform);
        Assert.True(motion.X > 0);
        Assert.Equal(0d, motion.GetAnimationBaseValue(TranslateTransform.XProperty));
        Assert.Null(Field<object?>(tray, "_pendingCardPositions"));
    });

    [Fact]
    public void CardPositionSnapshotsStayStableWhenTheViewportScrolls() => RunWithCards(20, async (tray, list, cards) =>
    {
        var before = (Dictionary<Guid, double>)Invoke(tray, "CaptureCardPositions")!;
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        scroll.ScrollToHorizontalOffset(100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(100, scroll.HorizontalOffset);

        var after = (Dictionary<Guid, double>)Invoke(tray, "CaptureCardPositions")!;
        Assert.True(before.ContainsKey(cards[0].Entry.Id));
        Assert.True(after.ContainsKey(cards[0].Entry.Id));
        Assert.Equal(before[cards[0].Entry.Id], after[cards[0].Entry.Id], 3);
    });

    [Fact]
    public void MetadataRefreshKeepsAnInFlightSlideRunning() => RunWithCards(2, async (tray, list, cards) =>
    {
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(cards[0]);
        var motion = StartSlide(container);
        var positions = (Dictionary<Guid, double>)Invoke(tray, "CaptureCardPositions")!;
        // Let the rendered position advance between the snapshot and the layout pass.
        await Task.Delay(40);
        var entries = cards.Select(card => card.Entry with { IsPinned = true }).ToArray();
        Invoke(tray, "ReconcileCards", (object)entries);
        Invoke(tray, "AnimateCardPositions", positions);
        list.InvalidateArrange();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Same(motion, container.RenderTransform);
        Assert.True(motion.HasAnimatedProperties);
        Assert.Null(Field<object?>(tray, "_pendingCardPositions"));
    });

    [Fact]
    public void ReorderingDuringASlideStartsFromTheRenderedPosition() => RunWithCards(3, async (tray, list, cards) =>
    {
        var first = cards[0];
        var container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first);
        StartSlide(container);
        await NextRenderFrame();
        var positions = (Dictionary<Guid, double>)Invoke(tray, "CaptureCardPositions")!;
        var reordered = new[] { cards[2].Entry, cards[0].Entry, cards[1].Entry };
        Invoke(tray, "ReconcileCards", (object)reordered);
        Invoke(tray, "AnimateCardPositions", positions);
        // LayoutUpdated can arrive before the items host has arranged its new slots.
        Invoke(tray, "ApplyCardPositionsAfterLayout", null, EventArgs.Empty);
        Assert.Same(positions, Field<object?>(tray, "_pendingCardPositions"));

        list.UpdateLayout();
        container = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(first);
        var motion = Assert.IsType<TranslateTransform>(container.RenderTransform);
        Assert.Equal(0d, motion.GetAnimationBaseValue(TranslateTransform.XProperty));
        // WPF initializes the replacement animation clock on the next render tick.
        await NextRenderFrame();
        double renderedX = container.TranslatePoint(new Point(), list).X;
        Assert.InRange(Math.Abs(renderedX - positions[first.Entry.Id]), 0, 100);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(motion, container.RenderTransform);
        Assert.Null(Field<object?>(tray, "_pendingCardPositions"));
    });

    [Fact]
    public void CancelledRemovalKeepsSurvivorMotionAndRestoresTheRemovedCard() => RunWithCards(2, async (tray, list, cards) =>
    {
        var removed = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(cards[0]);
        var survivor = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(cards[1]);
        var motion = StartSlide(survivor);
        using var cancellation = new CancellationTokenSource();

        var removal = (Task)Invoke(tray, "AnimateCollectionChangesAsync", new[] { cards[1].Entry }, cancellation.Token)!;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => removal);

        Assert.Same(motion, survivor.RenderTransform);
        Assert.True(motion.HasAnimatedProperties);
        Assert.Equal(1, survivor.Opacity);
        Assert.Equal(1d, removed.GetAnimationBaseValue(UIElement.OpacityProperty));
        // Cancellation reverses the exit from its presentation, rather than snapping.
        await Task.Delay(300);
        await NextRenderFrame();
        Assert.Equal(Matrix.Identity, removed.RenderTransform.Value);
        Assert.Equal(1, removed.Opacity);
    });

    private static TranslateTransform StartSlide(ListBoxItem container)
    {
        var motion = new TranslateTransform();
        container.RenderTransform = motion;
        motion.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(60, 0, TimeSpan.FromSeconds(10)) { FillBehavior = FillBehavior.Stop });
        return motion;
    }

    private static async Task NextRenderFrame()
    {
        var frame = new TaskCompletionSource();
        EventHandler handler = (_, _) => frame.TrySetResult();
        CompositionTarget.Rendering += handler;
        try { await frame.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { CompositionTarget.Rendering -= handler; }
    }

    private static void RunWithCards(int count, Func<ClipboardTray, ListBox, ObservableCollection<ClipboardCardViewModel>, Task> test)
        => SharedStaTestRunner.RunAsync(async () =>
        {
            bool reduceMotion = AnimationConfig.ReduceMotion;
            AnimationConfig.SetReduceMotion(false);
            using var tray = new ClipboardTray();
            var cards = Field<ObservableCollection<ClipboardCardViewModel>>(tray, "_cards");
            for (int index = 0; index < count; index++)
                cards.Add(new ClipboardCardViewModel(new ClipboardEntry { Title = $"File {index}", Kind = ClipboardKind.File }));
            var window = new BackgroundWindow { Content = tray, Width = 780, Height = 300 };
            try
            {
                window.Show();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                SetField(tray, "_animateResults", false);
                await test(tray, (ListBox)tray.FindName("Cards"), cards);
            }
            finally
            {
                window.Close();
                AnimationConfig.SetReduceMotion(reduceMotion);
            }
        });

    private static object? Invoke(ClipboardTray tray, string method, params object?[] args)
        => typeof(ClipboardTray).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tray, args);
    private static T Field<T>(ClipboardTray tray, string name)
        => (T)typeof(ClipboardTray).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tray)!;
    private static void SetField(ClipboardTray tray, string name, object value)
        => typeof(ClipboardTray).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(tray, value);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
