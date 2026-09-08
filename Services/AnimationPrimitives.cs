using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VNotch.Services;

internal static class AnimationPrimitives
{
    #region Cached Easing Functions (Frozen - Thread Safe)

    public static readonly ExponentialEase _easeExpOut7 = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 7 });
    public static readonly ExponentialEase _easeExpOut6 = Freeze(new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 });
    public static readonly QuadraticEase _easeQuadOut = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    public static readonly QuadraticEase _easeQuadIn = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    public static readonly QuadraticEase _easeQuadInOut = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseInOut });
    public static readonly PowerEase _easePowerIn2 = Freeze(new PowerEase { EasingMode = EasingMode.EaseIn, Power = 2 });
    public static readonly PowerEase _easePowerOut3 = Freeze(new PowerEase { EasingMode = EasingMode.EaseOut, Power = 3 });
    public static readonly ElasticEase _easeSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 8 });
    public static readonly ElasticEase _easeSoftSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 3 });
    public static readonly ElasticEase _easeMenuSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 4 });
    public static readonly ElasticEase _easeThumbSpring = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 6.5 });
    public static readonly SineEase _easeSineInOut = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });
    public static readonly ElasticEase _easeHapticBounce = Freeze(new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 1, Springiness = 5 });
    public static readonly CubicBezierEase _easeAppleOut =
        Freeze(new CubicBezierEase(0.32, 0.72, 0.0, 1.0) { EasingMode = EasingMode.EaseIn });

    public static readonly CubicBezierEase _easeAppleInOut =
        Freeze(new CubicBezierEase(0.4, 0.0, 0.2, 1.0) { EasingMode = EasingMode.EaseIn });

    public static readonly CubicBezierEase _easeAppleIn =
        Freeze(new CubicBezierEase(0.4, 0.0, 1.0, 1.0) { EasingMode = EasingMode.EaseIn });

    private static T Freeze<T>(T easing) where T : Freezable
    {
        easing.Freeze();
        return easing;
    }

    #endregion

    #region Cached Durations

    public static readonly Duration _dur600 = new(TimeSpan.FromMilliseconds(600));
    public static readonly Duration _dur500 = new(TimeSpan.FromMilliseconds(500));
    public static readonly Duration _dur450 = new(TimeSpan.FromMilliseconds(450));
    public static readonly Duration _dur400 = new(TimeSpan.FromMilliseconds(400));
    public static readonly Duration _dur350 = new(TimeSpan.FromMilliseconds(350));
    public static readonly Duration _dur250 = new(TimeSpan.FromMilliseconds(250));
    public static readonly Duration _dur200 = new(TimeSpan.FromMilliseconds(200));
    public static readonly Duration _dur150 = new(TimeSpan.FromMilliseconds(150));
    public static readonly Duration _dur100 = new(TimeSpan.FromMilliseconds(100));
    public static readonly Duration _dur80 = new(TimeSpan.FromMilliseconds(80));

    #endregion

    #region Animation Factories

    public static T WithFps<T>(T timeline) where T : Timeline
    {
        try
        {
            Timeline.SetDesiredFrameRate(timeline, AnimationConfig.TargetFps);
        }
        catch
        {
            return timeline;
        }

        if (timeline is TimelineGroup group)
        {
            foreach (var child in group.Children)
                WithFps(child);
        }

        return timeline;
    }

    public static void ApplyFpsToTree(DependencyObject root)
    {
        var styles = new HashSet<Style>();
        var templates = new HashSet<FrameworkTemplate>();
        var resources = new HashSet<ResourceDictionary>();

        void Visit(DependencyObject node)
        {
            if (node is FrameworkElement fe)
            {
                ApplyFpsToResources(fe.Resources, styles, templates, resources);
                ApplyFpsToStyle(fe.Style, styles, templates, resources);
                if (fe is Control control)
                    ApplyFpsToTemplate(control.Template, styles, templates, resources);
            }
            else if (node is FrameworkContentElement fce)
            {
                ApplyFpsToResources(fce.Resources, styles, templates, resources);
                ApplyFpsToStyle(fce.Style, styles, templates, resources);
            }

            int count;
            try { count = VisualTreeHelper.GetChildrenCount(node); }
            catch { return; }

            for (int i = 0; i < count; i++)
                Visit(VisualTreeHelper.GetChild(node, i));
        }

        ApplyFpsToResources(Application.Current?.Resources, styles, templates, resources);
        Visit(root);
    }

    private static void ApplyFpsToResources(ResourceDictionary? dictionary, HashSet<Style> styles,
        HashSet<FrameworkTemplate> templates, HashSet<ResourceDictionary> resources)
    {
        if (dictionary == null || !resources.Add(dictionary)) return;

        foreach (var merged in dictionary.MergedDictionaries)
            ApplyFpsToResources(merged, styles, templates, resources);

        foreach (var value in dictionary.Values)
        {
            switch (value)
            {
                case Timeline timeline:
                    WithFps(timeline);
                    break;
                case Style style:
                    ApplyFpsToStyle(style, styles, templates, resources);
                    break;
                case FrameworkTemplate template:
                    ApplyFpsToTemplate(template, styles, templates, resources);
                    break;
                case ResourceDictionary nested:
                    ApplyFpsToResources(nested, styles, templates, resources);
                    break;
            }
        }
    }

    private static void ApplyFpsToStyle(Style? style, HashSet<Style> styles,
        HashSet<FrameworkTemplate> templates, HashSet<ResourceDictionary> resources)
    {
        if (style == null || !styles.Add(style)) return;

        ApplyFpsToStyle(style.BasedOn, styles, templates, resources);
        ApplyFpsToTriggers(style.Triggers);

        foreach (SetterBase setterBase in style.Setters)
        {
            if (setterBase is Setter { Value: Style setterStyle })
                ApplyFpsToStyle(setterStyle, styles, templates, resources);
            else if (setterBase is Setter { Value: FrameworkTemplate setterTemplate })
                ApplyFpsToTemplate(setterTemplate, styles, templates, resources);
        }
    }

    private static void ApplyFpsToTemplate(FrameworkTemplate? template, HashSet<Style> styles,
        HashSet<FrameworkTemplate> templates, HashSet<ResourceDictionary> resources)
    {
        if (template == null || !templates.Add(template)) return;

        ApplyFpsToResources(template.Resources, styles, templates, resources);
        if (template is ControlTemplate controlTemplate)
            ApplyFpsToTriggers(controlTemplate.Triggers);
        else if (template is DataTemplate dataTemplate)
            ApplyFpsToTriggers(dataTemplate.Triggers);
    }

    private static void ApplyFpsToTriggers(TriggerCollection triggers)
    {
        foreach (TriggerBase trigger in triggers)
        {
#pragma warning disable S1871 // Trigger types share identical EnterActions/ExitActions patterns without a common WPF interface
            switch (trigger)
            {
                case Trigger t:
                    ApplyFpsToActions(t.EnterActions);
                    ApplyFpsToActions(t.ExitActions);
                    break;
                case MultiTrigger t:
                    ApplyFpsToActions(t.EnterActions);
                    ApplyFpsToActions(t.ExitActions);
                    break;
                case DataTrigger t:
                    ApplyFpsToActions(t.EnterActions);
                    ApplyFpsToActions(t.ExitActions);
                    break;
                case MultiDataTrigger t:
                    ApplyFpsToActions(t.EnterActions);
                    ApplyFpsToActions(t.ExitActions);
                    break;
                case EventTrigger t:
                    ApplyFpsToActions(t.Actions);
                    break;
            }
#pragma warning restore S1871
        }
    }

    private static void ApplyFpsToActions(System.Collections.IEnumerable actions)
    {
        foreach (var action in actions)
        {
            if (action is BeginStoryboard { Storyboard: { } storyboard })
                WithFps(storyboard);
        }
    }

    public static DoubleAnimation MakeAnim(double? from, double to, Duration duration, IEasingFunction? easing = null, int? fps = null)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, fps ?? AnimationConfig.TargetFps);
        return anim;
    }

    public static DoubleAnimation MakeAnim(double to, Duration duration, IEasingFunction? easing = null, int? fps = null)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, fps ?? AnimationConfig.TargetFps);
        return anim;
    }

    public static DoubleAnimation MakeAnim(double from, double to, Duration duration, IEasingFunction? easing, TimeSpan? beginTime)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = duration,
            EasingFunction = easing
        };

        if (beginTime.HasValue)
            anim.BeginTime = beginTime.Value;
        Timeline.SetDesiredFrameRate(anim, AnimationConfig.TargetFps);
        return anim;
    }

    #endregion
}

