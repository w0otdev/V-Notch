using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class SettingsWindow
{
    private void UpdateSectionHeader()
    {
        if (!_navButtons.TryGetValue(_activeNav, out var button) ||
            button.Child is not StackPanel row || row.Children.Count < 2 ||
            row.Children[1] is not TextBlock label) return;

        SectionHeroTitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(TextBlock.Text))
        {
            Source = label,
            Mode = BindingMode.OneWay,
            NotifyOnTargetUpdated = true
        });
        System.Windows.Automation.AutomationProperties.SetName(SectionHeroIcon, label.Text);
        var icon = new GeometryGroup { FillRule = FillRule.Nonzero };
        CollectHeaderIcon(row.Children[0], icon);
        if (icon.Children.Count > 0) SectionHeroIcon.MorphTo(icon);
    }

    private void SectionHeroTitle_TargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not TextBlock title) return;
        if (title.RenderTransform is not TranslateTransform translate || translate.IsFrozen)
            title.RenderTransform = translate = new TranslateTransform();

        title.BeginAnimation(OpacityProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        title.Opacity = 1;
        translate.Y = 0;
        if (!IsLoaded || AnimationConfig.ReduceMotion) return;

        var duration = System.TimeSpan.FromMilliseconds(280);
        var fade = new DoubleAnimation(0, 1, duration);
        var slide = new DoubleAnimation(6, 0, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(slide, AnimationConfig.TargetFps);
        title.BeginAnimation(OpacityProperty, fade);
        translate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static void CollectHeaderIcon(UIElement element, GeometryGroup geometry)
    {
        switch (element)
        {
            case System.Windows.Shapes.Path path when path.Data != null:
                geometry.Children.Add(path.Data.Clone());
                break;
            case Viewbox viewbox when viewbox.Child != null:
                CollectHeaderIcon(viewbox.Child, geometry);
                break;
            case Panel panel:
                foreach (UIElement child in panel.Children) CollectHeaderIcon(child, geometry);
                break;
            case TextBlock glyph:
                var text = new FormattedText(glyph.Text, CultureInfo.CurrentUICulture,
                    glyph.FlowDirection, new Typeface(glyph.FontFamily, glyph.FontStyle,
                        glyph.FontWeight, glyph.FontStretch), 48, Brushes.White,
                    VisualTreeHelper.GetDpi(glyph).PixelsPerDip);
                geometry.Children.Add(text.BuildGeometry(new Point()));
                break;
        }
    }

}
