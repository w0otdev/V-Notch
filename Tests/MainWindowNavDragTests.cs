using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Microsoft.Extensions.DependencyInjection;
using VNotch.Models;
using VNotch.Services;
using VNotch.Tests.Fakes;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class MainWindowNavDragTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(1, 2)]
    public void DroppingTabReordersVisibleSlotsAndPersistsTheResult(int from, int to) => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var children = window.NavTabsStackPanel.Children.OfType<FrameworkElement>().ToList();
        var item = children[from];
        var expected = children.Select(x => x.Tag!.ToString()!).ToList();
        string moved = expected[from];
        expected.RemoveAt(from);
        expected.Insert(to, moved);
        var shadow = new DropShadowEffect();
        shadow.Freeze();
        item.Effect = shadow;
        Set(window, "_navDragItem", item);
        Set(window, "_isNavDragging", true);
        Set(window, "_dragInitialSlot", from);
        Set(window, "_dragTargetSlot", from);
        Invoke(window, "AnimateNavDragLift", item, true);
        Assert.False(((DropShadowEffect)item.Effect).IsFrozen);
        var translate = ((TransformGroup)item.RenderTransform).Children.OfType<TranslateTransform>().Single();
        translate.X = (to - from) * 26;
        Invoke(window, "UpdateNeighborSlotDisplacements");
        Assert.Equal(to, Field<int>(window, "_dragTargetSlot"));
        Invoke(window, "UpdateNeighborSlotDisplacements");
        Invoke(window, "EndNavDrag", true);
        Assert.False(Field<bool>(window, "_isNavDragging"));
        await WpfFrameWaiter.UntilAsync(() => Field<NotchSettings>(window, "_settings").NavTabOrder == string.Join(',', expected), "tab order persisted", ct);
        Assert.Equal(expected, window.NavTabsStackPanel.Children.OfType<FrameworkElement>().Select(x => x.Tag!.ToString()!));
        foreach (var child in children)
        {
            var group = (TransformGroup)child.RenderTransform;
            Assert.Equal(0, group.Children.OfType<TranslateTransform>().Single().X);
            Assert.Equal(1, group.Children.OfType<ScaleTransform>().Single().ScaleX);
            Assert.Equal(0, Panel.GetZIndex(child));
        }
        window.ApplyNavTabOrderAndVisibility();
        Assert.Equal(expected.Select(x => Enum.Parse<NotchView>(x)), window.GetActiveTabSequence());
    });

    [Theory]
    [InlineData(1, 39.9, 1)]
    [InlineData(1, 40.4, 2)]
    [InlineData(1, 11.9, 1)]
    [InlineData(1, 11.6, 0)]
    [InlineData(0, -999, 0)]
    [InlineData(3, 999, 3)]
    public void SlotHysteresisKeepsTabStableUntilTheThresholdIsCrossed(int current, double position, int expected) => SharedStaTestRunner.Run(() =>
    {
        Assert.Equal(expected, typeof(MainWindow).GetMethod("CalculateNavTargetSlotWithHysteresis", Private)!.Invoke(null, new object[] { current, position, 26d, 4 }));
    });

    [Fact]
    public void VisibilityKeepsHomeAndOrderNormalizesDuplicatesAndUnknownTabs() => SharedStaTestRunner.Run(() =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var settings = Field<NotchSettings>(window, "_settings");
        settings.NavTabOrder = "timer,Timer,unknown,AudioMixer";
        settings.VisibleNavTabs = "AudioMixer";
        window.ApplyNavTabOrderAndVisibility();
        Assert.Equal(new[] { NotchView.AudioMixer, NotchView.Media }, window.GetActiveTabSequence());
        Assert.Equal(Visibility.Collapsed, window.TimerIconButton.Visibility);
        Assert.Equal(Visibility.Collapsed, window.FileShelfIconButton.Visibility);
        Assert.Equal(Visibility.Visible, window.HomeIconButton.Visibility);
        window.NavTabsStackPanel.Children.Clear();
        Assert.Equal(new[] { NotchView.Media }, window.GetActiveTabSequence());
    });

    [Fact]
    public void CancelledAndTransformlessDragsAlwaysReleaseTheirVisualState() => SharedStaTestRunner.RunAsync(async ct =>
    {
        using var fixture = CreateFixture();
        var window = fixture.Window;
        var item = window.TimerIconButton;
        string initial = Field<NotchSettings>(window, "_settings").NavTabOrder;
        Set(window, "_navDragItem", item);
        Set(window, "_isNavDragging", true);
        Set(window, "_dragInitialSlot", 2);
        Set(window, "_dragTargetSlot", 2);
        Invoke(window, "AnimateNavDragLift", item, true);
        Invoke(window, "EndNavDrag", false);
        await WpfFrameWaiter.UntilAsync(() => Panel.GetZIndex(item) == 0, "cancelled drag settled", ct);
        Assert.Equal(initial, Field<NotchSettings>(window, "_settings").NavTabOrder);
        int completed = 0;
        var plain = new Border();
        Invoke(window, "AnimateNavDragDropSettle", plain, 10d, (Action)(() => completed++));
        plain.RenderTransform = new TransformGroup { Children = { new ScaleTransform() } };
        Invoke(window, "AnimateNavDragDropSettle", plain, 10d, (Action)(() => completed++));
        Assert.Equal(2, completed);
        Assert.Equal(0, Panel.GetZIndex(plain));
        Invoke(window, "AnimateElementToX", plain, 10d);
        Invoke(window, "EndNavDrag", false);
        Invoke(window, "CommitFinalTabOrder");
    });

    private static GreetingAcceptanceTests.MainWindowFixture CreateFixture() => new("en", greeting: false,
        configureSettings: settings => { settings.EnableLocalOnlyMode = true; settings.NavTabOrder = "Media,Secondary,Timer,AudioMixer"; settings.VisibleNavTabs = settings.NavTabOrder; },
        configureServices: services => services.AddSingleton<IMediaDetectionService>(new FakeMediaDetectionService()));
    private static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
}
