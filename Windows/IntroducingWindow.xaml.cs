using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

/// <summary>Consent first, followed by a small modeless guide beside the real notch.</summary>
public partial class IntroducingWindow : Window
{
    internal static readonly string[] Steps = { "welcome", "media", "shelf", "audio", "search", "settings" };
    private readonly Action<int> _stepChanged;
    private int _step = -1;
    private bool _transitioning;
    private bool _closing;
    private bool _closeReady;
    private readonly TranslateTransform _contentOffset = new();
    private readonly TranslateTransform _outgoingOffset = new();
    private Storyboard? _pageTransition;
    private double _destinationLeft = double.NaN;
    private double _destinationTop = double.NaN;
    private bool Animate => IsLoaded && SystemParameters.ClientAreaAnimation && !AnimationConfig.ReduceMotion;

    private static DoubleAnimation Motion(double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = AnimationPrimitives._easeAppleOut,
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, Math.Min(AnimationConfig.TargetFps, 60));
        return animation;
    }
    internal bool IsGuiding => _step >= 0;
    internal int StepIndex => _step;

    public IntroducingWindow(Action<int> stepChanged)
    {
        InitializeComponent();
        _stepChanged = stepChanged;
        StepContent.RenderTransform = _contentOffset;
        OutgoingContent.RenderTransform = _outgoingOffset;
        Loaded += (_, _) =>
        {
            if (!Animate) return;
            CoachSurface.BeginAnimation(OpacityProperty, Motion(0, 1, 190));
        };
        AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;
        Closed += (_, _) =>
        {
            AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
            _pageTransition?.Remove(this);
            _pageTransition = null;
        };
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        FlowDirection = Loc.GetCulture().TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Title = Loc.Get("tour.title");
        MaxHeight = SystemParameters.WorkArea.Height;
        MaxWidth = SystemParameters.WorkArea.Width;
        StepText.Text = Loc.Get("tour.title");
        HeadlineText.Text = Loc.Get("tour.consent.title");
        BodyText.Text = Loc.Get("tour.consent.body");
        BackButton.Content = Loc.Get("tour.consent.explore");
        NextButton.Content = Loc.Get("tour.consent.start");
        CloseButton.ToolTip = Loc.Get("tour.skip");
    }

    internal void SetProgress(bool completed, string? unavailable = null)
    {
        var text = unavailable ?? Loc.Get(completed ? "tour.live.done" : "tour.live.hint");
        if (HintText.Text == text) return;
        HintText.Text = text;
    }

    private void PrepareStepContent()
    {
        StepText.Text = Loc.Get("tour.progress", _step + 1, Steps.Length);
        HeadlineText.Text = Loc.Get($"tour.{Steps[_step]}.title");
        BodyText.Text = Loc.Get($"tour.{Steps[_step]}.body");
        BackButton.Content = Loc.Get("tour.back");
        BackButton.IsEnabled = _step > 0;
        NextButton.Content = Loc.Get(_step == Steps.Length - 1 ? "tour.finish" : "tour.next");
        SetProgress(false);
    }

    private void CompleteStep()
    {
        _transitioning = false;
        _stepChanged(_step);
    }

    private void RenderStep()
    {
        PrepareStepContent();
        CompleteStep();
    }

    private void ChangeStep(int direction)
    {
        if (_transitioning || _closing) return;
        if (!Animate)
        {
            _step += direction;
            ProgressScale.ScaleX = (double)(_step + 1) / Steps.Length;
            RenderStep();
            return;
        }
        _transitioning = true;
        // Remember the visible position of the old page before resetting the scroll offset.
        double previousScroll = StepScroller.VerticalOffset;
        OutgoingHeadline.Text = HeadlineText.Text;
        OutgoingBody.Text = BodyText.Text;
        OutgoingHint.Text = HintText.Text;
        // Match the width of the old scroll viewport, including when its scrollbar was showing.
        // Otherwise the outgoing copy wraps differently and visibly jumps during the crossfade.
        OutgoingContent.Width = StepScroller.ViewportWidth > 0
            ? StepScroller.ViewportWidth : StepScroller.ActualWidth;
        OutgoingContent.Visibility = Visibility.Visible;
        OutgoingContent.Opacity = 1;
        _outgoingOffset.Y = -previousScroll;
        StepContent.Opacity = 0;
        _contentOffset.Y = 8 * direction;
        _step += direction;
        PrepareStepContent();
        // Restore the top if the previous language/description required scrolling.
        StepScroller.ScrollToTop();

        // Keep final animated values until the completion handler commits the base values.
        // FillBehavior.Stop briefly restores opacity 0/1 and offset 8 on the last frame.
        var transition = new Storyboard { FillBehavior = FillBehavior.HoldEnd };
        void Track(DependencyObject target, DependencyProperty property, double from, double to, int duration)
        {
            var animation = Motion(from, to, duration);
            animation.FillBehavior = FillBehavior.HoldEnd;
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, new PropertyPath(property));
            transition.Children.Add(animation);
        }
        Track(OutgoingContent, OpacityProperty, 1, 0, 195);
        Track(_outgoingOffset, TranslateTransform.YProperty, -previousScroll, -previousScroll - 6 * direction, 260);
        Track(StepContent, OpacityProperty, 0, 1, 260);
        Track(_contentOffset, TranslateTransform.YProperty, 8 * direction, 0, 300);
        Track(ProgressScale, ScaleTransform.ScaleXProperty, ProgressScale.ScaleX,
            (double)(_step + 1) / Steps.Length, 300);
        ProgressScale.ScaleX = (double)(_step + 1) / Steps.Length;
        Timeline.SetDesiredFrameRate(transition, Math.Min(AnimationConfig.TargetFps, 60));
        _pageTransition = transition;
        transition.Completed += (_, _) =>
        {
            if (_closing || !ReferenceEquals(_pageTransition, transition)) return;
            _pageTransition = null;
            // Commit the final state while the clocks still hold their end values.
            // Remove the outgoing page before releasing its opacity clock.
            OutgoingContent.Visibility = Visibility.Collapsed;
            OutgoingContent.Opacity = 0;
            _outgoingOffset.Y = 0;
            _contentOffset.Y = 0;
            StepContent.Opacity = 1;
            transition.Remove(this);
            CompleteStep();
        };
        transition.Begin(this, true);
    }

    private void OnReduceMotionChanged()
    {
        if (!AnimationConfig.ReduceMotion) return;
        if (_closing)
        {
            _closeReady = true;
            Close();
            return;
        }
        if (_pageTransition == null) return;
        _pageTransition.Remove(this);
        _pageTransition = null;
        OutgoingContent.Visibility = Visibility.Collapsed;
        _outgoingOffset.Y = 0;
        _contentOffset.Y = 0;
        StepContent.Opacity = 1;
        ProgressScale.ScaleX = (double)(_step + 1) / Steps.Length;
        CompleteStep();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_closeReady && Animate)
        {
            e.Cancel = true;
            if (!_closing)
            {
                _closing = true;
                _pageTransition?.Remove(this);
                _pageTransition = null;
                _transitioning = false;
                OutgoingContent.Visibility = Visibility.Collapsed;
                StepContent.Opacity = 1;
                IsHitTestVisible = false;
                var fade = Motion(CoachSurface.Opacity, 0, 170);
                fade.Completed += (_, _) => { _closeReady = true; Close(); };
                CoachSurface.BeginAnimation(OpacityProperty, fade);
            }
        }
        base.OnClosing(e);
    }

    internal void MoveBeside(double left, double top)
    {
        // Moving a native window every frame causes expensive HWND updates.
        // Snap on meaningful anchor changes and ignore polling jitter.
        if (Math.Abs(_destinationLeft - left) < 4 && Math.Abs(_destinationTop - top) < 4) return;
        _destinationLeft = left;
        _destinationTop = top;
        Left = left;
        Top = top;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (!IsGuiding) { Close(); return; }
        if (_step > 0) ChangeStep(-1);
    }
    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step == Steps.Length - 1) { Close(); return; }
        ChangeStep(1);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }
}