internal sealed class CubicBezierEase : EasingFunctionBase
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    public CubicBezierEase() { /* Required by WPF Freezable/XAML construction. */ }

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        X1 = x1; Y1 = y1; X2 = x2; Y2 = y2;
    }

    protected override double EaseInCore(double normalizedTime)
    {
        double x = Math.Clamp(normalizedTime, 0.0, 1.0);
        double t = SolveForT(x);
        return Sample(t, Y1, Y2);
    }

    private double SolveForT(double x)
    {
        double t = x;
        for (int i = 0; i < 8; i++)
        {
            double error = Sample(t, X1, X2) - x;
            if (error > -1e-5 && error < 1e-5) break;
            double slope = Derivative(t, X1, X2);
            if (slope > -1e-6 && slope < 1e-6) break;
            t -= error / slope;
        }
        return Math.Clamp(t, 0.0, 1.0);
    }

    private static double Sample(double t, double p1, double p2)
    {
        double mt = 1 - t;
        return (3 * mt * mt * t * p1) + (3 * mt * t * t * p2) + (t * t * t);
    }

    private static double Derivative(double t, double p1, double p2)
    {
        double mt = 1 - t;
        return (3 * mt * mt * p1) + (6 * mt * t * (p2 - p1)) + (3 * t * t * (1 - p2));
    }

    protected override Freezable CreateInstanceCore() => new CubicBezierEase(X1, Y1, X2, Y2);
}
