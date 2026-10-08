using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch.Controls;

public sealed class AiStopButton : Button
{
    private FrameworkElement? _revealSurface;
    private ScaleTransform? _revealScale;
    private ScaleTransform? _bodyScale;
    private FrameworkElement? _hoverOverlay;
    private FrameworkElement? _pressedOverlay;
    private FrameworkElement? _glow;
    private bool _shown;
    private bool _hiding;
    private int _presentationVersion;

    public event EventHandler? HideCompleted;

    public AiStopButton()
    {
        Loaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
            if (IsVisible && _shown) Reveal(fresh: true);
        };
        Unloaded += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
            ++_presentationVersion;
            ResetMotion();
            if (!_shown) CompleteHide(_presentationVersion);
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && IsLoaded && _shown) Reveal(fresh: true);
            else if (!IsVisible)
            {
                ++_presentationVersion;
                ResetMotion();
                if (!_shown && Visibility != Visibility.Collapsed) CompleteHide(_presentationVersion);
            }
        };
    }

    public void SetShown(bool shown)
    {
        if (_shown == shown && (shown ? Visibility == Visibility.Visible : _hiding || Visibility == Visibility.Collapsed)) return;
        _shown = shown;
        IsEnabled = shown;
        IsHitTestVisible = shown;
        if (!shown)
        {
            Hide();
            return;
        }

        bool alreadyVisible = IsVisible;
        Visibility = Visibility.Visible;
        ApplyTemplate();
        if (alreadyVisible && IsLoaded) Reveal(fresh: false);
    }

    public override void OnApplyTemplate()
    {
        ++_presentationVersion;
        ResetMotion();
        base.OnApplyTemplate();
        _revealSurface = GetTemplateChild("RevealSurface") as FrameworkElement;
        // WPF can freeze template transforms; each button owns mutable animation targets.
        _revealScale = CreateScale(_revealSurface);
        _bodyScale = CreateScale(GetTemplateChild("ButtonContent") as FrameworkElement);
        _hoverOverlay = GetTemplateChild("HoverOverlay") as FrameworkElement;
        _pressedOverlay = GetTemplateChild("PressedOverlay") as FrameworkElement;
        _glow = GetTemplateChild("BreathingGlow") as FrameworkElement;
        if (IsLoaded && IsVisible)
        {
            if (_shown) Reveal(fresh: true);
            else Hide();
        }
    }

    private static ScaleTransform? CreateScale(FrameworkElement? element)
    {
        if (element == null) return null;
        var scale = new ScaleTransform();
        element.RenderTransform = scale;
        return scale;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == IsMouseOverProperty || e.Property == IsPressedProperty || e.Property == IsEnabledProperty)
        {
            // Keyboard activation keeps instant feedback; pointer presses can ease back on release.
            bool animate = e.Property != IsPressedProperty || IsMouseOver;
            UpdateInteraction(animate);
        }
    }

    private void Reveal(bool fresh)
    {
        if (_revealSurface == null || !IsVisible) return;
        ++_presentationVersion;
        _hiding = false;
        bool reduced = AnimationConfig.ReduceMotion;
        if (fresh)
        {
            ResetMotion();
            _revealSurface.Opacity = 0;
            if (_revealScale != null && !reduced)
                _revealScale.ScaleX = _revealScale.ScaleY = 0.95;
        }
        Retarget(_revealSurface, OpacityProperty, 1, 200, animate: true);
        Retarget(_revealScale, ScaleTransform.ScaleXProperty, 1, 200, animate: !reduced);
        Retarget(_revealScale, ScaleTransform.ScaleYProperty, 1, 200, animate: !reduced);
        StartBreathing();
        UpdateInteraction(animate: !fresh);
    }

    private void Hide()
    {
        int version = ++_presentationVersion;
        _hiding = true;
        StopBreathing();
        if (!IsVisible || !IsLoaded || _revealSurface == null)
        {
            CompleteHide(version);
            return;
        }
        bool reduced = AnimationConfig.ReduceMotion;
        Retarget(_revealScale, ScaleTransform.ScaleXProperty, reduced ? 1 : 0.95, 160, animate: !reduced);
        Retarget(_revealScale, ScaleTransform.ScaleYProperty, reduced ? 1 : 0.95, 160, animate: !reduced);
        Retarget(_revealSurface, OpacityProperty, 0, 160, animate: true, () => CompleteHide(version));
    }

    private void CompleteHide(int version)
    {
        if (version != _presentationVersion || _shown) return;
        bool notify = Visibility != Visibility.Collapsed;
        _hiding = false;
        Visibility = Visibility.Collapsed;
        ResetMotion();
        if (notify) HideCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void StartBreathing()
    {
        if (_glow == null) return;
        StopBreathing();
        if (AnimationConfig.ReduceMotion || !_shown || !IsVisible || !IsLoaded) return;
        var breathing = new DoubleAnimation(0.18, 0.32, TimeSpan.FromMilliseconds(1600))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(breathing, AnimationConfig.TargetFps);
        _glow.BeginAnimation(OpacityProperty, breathing);
    }

    private void StopBreathing()
    {
        if (_glow == null) return;
        _glow.BeginAnimation(OpacityProperty, null);
        _glow.Opacity = 0.22;
    }

    private void UpdateInteraction(bool animate)
    {
        if (!IsLoaded || !IsVisible) return;
        bool hover = IsEnabled && IsMouseOver && !AreAnyTouchesOver && Stylus.CurrentStylusDevice == null;
        bool pressed = IsEnabled && IsPressed;
        double scale = AnimationConfig.ReduceMotion ? 1 : pressed ? 0.97 : hover ? 1.03 : 1;
        Retarget(_bodyScale, ScaleTransform.ScaleXProperty, scale, 160, animate);
        Retarget(_bodyScale, ScaleTransform.ScaleYProperty, scale, 160, animate);
        Retarget(_hoverOverlay, OpacityProperty, hover && !pressed ? 1 : 0, 160, animate);
        Retarget(_pressedOverlay, OpacityProperty, pressed ? 1 : 0, 160, animate);
    }

    private void OnReduceMotionChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(OnReduceMotionChanged));
            return;
        }
        if (!_shown)
        {
            CompleteHide(++_presentationVersion);
            return;
        }
        ResetMotion();
        StartBreathing();
        UpdateInteraction(animate: false);
    }

    private void ResetMotion()
    {
        StopBreathing();
        Retarget(_revealSurface, OpacityProperty, 1, 0, animate: false);
        Retarget(_revealScale, ScaleTransform.ScaleXProperty, 1, 0, animate: false);
        Retarget(_revealScale, ScaleTransform.ScaleYProperty, 1, 0, animate: false);
        Retarget(_bodyScale, ScaleTransform.ScaleXProperty, 1, 0, animate: false);
        Retarget(_bodyScale, ScaleTransform.ScaleYProperty, 1, 0, animate: false);
        Retarget(_hoverOverlay, OpacityProperty, 0, 0, animate: false);
        Retarget(_pressedOverlay, OpacityProperty, 0, 0, animate: false);
    }

    private static void Retarget(IAnimatable? target, DependencyProperty property, double value, int milliseconds, bool animate, Action? completed = null)
    {
        if (target is not DependencyObject dependency) return;
        double from = (double)dependency.GetValue(property);
        target.BeginAnimation(property, null);
        dependency.SetValue(property, value);
        if (!animate || Math.Abs(from - value) < 0.001)
        {
            completed?.Invoke();
            return;
        }
        var animation = new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = CubicBezierEase.FromEaseOutCurve(0.23, 1, 0.32, 1),
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, AnimationConfig.TargetFps);
        if (completed != null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
