using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.Translation;
using Xunit;
using Xunit.Abstractions;

namespace VNotch.Tests;

public sealed class TranslationPresentationTests(ITestOutputHelper output)
{
    [Fact]
    public void ResultArrivingDuringEntranceDoesNotMoveOrResizeTheVisibleWindow() => SharedStaTestRunner.RunAsync(async ct =>
    {
        bool previous = AnimationConfig.ReduceMotion;
        AnimationConfig.SetReduceMotion(false);
        var window = new TranslationWindow("en", "vi") { Opacity = 0, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        var frames = new List<(TimeSpan Time, int Left, int Top, int Width, int Height)>();
        var motion = typeof(TranslationWindow).GetField("_popupAnimating", BindingFlags.Instance | BindingFlags.NonPublic)!;
        void OnFrame(object? sender, EventArgs e)
        {
            if (!(bool)motion.GetValue(window)! || window.PopupSurface.Opacity <= .01) return;
            if (!Win32Interop.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) return;
            frames.Add((((RenderingEventArgs)e).RenderingTime, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        }
        CompositionTarget.Rendering += OnFrame;
        try
        {
            var work = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            window.ShowSelection(new(1, "selected text", new Rect(work.Left + 100, work.Bottom - 280, 100, 20), IntPtr.Zero, false), true);
            await WpfFrameWaiter.UntilAsync(() => window.PopupSurface.Opacity > .01, "entrance begins", ct);
            await Task.Delay(40, ct);
            window.ShowResult(new(string.Join("\n", Enumerable.Repeat("A translated sentence with enough text to grow the popup.", 14)), "en", "vi", true));
            await WpfFrameWaiter.UntilAsync(() => !(bool)motion.GetValue(window)!, "entrance settles", ct);
            var geometries = frames.Select(f => (f.Left, f.Top, f.Width, f.Height)).Distinct().ToArray();
            var intervals = frames.Zip(frames.Skip(1), (a, b) => (b.Time - a.Time).TotalMilliseconds).Where(ms => ms > 0).Order().ToArray();
            output.WriteLine($"Entrance frames: {frames.Count}; visible window geometries: {geometries.Length}; UI rendering p95 interval: {(intervals.Length == 0 ? 0 : intervals[(int)((intervals.Length - 1) * .95)]):F2} ms");
            if (SystemParameters.ClientAreaAnimation) Assert.Single(geometries);
            Assert.True(window.IsVisible);
            await WpfFrameWaiter.UntilAsync(() => window.ResultText.Text.Length > 0, "result becomes visible after entrance", ct);
            Assert.True(window.CopyButton.IsEnabled);
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
            window.Close();
            AnimationConfig.SetReduceMotion(previous);
        }
    });
}
