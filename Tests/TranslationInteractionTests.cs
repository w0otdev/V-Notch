using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationInteractionTests
{
    private static string LongResult => string.Join("\n", Enumerable.Repeat("A long translated paragraph that needs scrolling while keeping the menu within the screen.", 30));

    [Fact]
    public void LongResultMovesOnlyEnoughToFitAndUsesOneResultScrollArea() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            window.ShowSelection(new(1, "original", new Rect(work.Left + 100, work.Bottom - 450, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            await WpfFrameWaiter.NextAsync(ct);
            var before = Bounds(window);
            window.ShowResult(new(LongResult, "en", "vi", true));
            await WpfFrameWaiter.NextAsync(ct);
            await WpfFrameWaiter.UntilAsync(() => Bounds(window).Bottom <= work.Bottom + 1 && !window.IsPresentationAnimating,
                "result layout and placement settle inside the screen", ct);
            var after = Bounds(window);
            Assert.Equal(before.Left, after.Left);
            Assert.True(after.Top <= before.Top);
            Assert.True(after.Bottom <= work.Bottom + 1);
            Assert.True(window.ResultScroll.ScrollableHeight > 1);
            for (var parent = VisualTreeHelper.GetParent(window.ResultScroll); parent != null && parent != window.PopupSurface; parent = VisualTreeHelper.GetParent(parent))
                Assert.False(parent is System.Windows.Controls.ScrollViewer, "The result must not be nested in a second scroll viewer.");
            window.SetStatus("translation.working");
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(after.Top, Bounds(window).Top);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpansionGrowsFromTheChipAndDraggingDuringExpansionSurvivesTheResult(bool reduced) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduced);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            window.ShowSelection(new(1, "original", new Rect(work.Left + 120, work.Top + 120, 100, 20), IntPtr.Zero, false), false);
            await window.WaitForEntranceAsync(ct);
            var before = Bounds(window);
            double chipWidth = window.PopupSurface.ActualWidth;
            window.Expand();
            if (!reduced && SystemParameters.ClientAreaAnimation)
            {
                await WpfFrameWaiter.UntilAsync(() => window.PopupSurface.RenderTransform.Value.M11 is > .2 and < .9, "spring grows from the chip", ct);
                Assert.InRange(window.PopupSurface.Opacity, .99, 1);
            }
            else await window.WaitForEntranceAsync(ct);
            Assert.True(window.PopupSurface.ActualWidth > chipWidth * 2);
            Assert.Equal(before.Top, Bounds(window).Top);
            Assert.Equal(before.Left, Bounds(window).Left);
            var pointer = new Point(before.Left + 70, before.Top + 60);
            Assert.True(window.BeginPopupDrag(pointer));
            window.MovePopupDrag(pointer + new Vector(80, 55));
            window.EndPopupDrag();
            var dragged = Bounds(window);
            Assert.Equal(before.Left + 80, dragged.Left);
            Assert.Equal(before.Top + 55, dragged.Top);
            window.ShowResult(new(LongResult, "en", "vi", true));
            await window.WaitForEntranceAsync(ct);
            await WpfFrameWaiter.NextAsync(ct);
            await WpfFrameWaiter.UntilAsync(() => Bounds(window).Bottom <= work.Bottom + 1 && !window.IsPresentationAnimating,
                "dragged result layout and placement settle inside the screen", ct);
            var after = Bounds(window);
            Assert.Equal(dragged.Left, after.Left);
            // A small desktop may require moving up just enough to fit the taller result.
            int expectedTop = Math.Clamp(dragged.Top, work.Top, Math.Max(work.Top, work.Bottom - (after.Bottom - after.Top)));
            Assert.Equal(expectedTop, after.Top);
            Assert.Equal(1, window.PopupSurface.RenderTransform.Value.M11, 3);
            Assert.Equal(1, window.PopupSurface.RenderTransform.Value.M22, 3);
            Assert.False(window.TranslationDragHandle.IsMouseCaptured);
            window.Dismiss();
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "dismiss after dragging", ct);
            window.ShowSelection(new(2, "new selection", new Rect(work.Left + 50, work.Top + 50, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            Assert.NotEqual(dragged.Left, Bounds(window).Left);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void ExpansionNearTheRightEdgeSettlesInsideTheScreenAndCanBeDismissedMidSpring() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var selection = new Rect(work.Right - 190, work.Top + 140, 100, 20);
            window.ShowSelection(new(1, "original", selection, IntPtr.Zero, false), false);
            await window.WaitForEntranceAsync(ct);
            window.Expand();
            await window.WaitForEntranceAsync(ct);
            await WpfFrameWaiter.UntilAsync(() => !window.IsPresentationAnimating, "expansion placement settles", ct);
            Assert.True(Bounds(window).Right <= work.Right + 1);
            Assert.Equal(0, window.PopupOffset.X, 3);
            var settled = Bounds(window);
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(settled.Left, Bounds(window).Left);
            Assert.Equal(0, window.PopupOffset.X, 3);

            window.Dismiss();
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "close expanded menu", ct);
            window.ShowSelection(new(2, "next", selection, IntPtr.Zero, false), false);
            await window.WaitForEntranceAsync(ct);
            window.Expand();
            await WpfFrameWaiter.NextAsync(ct);
            window.Dismiss();
            await WpfFrameWaiter.UntilAsync(() => !window.IsVisible, "dismiss interrupts expansion", ct);
            window.ShowSelection(new(3, "newest", selection, IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            Assert.True(window.IsVisible);
            Assert.Equal(1, window.PopupSurface.RenderTransform.Value.M11);
            Assert.Equal("newest", window.OriginalText.Text);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void EnablingReducedMotionSettlesAnActiveExpansion() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowSelection(new(1, "original", new Rect(150, 150, 100, 20), IntPtr.Zero, false), false);
            await window.WaitForEntranceAsync(ct);
            window.Expand();
            await WpfFrameWaiter.NextAsync(ct);
            AnimationConfig.SetReduceMotion(true);
            await window.WaitForEntranceAsync(ct);
            Assert.Equal(1, window.PopupSurface.RenderTransform.Value.M11);
            Assert.Equal(1, window.PopupSurface.RenderTransform.Value.M22);
            Assert.False(window.IsPresentationAnimating);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    private static Win32Interop.RECT Bounds(TranslationWindow window)
    {
        Assert.True(Win32Interop.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return rect;
    }
}
