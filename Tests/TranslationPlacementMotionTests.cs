using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class TranslationPlacementMotionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongResultRepositionsThroughIntermediateFramesAndKeepsActionsVisible(bool reduced) => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(reduced);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        var tops = new List<int>();
        void OnFrame(object? sender, EventArgs e)
        {
            if (window.IsVisible && window.ResultText.Text.Length > 0) tops.Add(Bounds(window).Top);
        }
        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            window.ShowSelection(new(1, "original", new Rect(work.Left + 100, work.Bottom - 350, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            await WpfFrameWaiter.NextAsync(ct);
            MoveNearBottom(window, work.Bottom);
            await WpfFrameWaiter.NextAsync(ct);
            int from = Bounds(window).Top;
            CompositionTarget.Rendering += OnFrame;
            window.ShowResult(new(LongResult, "en", "vi", true));
            await WpfFrameWaiter.NextAsync(ct);
            await WpfFrameWaiter.UntilAsync(() => Bounds(window).Bottom <= work.Bottom + 1 && !window.IsPresentationAnimating, "automatic position spring settles inside the screen", ct);
            await WpfFrameWaiter.NextAsync(ct);
            var settled = Bounds(window);
            Assert.True(settled.Top < from, $"The menu should grow and move up to fit: from={from}, final={settled.Top}, height={settled.Bottom - settled.Top}, screenBottom={work.Bottom}.");
            Assert.InRange(settled.Bottom, work.Top, work.Bottom + 1);
            if (!reduced && SystemParameters.ClientAreaAnimation)
                Assert.True(tops.Distinct().Count() >= 3, "Movement must render intermediate native positions, not teleport.");
            var actions = window.TranslationActions.PointToScreen(new Point(0, window.TranslationActions.ActualHeight));
            Assert.True(actions.Y <= work.Bottom);
            var title = window.TranslationDragHandle.PointToScreen(new Point());
            Assert.True(title.Y >= work.Top);
            Assert.True(window.ResultScroll.ScrollableHeight > 1);
            Assert.Equal(260, window.ResultScroll.ActualHeight, 0);
            window.ResultScroll.ScrollToEnd();
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(settled.Top, Bounds(window).Top);
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
            window.Close();
            AnimationConfig.SetReduceMotion(previous);
        }
    });

    [Fact]
    public void DraggingInterruptsAnAutomaticMoveAndTheOldClockCannotOverrideTheDrop() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            window.ShowSelection(new(1, "original", new Rect(work.Left + 100, work.Bottom - 350, 100, 20), IntPtr.Zero, false), true);
            await window.WaitForEntranceAsync(ct);
            await WpfFrameWaiter.NextAsync(ct);
            MoveNearBottom(window, work.Bottom);
            await WpfFrameWaiter.NextAsync(ct);
            window.ShowResult(new(LongResult, "en", "vi", true));
            await WpfFrameWaiter.NextAsync(ct);
            if (SystemParameters.ClientAreaAnimation)
                await WpfFrameWaiter.UntilAsync(() => window.IsPresentationAnimating, "automatic movement begins before the drag", ct);
            var before = Bounds(window);
            var pointer = new Point(before.Left + 60, before.Top + 60);
            Assert.True(window.BeginPopupDrag(pointer));
            Assert.False(window.IsPresentationAnimating);
            var delta = new Vector(work.Left + 120 - before.Left, work.Top + 80 - before.Top);
            window.MovePopupDrag(pointer + delta);
            window.EndPopupDrag();
            var dropped = Bounds(window);
            // Allow the replaced clock's original completion deadline to pass.
            await Task.Delay(500, ct);
            await WpfFrameWaiter.NextAsync(ct);
            Assert.Equal(dropped.Left, Bounds(window).Left);
            Assert.Equal(dropped.Top, Bounds(window).Top);
            Assert.False(window.IsPresentationAnimating);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    [Fact]
    public void TightHeightShrinksOnlyTheResultViewportAndKeepsTheButtonsAccessible() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(true);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.ShowClipboard("original");
            await window.WaitForEntranceAsync(ct);
            window.ShowResult(new(LongResult, "en", "vi", true));
            await WpfFrameWaiter.NextAsync(ct);
            window.TranslationPanel.MaxHeight = 300;
            await WpfFrameWaiter.UntilAsync(() => window.ResultScroll.ActualHeight < 260, "result viewport shrinks to the available height", ct);
            Assert.InRange(window.ResultScroll.ActualHeight, 30, 259);
            Assert.True(window.ResultScroll.ScrollableHeight > 1);
            var actions = window.TranslationActions.TransformToAncestor(window.TranslationPanel).Transform(new Point(0, window.TranslationActions.ActualHeight));
            Assert.True(actions.Y <= window.TranslationPanel.ActualHeight + 1);
        }
        finally { window.Close(); AnimationConfig.SetReduceMotion(previous); }
    });

    private static string LongResult => string.Join("\n", Enumerable.Repeat("A translated paragraph long enough to require result scrolling, with all actions still visible.", 30));

    private static void MoveNearBottom(TranslationWindow window, int screenBottom)
    {
        var rect = Bounds(window);
        var pointer = new Point(rect.Left + 60, rect.Top + 60);
        Assert.True(window.BeginPopupDrag(pointer));
        window.MovePopupDrag(pointer + new Vector(0, screenBottom - rect.Bottom - 24));
        window.EndPopupDrag();
    }

    private static Win32Interop.RECT Bounds(TranslationWindow window)
    {
        Assert.True(Win32Interop.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect));
        return rect;
    }

}
