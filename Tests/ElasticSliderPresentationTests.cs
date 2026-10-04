using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using VNotch.Controls;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ElasticSliderPresentationTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData(FlowDirection.LeftToRight, Key.Left, 45)]
    [InlineData(FlowDirection.RightToLeft, Key.Left, 55)]
    [InlineData(FlowDirection.LeftToRight, Key.Right, 55)]
    [InlineData(FlowDirection.RightToLeft, Key.Right, 45)]
    [InlineData(FlowDirection.LeftToRight, Key.Up, 55)]
    [InlineData(FlowDirection.RightToLeft, Key.Down, 45)]
    [InlineData(FlowDirection.LeftToRight, Key.Home, 0)]
    [InlineData(FlowDirection.LeftToRight, Key.End, 100)]
    public void KeyboardChangesValueInTheCorrectDirection(FlowDirection direction, Key key, double expected) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var slider = Create();
        slider.FlowDirection = direction;
        slider.Value = 50;
        var args = KeyArgs(key);
        Invoke(slider, "OnPreviewKeyDown", args);
        Assert.True(args.Handled);
        Assert.Equal(expected, slider.Value);
        var root = Part<Border>(slider, "PART_Root");
        var fill = Part<Border>(slider, "PART_Fill");
        await WpfFrameWaiter.UntilAsync(() => Math.Abs(fill.Width - root.ActualWidth * expected / 100) < 0.001, "slider fill follows keyboard value", ct);
        Assert.Equal(((int)expected).ToString(), Part<TextBlock>(slider, "PART_Value").Text);
        slider.Value = 100;
        Invoke(slider, "OnPreviewKeyDown", KeyArgs(Key.Up));
        Assert.Equal(100, slider.Value);
        slider.Value = 0;
        Invoke(slider, "OnPreviewKeyDown", KeyArgs(Key.Down));
        Assert.Equal(0, slider.Value);
        Invoke(slider, "OnPreviewKeyDown", KeyArgs(Key.F12));
    });

    [Fact]
    public void TemplatePublishesLabelsTicksAndLocalizedAutomaticFps() => SharedStaTestRunner.RunAsync(async ct =>
    {
        var slider = Create();
        slider.Label = "Animation speed";
        slider.Description = "Choose a frame rate";
        slider.Unit = " fps";
        Assert.Equal(slider.Label, AutomationProperties.GetName(slider));
        Assert.Equal(slider.Description, AutomationProperties.GetHelpText(slider));
        Assert.Equal(slider.Label, Part<TextBlock>(slider, "PART_Label").Text);
        Assert.Equal(Visibility.Visible, Part<TextBlock>(slider, "PART_Description").Visibility);
        Assert.Equal(Loc.Get("settings.fps.auto"), Part<TextBlock>(slider, "PART_Value").Text);
        var ticks = Part<Canvas>(slider, "PART_Ticks");
        Assert.Equal(21, ticks.Children.Count);
        Assert.Equal(14, ((Rectangle)ticks.Children[0]).Height);
        Assert.Equal(8, ((Rectangle)ticks.Children[1]).Height);
        slider.Description = "";
        Assert.Equal(Visibility.Collapsed, Part<TextBlock>(slider, "PART_Description").Visibility);
        slider.Unit = "%";
        slider.Value = 32;
        Assert.Equal(30, slider.Value);
        Assert.Equal("30%", Part<TextBlock>(slider, "PART_Value").Text);
        slider.Minimum = 10;
        slider.Maximum = 60;
        Assert.Equal(11, ticks.Children.Count);
        slider.TickFrequency = 0;
        Invoke(slider, "DrawTicks");
        Assert.Equal(21, ticks.Children.Count);
        var root = Part<Border>(slider, "PART_Root");
        root.Arrange(new Rect(0, 0, 10, 48));
        Invoke(slider, "DrawTicks");
        Assert.Empty(ticks.Children);
        root.Arrange(new Rect(0, 0, 400, 48));
        Invoke(slider, "SetFillImmediate");
        Assert.Equal(160, Part<Border>(slider, "PART_Fill").Width);
        await WpfFrameWaiter.NextAsync(ct);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OverscrollReleaseRestoresScaleAndCenteredOrigin(bool fromLeft) => SharedStaTestRunner.RunAsync(async ct =>
    {
        var slider = Create();
        var root = Part<Border>(slider, "PART_Root");
        Invoke(slider, "AnimateScaleImmediate", 1.04, 0.92, fromLeft);
        Assert.Equal(new Point(fromLeft ? 0 : 1, 0.5), root.RenderTransformOrigin);
        Invoke(slider, "AnimateRubberBandRelease");
        await WpfFrameWaiter.UntilAsync(() => root.RenderTransformOrigin == new Point(0.5, 0.5), "rubber band restores centered origin", ct);
        var scale = Assert.IsType<ScaleTransform>(root.RenderTransform);
        Assert.Equal(1, scale.ScaleX);
        Assert.Equal(1, scale.ScaleY);
    });

    [Fact]
    public void MissingTemplatePartsAndZeroRangeRemainSafe() => SharedStaTestRunner.Run(() =>
    {
        var slider = new ElasticSlider { Minimum = 5, Maximum = 5 };
        slider.OnApplyTemplate();
        slider.Label = "label";
        slider.Description = "description";
        slider.Unit = "%";
        Invoke(slider, "AnimateScaleImmediate", 1.05, 0.9, false);
        Invoke(slider, "AnimateRubberBandRelease");
        Assert.Equal(1, Invoke(slider, "GetTickCount"));
        Assert.Equal(1d, Invoke(slider, "GetEffectiveTickStep"));
        Assert.Equal(5, slider.Value);
    });

    private static ElasticSlider Create()
    {
        const string template = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:controls="clr-namespace:VNotch.Controls;assembly=V-Notch"
                TargetType="controls:ElasticSlider">
              <Border x:Name="PART_Root"><Grid>
                <Border x:Name="PART_Fill" Width="0" HorizontalAlignment="Left"/>
                <Canvas x:Name="PART_Ticks"/>
                <Canvas><Rectangle x:Name="PART_Indicator" Width="3" Height="16"/></Canvas>
                <TextBlock x:Name="PART_Label"/><TextBlock x:Name="PART_Description"/>
                <TextBlock x:Name="PART_Value" HorizontalAlignment="Right"/>
              </Grid></Border>
            </ControlTemplate>
            """;
        var slider = new ElasticSlider
        {
            Minimum = 0,
            Maximum = 100,
            TickFrequency = 5,
            IsSnapToTickEnabled = true,
            Template = (ControlTemplate)XamlReader.Parse(template)
        };
        slider.ApplyTemplate();
        slider.Measure(new Size(400, 48));
        slider.Arrange(new Rect(0, 0, 400, 48));
        slider.UpdateLayout();
        return slider;
    }
    private static T Part<T>(ElasticSlider slider, string name) => (T)slider.Template.FindName(name, slider);
    private static object? Invoke(ElasticSlider slider, string name, params object?[] args) => typeof(ElasticSlider).GetMethod(name, Private)!.Invoke(slider, args);
    private static KeyEventArgs KeyArgs(Key key) => new(Keyboard.PrimaryDevice, new TestPresentationSource(), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
    private sealed class TestPresentationSource : PresentationSource
    {
        public override Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override CompositionTarget GetCompositionTargetCore() => null!;
    }
}
