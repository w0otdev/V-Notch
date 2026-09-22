using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    private IntroducingWindow? _tutorialWindow;
    private DispatcherTimer? _tutorialWatchTimer;
    private TutorialHighlightAdorner? _tutorialHighlight;
    private AdornerLayer? _tutorialAdornerLayer;
    private readonly List<(TutorialHighlightAdorner Highlight, AdornerLayer Layer)> _tutorialExitingHighlights = new();
    private FrameworkElement? _tutorialTarget;
    private int _tutorialStep = -1;
    private readonly bool[] _tutorialCompleted = new bool[6];
    private bool _tutorialPrepared;
    private NotchView _tutorialOriginalView;
    private DateTime _tutorialOriginalHoverSuppression;

    internal void ShowTutorial()
    {
        if (_tutorialWindow != null) { _tutorialWindow.Activate(); return; }
        Array.Clear(_tutorialCompleted);
        _tutorialStep = -1;
        _tutorialWindow = new IntroducingWindow(BeginTutorialStep) { Owner = this };
        _tutorialWindow.Closed += (_, _) =>
        {
            bool wasGuiding = _tutorialStep >= 0;
            _tutorialWindow = null;
            _tutorialWatchTimer?.Stop();
            _tutorialWatchTimer = null;
            RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(TutorialObservedClick));
            SetTutorialHighlight(null);
            _tutorialStep = -1;
            if (wasGuiding)
            {
                _suppressHoverCollapseUntilUtc = _tutorialOriginalHoverSuppression;
                if (!_spotlightMorphSessionActive && !_spotlightMorphOwnsNotchVisibility)
                    _transitionCoordinator.RequestView(_tutorialOriginalView, "TutorialClosed");
            }
            _settings.HasSeenTutorial = true;
            try { _settingsService.Save(_settings); }
            catch (Exception ex) { RuntimeLog.Error("TUTORIAL", ex, "Could not save tutorial choice"); }
        };
        _tutorialWindow.Show();
        RuntimeLog.Log("TUTORIAL", "Tutorial invitation opened.");
    }

    private void Tutorial_Click(object sender, RoutedEventArgs e) => ShowTutorial();

    private void BeginTutorialStep(int step)
    {
        if (_tutorialStep < 0)
        {
            _tutorialOriginalView = _transitionCoordinator.CurrentView;
            _tutorialOriginalHoverSuppression = _suppressHoverCollapseUntilUtc;
            AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(TutorialObservedClick), true);
            _tutorialWatchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
            _tutorialWatchTimer.Tick += (_, _) => RefreshTutorial();
            _tutorialWatchTimer.Start();
        }
        _tutorialStep = step;
        _tutorialPrepared = false;
        RefreshTutorial();
    }

    private void RefreshTutorial()
    {
        if (_tutorialWindow == null || _tutorialStep < 0) return;
        if (!_isNotchVisible || _isHiddenByFullscreen)
        {
            SetTutorialHighlight(null);
            _tutorialWindow.SetProgress(false, Loc.Get("tour.hidden"));
            return;
        }
        if (_isGreetingActive)
        {
            SetTutorialHighlight(null);
            _tutorialWindow.SetProgress(false, Loc.Get("tour.busy"));
            return;
        }
        WakeFromIdle();
        // Keep the real controls reachable while moving between notch and coach.
        _suppressHoverCollapseUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        _hoverCollapseTimer.Stop();
        if (_spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility)
        {
            if (_tutorialStep == 4) _tutorialCompleted[4] = true;
            var spotlight = Application.Current.Windows.OfType<SpotlightWindow>().FirstOrDefault(window => window.IsVisible);
            SetTutorialHighlight(_tutorialStep == 4 ? spotlight?.FindName("SearchBox") as FrameworkElement : null);
            if (spotlight?.FindName("Shell") is FrameworkElement shell) PositionTutorial(shell);
            _tutorialWindow.SetProgress(_tutorialCompleted[_tutorialStep]);
            return;
        }
        if (!_tutorialPrepared)
        {
            // Prepare the view only. Playback, file drops, volume and Settings
            // are operated by the user on their actual controls.
            var view = _tutorialStep == 0 ? NotchView.Compact : NotchView.Media;
            _transitionCoordinator.RequestView(view, "TutorialPrepare");
            _tutorialPrepared = true;
        }
        if (_tutorialStep == 0 && _isExpanded && !_isAnimating) _tutorialCompleted[0] = true;
        if (_tutorialStep == 2 && _isSecondaryView && !_isAnimating) _tutorialCompleted[2] = true;
        if (_tutorialStep == 3 && _isAudioView && !_isAnimating) _tutorialCompleted[3] = true;
        FrameworkElement target = _tutorialStep switch
        {
            1 => _isExpanded && !_isSecondaryView && !_isAudioView
                ? (PlayPauseButton.IsVisible ? PlayPauseButton : InlinePlayPauseButton.IsVisible ? InlinePlayPauseButton : ExpandedContent)
                : NotchBorder,
            2 => _isSecondaryView ? SecondaryContent : FileShelfIconButton,
            3 => _isAudioView ? AudioContent : AudioIconButton,
            5 => SettingsButton,
            _ => NotchBorder
        };
        bool targetAvailable = target.IsVisible && target.Opacity > 0 && target.ActualWidth > 0;
        SetTutorialHighlight(targetAvailable ? target : NotchBorder);
        _tutorialWindow.SetProgress(_tutorialCompleted[_tutorialStep],
            !targetAvailable && _tutorialStep is 2 or 3 ? Loc.Get("tour.live.hiddenTab") : null);
        PositionTutorial();
    }

    private void TutorialObservedClick(object sender, MouseButtonEventArgs e)
    {
        if (_tutorialStep is not (1 or 5) || e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        foreach (var target in _tutorialStep == 1
            ? new FrameworkElement[] { PlayPauseButton, PrevButton, NextButton, InlinePlayPauseButton, InlinePrevButton, InlineNextButton }
            : new FrameworkElement[] { SettingsButton })
        {
            if (ReferenceEquals(source, target) || (source is Visual visual && target.IsAncestorOf(visual)))
            {
                _tutorialCompleted[_tutorialStep] = true;
                _tutorialWindow?.SetProgress(true);
                break;
            }
        }
    }

    private void SetTutorialHighlight(FrameworkElement? target)
    {
        if (target == null && _tutorialExitingHighlights.Count > 0)
        {
            // Closing the tour or hiding the notch cancels every pending fade.
            foreach (var (highlight, layer) in _tutorialExitingHighlights)
            {
                highlight.StopAnimation();
                layer.Remove(highlight);
            }
            _tutorialExitingHighlights.Clear();
        }
        if (ReferenceEquals(target, _tutorialTarget)) return;
        if (_tutorialHighlight != null)
        {
            var previous = _tutorialHighlight;
            var previousLayer = _tutorialAdornerLayer;
            if (target != null && previousLayer != null)
            {
                var exiting = (previous, previousLayer);
                _tutorialExitingHighlights.Add(exiting);
                previous.FadeOut(() =>
                {
                    previousLayer.Remove(previous);
                    _tutorialExitingHighlights.Remove(exiting);
                });
            }
            else
            {
                previous.StopAnimation();
                previousLayer?.Remove(previous);
            }
        }
        _tutorialHighlight = null;
        _tutorialAdornerLayer = null;
        _tutorialTarget = target;
        if (target == null) return;
        _tutorialAdornerLayer = AdornerLayer.GetAdornerLayer(target);
        if (_tutorialAdornerLayer == null) { _tutorialTarget = null; return; }
        _tutorialHighlight = new TutorialHighlightAdorner(target);
        _tutorialAdornerLayer.Add(_tutorialHighlight);
    }

    private void PositionTutorial(FrameworkElement? anchor = null)
    {
        anchor ??= NotchBorder;
        if (_tutorialWindow == null || !anchor.IsVisible || _isAnimating) return;
        var point = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, anchor.ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this);
        var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)point.X, (int)point.Y)).WorkingArea;
        double left = screen.Left / dpi.DpiScaleX;
        double top = screen.Top / dpi.DpiScaleY;
        double right = screen.Right / dpi.DpiScaleX;
        double bottom = screen.Bottom / dpi.DpiScaleY;
        _tutorialWindow.WindowStartupLocation = WindowStartupLocation.Manual;
        double destinationLeft = Math.Clamp(point.X / dpi.DpiScaleX - _tutorialWindow.Width / 2, left, Math.Max(left, right - _tutorialWindow.Width));
        double destinationTop = Math.Clamp(point.Y / dpi.DpiScaleY + 14, top, Math.Max(top, bottom - _tutorialWindow.Height));
        _tutorialWindow.MoveBeside(destinationLeft, destinationTop);
    }
}
