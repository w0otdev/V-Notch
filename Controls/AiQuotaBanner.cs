using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.Services.AiUsage;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch.Controls;

// Provider and allowance context stay together while the remaining quota animates.
public sealed class AiQuotaBanner : UserControl
{
    private readonly StackPanel _rows = new();
    private readonly List<MotionRow> _motionRows = [];
    private int _motionVersion;

    private sealed record MotionRow(Grid View, TranslateTransform Slide, ScaleTransform? LogoScale,
        RoundedQuotaFill Fill, PercentageText Percentage, double Target);

    private sealed class RoundedQuotaFill(Brush brush) : FrameworkElement
    {
        internal static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            "Progress", typeof(double), typeof(RoundedQuotaFill),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            double progress = (double)GetValue(ProgressProperty);
            if (!double.IsFinite(progress)) return;
            double width = RenderSize.Width * Math.Clamp(progress, 0, 1);
            double height = RenderSize.Height;
            if (width <= 0 || height <= 0) return;
            // Draw at the actual fill width so both caps keep a circular radius during animation.
            drawingContext.DrawRoundedRectangle(brush, null, new Rect(0, 0, width, height), height / 2, height / 2);
        }
    }

    private sealed class PercentageText : TextBlock
    {
        internal static readonly DependencyProperty PercentageProperty = DependencyProperty.Register(
            "Percentage", typeof(double), typeof(PercentageText), new PropertyMetadata(0d, OnPercentageChanged));

        private static void OnPercentageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
            ((PercentageText)sender).Text = ((double)args.NewValue).ToString("0.#", Loc.GetCulture()) + "%";
    }

    public AiQuotaBanner()
    {
        AiUsageTypography.Apply(this);
        Content = _rows;
        Focusable = false;
        IsHitTestVisible = false;
        Loaded += (_, _) => AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
        Unloaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
            CompleteMotion();
        };
        IsVisibleChanged += (_, args) => { if (args.NewValue is false) CompleteMotion(); };
    }

    internal void Present(IReadOnlyList<AiQuotaAlert> alerts)
    {
        CompleteMotion();
        _motionRows.Clear();
        _rows.Children.Clear();
        var shownProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alert in alerts.GroupBy(alert => alert.Provider, StringComparer.OrdinalIgnoreCase).SelectMany(group => group))
        {
            bool showProvider = shownProviders.Add(alert.Provider);
            var color = alert.ThresholdPercent == 20 ? Color.FromRgb(0xF0, 0x7C, 0x7C) : Color.FromRgb(0xF5, 0xB8, 0x4B);
            var accent = new SolidColorBrush(color);
            accent.Freeze();
            var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            var slide = new TranslateTransform();
            row.RenderTransform = slide;
            ScaleTransform? logoScale = null;
            if (showProvider)
            {
                var header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, shownProviders.Count > 1 ? 12 : 0, 0, 8)
                };
                logoScale = new ScaleTransform(1, 1);
                var logo = new Image
                {
                    Source = AiProviderBrand.For(alert.Provider), Width = 28, Height = 28,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
                    RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = logoScale
                };
                header.Children.Add(logo);
                header.Children.Add(new TextBlock
                {
                    Text = alert.Provider, FontSize = 14, FontWeight = FontWeights.SemiBold,
                    Foreground = UiPalette.PrimaryBrush, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                });
                _rows.Children.Add(header);
            }

            string windowLabel = Loc.Get(alert.Window.DurationMinutes switch
            {
                300 => "aiUsage.fiveHour",
                10080 => "aiUsage.sevenDay",
                _ => alert.Window.LabelKey
            });
            if (!string.IsNullOrEmpty(alert.Window.Bucket) &&
                !string.Equals(alert.Window.Bucket, alert.Provider, StringComparison.OrdinalIgnoreCase))
                windowLabel += " · " + alert.Window.Bucket;
            var cycle = new TextBlock
            {
                Text = windowLabel, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = UiPalette.PrimaryBrush,
                Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            row.Children.Add(cycle);

            var context = new TextBlock
            {
                Text = Loc.Get("aiUsage.remaining"), FontSize = 11.5, Foreground = UiPalette.SecondaryBrush,
                Margin = new Thickness(0, 3, 8, 0), TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(context, 1);
            row.Children.Add(context);
            var track = new Border
            {
                Height = 7, CornerRadius = new CornerRadius(3.5),
                Background = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                BorderThickness = new Thickness(.5),
                Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Center
            };
            var fill = new RoundedQuotaFill(accent);
            fill.SetValue(RoundedQuotaFill.ProgressProperty, alert.Window.RemainingPercent / 100);
            track.Child = fill;
            Grid.SetColumnSpan(track, 2);
            Grid.SetRow(track, 2);
            row.Children.Add(track);
            string percentage = alert.Window.RemainingPercent.ToString("0.#", Loc.GetCulture()) + "%";
            var value = new PercentageText
            {
                Text = percentage, FontSize = 22, FontWeight = FontWeights.SemiBold,
                Foreground = accent, VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            value.SetValue(PercentageText.PercentageProperty, alert.Window.RemainingPercent);
            Grid.SetColumn(value, 1);
            Grid.SetRowSpan(value, 2);
            row.Children.Add(value);
            AutomationProperties.SetName(row, alert.Provider + ", " + windowLabel + ", " +
                Loc.Get("aiUsage.left", alert.Window.RemainingPercent.ToString("0.#", Loc.GetCulture())));
            _rows.Children.Add(row);
            _motionRows.Add(new(row, slide, logoScale, fill, value, alert.Window.RemainingPercent));
        }
    }

    internal void PlayEntrance()
    {
        CompleteMotion();
        if (AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation) return;
        int version = _motionVersion;
        for (int i = 0; i < _motionRows.Count; i++)
        {
            var row = _motionRows[i];
            int delay = 50 + Math.Min(i, 5) * 30;
            Animate(row.View, OpacityProperty, 0, 1, 180, delay, _easeAppleOut, version);
            Animate(row.Slide, TranslateTransform.YProperty, 4, 0, 300, delay, _easeAppleOut, version);
            if (row.LogoScale is { } logoScale)
            {
                Animate(logoScale, ScaleTransform.ScaleXProperty, .92, 1, 360, delay, _easeSoftSpring, version);
                Animate(logoScale, ScaleTransform.ScaleYProperty, .92, 1, 360, delay, _easeSoftSpring, version);
            }
            // Fill and number use the same curve so the allowance always reads as one movement.
            Animate(row.Fill, RoundedQuotaFill.ProgressProperty, 0, row.Target / 100, 420, delay + 45, _easeAppleOut, version);
            Animate(row.Percentage, PercentageText.PercentageProperty, 0, row.Target, 420, delay + 45, _easeAppleOut, version);
        }
    }

    internal void PlayExit(bool immediate)
    {
        ++_motionVersion;
        if (immediate || AnimationConfig.ReduceMotion || !AnimationConfig.ClientAreaAnimation)
        {
            CompleteMotion();
            return;
        }
        int version = _motionVersion;
        foreach (var row in _motionRows)
        {
            // Keep the quota itself steady while the readout withdraws.
            row.Fill.BeginAnimation(RoundedQuotaFill.ProgressProperty, null);
            row.Fill.SetValue(RoundedQuotaFill.ProgressProperty, row.Target / 100);
            row.Percentage.BeginAnimation(PercentageText.PercentageProperty, null);
            row.Percentage.SetValue(PercentageText.PercentageProperty, row.Target);
            Animate(row.View, OpacityProperty, row.View.Opacity, 0, 120, 0, _easeAppleOut, version);
            Animate(row.Slide, TranslateTransform.YProperty, row.Slide.Y, -2, 160, 0, _easeAppleOut, version);
        }
    }

    private void Animate(IAnimatable target, DependencyProperty property, double from, double to,
        int milliseconds, int delay, IEasingFunction easing, int version)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, to);
        var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delay > 0)
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(to,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay + milliseconds)), easing));
        Timeline.SetDesiredFrameRate(animation, Math.Min(60, AnimationConfig.TargetFps));
        animation.Completed += (_, _) =>
        {
            if (version == _motionVersion) target.BeginAnimation(property, null);
        };
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void CompleteMotion()
    {
        ++_motionVersion;
        foreach (var row in _motionRows)
        {
            row.View.BeginAnimation(OpacityProperty, null);
            row.View.Opacity = 1;
            row.Slide.BeginAnimation(TranslateTransform.YProperty, null);
            row.Slide.Y = 0;
            if (row.LogoScale is { } logoScale)
            {
                logoScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                logoScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                logoScale.ScaleX = logoScale.ScaleY = 1;
            }
            row.Fill.BeginAnimation(RoundedQuotaFill.ProgressProperty, null);
            row.Fill.SetValue(RoundedQuotaFill.ProgressProperty, row.Target / 100);
            row.Percentage.BeginAnimation(PercentageText.PercentageProperty, null);
            row.Percentage.SetValue(PercentageText.PercentageProperty, row.Target);
        }
    }

    private void OnReduceMotionChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnReduceMotionChanged()); return; }
        if (AnimationConfig.ReduceMotion) CompleteMotion();
    }
}
