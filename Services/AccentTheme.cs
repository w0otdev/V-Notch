using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VNotch.Services;

// WPF presentation resources shared by settings and the app's other windows.
internal static class AccentTheme
{
    internal sealed record Swatch(string Id, Color Color, Color Strong);

    internal static readonly Color ForegroundColor = Color.FromRgb(34, 37, 45);

    internal static IReadOnlyList<Swatch> Swatches { get; } = Array.AsReadOnly<Swatch>(
    [
        new("default", Color.FromRgb(215, 217, 222), Color.FromRgb(228, 230, 235)),
        new("blue", Color.FromRgb(139, 168, 200), Color.FromRgb(174, 193, 216)),
        new("purple", Color.FromRgb(176, 160, 200), Color.FromRgb(201, 189, 219)),
        new("pink", Color.FromRgb(207, 153, 184), Color.FromRgb(222, 183, 205)),
        new("red", Color.FromRgb(204, 150, 154), Color.FromRgb(221, 178, 181)),
        new("orange", Color.FromRgb(206, 172, 142), Color.FromRgb(222, 195, 171)),
        new("yellow", Color.FromRgb(199, 186, 135), Color.FromRgb(217, 206, 168)),
        new("green", Color.FromRgb(146, 180, 159), Color.FromRgb(178, 203, 190)),
        new("teal", Color.FromRgb(136, 180, 181), Color.FromRgb(173, 202, 204)),
    ]);

    internal static Swatch GetSwatch(string? id) => Swatches[id switch
    {
        "blue" => 1, "purple" => 2, "pink" => 3, "red" => 4,
        "orange" => 5, "yellow" => 6, "green" => 7, "teal" => 8,
        _ => 0,
    }];

    internal static void Apply(string? id, bool animate = false)
    {
        if (Application.Current is not { } app) return;
        var swatch = GetSwatch(id);
        UpdateBrush(app.Resources, "AccentBrush", swatch.Color, animate);
        UpdateBrush(app.Resources, "AccentBrushStrong", swatch.Strong, animate);
        // Every swatch uses a light fill, so keep text and icons dark for contrast.
        UpdateBrush(app.Resources, "AccentForegroundBrush", ForegroundColor, animate);
        UpdateBrush(app.Resources, "AccentHoverBrush", swatch.Strong, animate);
        UpdateBrush(app.Resources, "AccentSubtleBrush", Color.FromArgb(30, swatch.Color.R, swatch.Color.G, swatch.Color.B), animate);
        UpdateBrush(app.Resources, "AccentNavHoverBrush", Color.FromArgb(24, swatch.Color.R, swatch.Color.G, swatch.Color.B), animate);
        UpdateBrush(app.Resources, "AccentNavHoverBorderBrush", Color.FromArgb(48, swatch.Color.R, swatch.Color.G, swatch.Color.B), animate);
    }

    private static void UpdateBrush(ResourceDictionary resources, string key, Color target, bool animate)
    {
        if (resources[key] is not SolidColorBrush brush)
        {
            resources[key] = new SolidColorBrush(target);
            return;
        }

        // Applying the same settings again must not restart or finish an ongoing fade.
        if ((Color)brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty) == target)
        {
            if (!AnimationConfig.ReduceMotion || !brush.HasAnimatedProperties) return;
        }

        Color from = brush.Color;
        // Never modify a published brush: WPF can seal it during resource invalidation.
        var replacement = new SolidColorBrush(target);
        if (animate && !AnimationConfig.ReduceMotion && from != target)
        {
            var transition = new ColorAnimation(from, target, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = AnimationPrimitives._easePowerOut3,
                FillBehavior = FillBehavior.Stop,
            };
            Timeline.SetDesiredFrameRate(transition, Math.Min(60, AnimationConfig.TargetFps));
            var clock = transition.CreateClock();
            replacement.ApplyAnimationClock(SolidColorBrush.ColorProperty, clock, HandoffBehavior.SnapshotAndReplace);
            clock.Controller!.SeekAlignedToLastTick(TimeSpan.Zero, TimeSeekOrigin.BeginTime);
        }

        // ResourceDictionary freezes idle brushes on insertion. Attach the fade before publishing.
        resources[key] = replacement;
    }

    internal static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
