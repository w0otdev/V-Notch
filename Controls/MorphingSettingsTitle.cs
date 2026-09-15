using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

public sealed class MorphingSettingsTitle : MorphingSettingsIcon
{
    protected override bool IsTextGeometry => true;
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MorphingSettingsTitle),
        new FrameworkPropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public MorphingSettingsTitle()
    {
        ClipToBounds = true;
        SizeChanged += (_, _) => UpdateTextGeometry();
        Loaded += (_, _) => UpdateTextGeometry();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == TextProperty) UpdateTextGeometry();
    }

    private void UpdateTextGeometry()
    {
        AutomationProperties.SetName(this, Text);
        if (string.IsNullOrEmpty(Text) || ActualWidth <= 0) return;
        var typeface = new Typeface(new FontFamily(
            "pack://application:,,,/V-Notch;component/Fonts/#SF Pro Display, Nirmala UI, Segoe UI"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var text = new FormattedText(Text, Loc.GetCulture(), FlowDirection,
            typeface, 28, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, ActualWidth),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        MorphTo(text.BuildGeometry(new Point(0, Math.Max(0, (ActualHeight - text.Height) / 2))));
    }
}
