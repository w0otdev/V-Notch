using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MorphAnimationResourceTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly DependencyProperty Progress = (DependencyProperty)typeof(MorphingSettingsIcon)
        .GetField("ProgressProperty", Private)!.GetValue(null)!;
    private static readonly Geometry Square = Geometry.Parse("M0,0 L48,0 48,48 0,48 Z");
    private static readonly Geometry Triangle = Geometry.Parse("M24,0 L48,48 0,48 Z M18,18 L30,18 24,30 Z");

    [Fact]
    public void CompletionReleasesSamplingBuffersAndAnimationClock() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var icon = new RenderProbe();
        using var host = Load(icon);
        icon.MorphTo(Square, false);
        icon.MorphTo(Triangle, duration: TimeSpan.FromMilliseconds(60));
        Assert.NotEmpty(Field<Point[][]>(icon, "_from"));
        await WpfFrameWaiter.UntilAsync(() => !icon.HasAnimatedProperties, "morph releases its completed clock", ct);
        await WpfFrameWaiter.NextAsync(ct);
        Assert.False(icon.HasAnimatedProperties);
        AssertBuffersReleased(icon);
        Assert.Same(Field<Geometry>(icon, "_target"), Field<Geometry>(icon, "_display"));
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstantReplacementAndUnloadingReleaseInterruptedBuffers(bool unload) => SharedStaTestRunner.Run(() =>
    {
        var icon = new RenderProbe();
        using var host = Load(icon);
        icon.MorphTo(Square, false);
        icon.MorphTo(Triangle);
        Assert.NotEmpty(Field<Point[][]>(icon, "_from"));
        if (unload) icon.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
        else icon.MorphTo(Square, animate: false);
        AssertBuffersReleased(icon);
        Assert.False(icon.HasAnimatedProperties);
    });

    [Fact]
    public void RepaintingAnUnchangedFrameDoesNotRegenerateCustomGeometry() => SharedStaTestRunner.Run(() =>
    {
        var icon = new RenderProbe();
        using var host = Load(icon);
        icon.MorphTo(Square, false);
        int generated = 0;
        icon.MorphTo(Triangle, geometryAtProgress: _ => { generated++; return Triangle; });
        icon.BeginAnimation(Progress, null);
        icon.SetValue(Progress, .5);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) icon.Render(drawing);
        icon.Brush = Brushes.Red;
        using (var drawing = visual.RenderOpen()) icon.Render(drawing);
        Assert.Equal(1, generated);
        icon.SetValue(Progress, .6);
        using (var drawing = visual.RenderOpen()) icon.Render(drawing);
        Assert.Equal(2, generated);
    });

    private static void AssertBuffersReleased(RenderProbe icon)
    {
        Assert.Empty(Field<Point[][]>(icon, "_from"));
        Assert.Empty(Field<Point[][]>(icon, "_to"));
        Assert.Empty(Field<PointCollection[]>(icon, "_framePoints"));
        Assert.Empty(Field<Point[]>(icon, "_collapseCenters"));
        Assert.Null(typeof(MorphingSettingsIcon).GetField("_geometryAtProgress", Private)!.GetValue(icon));
    }

    private static HwndSource Load(RenderProbe icon)
    {
        var host = new HwndSource(new HwndSourceParameters("Morph memory regression")
        {
            Width = 56, Height = 56, WindowStyle = 0
        }) { RootVisual = icon };
        host.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        icon.Dispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        icon.Measure(new Size(56, 56));
        icon.Arrange(new Rect(0, 0, 56, 56));
        return host;
    }

    private static T Field<T>(RenderProbe icon, string name) =>
        (T)typeof(MorphingSettingsIcon).GetField(name, Private)!.GetValue(icon)!;

    private sealed class RenderProbe : MorphingSettingsIcon
    {
        public void Render(DrawingContext drawing) => OnRender(drawing);
    }
}
