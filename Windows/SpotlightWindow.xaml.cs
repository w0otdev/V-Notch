using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.ViewModels;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class SpotlightWindow : Window
{
    private const double ExpandedCornerRadius = 14;
    private const double NotchShadowBlurRadius = 20;
    private const double NotchShadowDepth = 4;
    private const double NotchShadowOpacity = 0.6;
    private const double SpotlightShadowBlurRadius = 24;
    private const double SpotlightShadowDepth = 5;
    private const double SpotlightShadowOpacity = 0.48;
    private const int PageJump = 4;
    private const double StaleResultsOpacity = 0.55;
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan SearchingPanelGrace = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan QueryRestoreWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FailureDisplayTime = TimeSpan.FromMilliseconds(2800);
    private readonly SpotlightViewModel _viewModel;
    private readonly SpotlightLauncher _launcher;
    private bool _allowClose;
    private bool _isClosing;
    private bool _statusPulseActive;
    private int _animationGeneration;
    private bool _launchInFlight;
    private string? _pendingLaunchQuery;
    private string? _lastDismissedQuery;
    private DateTime _lastDismissedAtUtc;
    private DispatcherTimer? _searchingGraceTimer;
    private int _searchingGraceGeneration;
    private bool _searchingPanelArmed;
    private DispatcherTimer? _failureTimer;
    private int _failureGeneration;
    private int _launchGeneration;
    private bool _resultsDimmed;
    private bool _escBadgeVisible = true;
    private System.Windows.Controls.Border? _selectionGlide;
    private TranslateTransform? _glideTransform;
    private bool _glideVisible;
    private bool _glideUpdateQueued;
    private bool _contentShown;
    private int _contentSizeGeneration;
    private bool _contentResizeQueued;
    private bool _statusRefreshQueued;
    private bool _entranceActive;
    private bool _pendingContentReveal;
    private bool _entranceContentReserved;
    private SolidColorBrush? _shellBorderBrush;
    private EventHandler? _freshEntranceRenderingHandler;
    private HwndSource? _hwndSource;
    private bool _isParked;
    private double _unparkedWindowOpacity = 1;

    private bool _unparkedWindowHitTesting = true;
    private bool _unparkedWindowFocusable = true;
    private IntPtr _previousForegroundWindow;
    private CancellationTokenSource? _searchDebounceCts;
    private Uri? _spotlightClickUri;
    internal ISpotlightMorphHost? MorphHostOverride { get; set; }
    internal bool SuppressForegroundActivationForTests { get; set; }
    internal bool IsSpotlightOpen => IsVisible && !_isParked;

    internal SpotlightWindow(SpotlightViewModel viewModel, SpotlightLauncher launcher)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _launcher = launcher;
        DataContext = viewModel;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        PlaceholderText.Text = Loc.Get("spotlight.placeholder");
        SearchBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, Loc.Get("spotlight.placeholder"));

        // Activation from the global hotkey can land after ShowSpotlight has
        // already tried to focus; whenever the window becomes active the
        // search box must own the keyboard.
        Activated += (_, _) =>
        {
            if (!_isParked
                && !_isClosing
                && SearchBox.IsEnabled
                && !SearchBox.IsKeyboardFocused)
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }
        };
        IsVisibleChanged += (_, args) =>
        {
            // Last-resort ownership invariant: no hidden Spotlight HWND may
            // leave the source notch transparent after interrupted animations.
            if (args.NewValue is false) ReleaseMorphSession();
        };

        // The entrance morph publishes results while the shell is still at
        // notch width; containers stretch as the shell expands, so the glide
        // must re-measure or it keeps the narrow mid-morph width.
        ResultsList.SizeChanged += (_, _) => ScheduleGlideUpdate();

        // Never measure the ListBox from inside ObservableCollection's
        // CollectionChanged callback. WPF may not have forwarded that same
        // event through ListCollectionView/ItemContainerGenerator yet, and a
        // nested UpdateLayout then sees different source/generator counts.
        _viewModel.Results.CollectionChanged += (_, _) => ScheduleStatusRefresh();
        _viewModel.ResultsPublished += (_, _) => OnResultsPublished();
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SpotlightViewModel.IsSearching)
                or nameof(SpotlightViewModel.HasNoResults)
                or nameof(SpotlightViewModel.IsWindowsSearchUnavailable))
            {
                if (args.PropertyName == nameof(SpotlightViewModel.IsSearching)
                    && !_viewModel.IsSearching)
                {
                    SetResultsDimmed(false);
                    // The search finished empty; a queued Enter must never fire
                    // against some later result set.
                    if (_viewModel.Results.Count == 0) _pendingLaunchQuery = null;
                }
                RefreshStatus();
            }
        };
    }

    internal void ShowSpotlight()
    {
        if (_isClosing) return;

        PlaySpotlightClickSfx();

        _previousForegroundWindow = GetForegroundWindow();
        int generation = ++_animationGeneration;
        InvalidateLaunchAttempt();
        ResetMorphVisuals();
        // A restored query can publish instant results synchronously from the
        // SearchBox.Text setter below. Arm the entrance before any reset or
        // text callback can reveal those results. A restored result panel may
        // reserve its final height, but remains non-rendered and fixed while
        // the geometry morph runs. CompleteEntrance releases the queued reveal.
        _entranceActive = !AnimationConfig.ReduceMotion;
        _viewModel.Reset();
        _pendingLaunchQuery = null;
        ClearLaunchFailure();
        SetResultsDimmed(false, animate: false);

        // An accidental dismissal (stray click, focus steal) should not cost
        // the user their query; restore it selected so typing replaces it.
        bool restoreQuery = !string.IsNullOrEmpty(_lastDismissedQuery)
            && DateTime.UtcNow - _lastDismissedAtUtc < QueryRestoreWindow;
        SearchBox.Text = restoreQuery ? _lastDismissedQuery : string.Empty;
        if (restoreQuery) SearchBox.SelectAll();
        else _ = _viewModel.SearchAsync(string.Empty);

        // Acquire the source view before Show(). WPF can deactivate MainWindow
        // synchronously while showing or activating this HWND; secondary and
        // timer views normally auto-collapse on that event. Keep the view
        // intact until the return handoff has completed.
        SetMorphSessionActive(true);
        try
        {
            RefreshStatus();
            if (_isParked) UnparkWindow();
            else if (!IsVisible) Show();
            UpdateLayout();
            FocusSearchBox(generation);
            PrepareEntranceContentReservation();

            var target = GetSpotlightTarget();
            Action startEntrance = PrepareEntrance(
                target.Left,
                target.Top,
                generation);
            ScheduleFreshEntranceAfterComposition(
                startEntrance,
                generation);
        }
        catch
        {
            CancelPendingFreshEntrance();
            ReleaseMorphSession();
            throw;
        }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern int mciSendString(string command, System.Text.StringBuilder? returnString, int returnLength, IntPtr hwndCallback);

    private static bool _sfxMciOpened;
    private static string? _sfxMciPath;

    private void PlaySpotlightClickSfx()
    {
        if (SuppressForegroundActivationForTests) return;
        try
        {
            if (_spotlightClickUri == null)
            {
                string findSfx(string folder)
                {
                    string m4a = System.IO.Path.Combine(folder, "Assets", "SPL_CLICK_SFX.m4a");
                    if (System.IO.File.Exists(m4a)) return m4a;
                    string mp3 = System.IO.Path.Combine(folder, "Assets", "SPL_CLICK_SFX.mp3");
                    return System.IO.File.Exists(mp3) ? mp3 : m4a;
                }

                string sfxPath = findSfx(AppDomain.CurrentDomain.BaseDirectory);
                if (!System.IO.File.Exists(sfxPath))
                {
                    string devFolder = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", ".."));
                    sfxPath = findSfx(devFolder);
                }

                if (System.IO.File.Exists(sfxPath))
                {
                    _spotlightClickUri = new Uri(sfxPath, UriKind.Absolute);
                }
            }

            if (_spotlightClickUri != null)
            {
                string localPath = _spotlightClickUri.LocalPath;
                if (!_sfxMciOpened || _sfxMciPath != localPath)
                {
                    mciSendString("close sfx_click", null, 0, IntPtr.Zero);
                    int openRes = mciSendString($"open \"{localPath}\" alias sfx_click", null, 0, IntPtr.Zero);
                    if (openRes == 0)
                    {
                        _sfxMciOpened = true;
                        _sfxMciPath = localPath;
                    }
                }

                if (_sfxMciOpened)
                {
                    mciSendString("play sfx_click from 0", null, 0, IntPtr.Zero);
                }
                else
                {
                    var player = new MediaPlayer();
                    player.Open(_spotlightClickUri);
                    player.Play();
                }
            }
        }
        catch
        {
            // Ignore audio playback errors
        }
    }

    private void ScheduleFreshEntranceAfterComposition(
        Action startEntrance,
        int generation)
    {
        CancelPendingFreshEntrance();

        // Hide() disconnects an AllowsTransparency layered HWND from WPF's
        // composition target. On the next Show(), UpdateLayout can finish while
        // that target is still reconnecting; starting the clocks immediately
        // hides the live notch even though Spotlight cannot present yet. The
        // clocks then advance offscreen and the entrance appears to jump in at
        // ~300 ms. Keep the source visible through one completed render pulse,
        // then start on the following pulse when the reused HWND is renderable.
        int pulsesRemaining = 2;
        EventHandler handler = null!;
        handler = (_, _) =>
        {
            if (generation != _animationGeneration || !IsSpotlightOpen || _isClosing)
            {
                if (ReferenceEquals(_freshEntranceRenderingHandler, handler))
                {
                    CompositionTarget.Rendering -= handler;
                    _freshEntranceRenderingHandler = null;
                }
                return;
            }

            if (--pulsesRemaining > 0) return;

            CompositionTarget.Rendering -= handler;
            if (ReferenceEquals(_freshEntranceRenderingHandler, handler))
                _freshEntranceRenderingHandler = null;

            // The first prepared snapshot frame has now been submitted. Flush
            // DWM before hiding the real source so the two layered HWNDs hand
            // visibility across without a transparent desktop frame between.
            DwmFlush();
            // Foreground activation can enter native input-queue work. Finish
            // it while the committed source frame is still stationary, then
            // attach the clocks so none of their duration is spent offscreen.
            FocusSearchBox(generation);
            Opacity = 1;
            startEntrance();
        };

        _freshEntranceRenderingHandler = handler;
        CompositionTarget.Rendering += handler;
        // Ensure the newly shown transparent surface has work queued even when
        // the source notch itself is otherwise idle.
        InvalidateVisual();
    }

    private bool CancelPendingFreshEntrance()
    {
        EventHandler? handler = _freshEntranceRenderingHandler;
        if (handler == null) return false;

        CompositionTarget.Rendering -= handler;
        _freshEntranceRenderingHandler = null;
        return true;
    }

    private void FocusSearchBox(int generation)
    {
        if (SuppressForegroundActivationForTests) return;
        ForceForeground();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        if (SearchBox.IsKeyboardFocused) return;

        // Windows can refuse the foreground switch while another process holds
        // the input lock; retry after the pending input queue settles.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (generation != _animationGeneration || !IsSpotlightOpen || _isClosing) return;
            ForceForeground();
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        });
    }

    private void ForceForeground()
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            Activate();
            return;
        }

        IntPtr foreground = GetForegroundWindow();
        if (foreground == hwnd)
        {
            Activate();
            return;
        }

        // When Spotlight is toggled from the low-level keyboard hook (Alt+Space
        // fallback), this process has no foreground-activation grant and plain
        // SetForegroundWindow/Activate is silently refused. Attaching to the
        // current foreground thread's input queue lifts that restriction.
        uint thisThread = GetCurrentThreadId();
        uint foregroundThread = 0;
        if (foreground != IntPtr.Zero)
            foregroundThread = GetWindowThreadProcessId(foreground, out _);

        bool attached = foregroundThread != 0
            && foregroundThread != thisThread
            && AttachThreadInput(thisThread, foregroundThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            Activate();
        }
        catch (InvalidOperationException)
        {
            // Window is mid-close; nothing to focus.
        }
        finally
        {
            if (attached) AttachThreadInput(thisThread, foregroundThread, false);
        }
    }

    internal void ToggleFromHotkey()
    {
        if (_isClosing)
        {
            ReverseExitToEntrance();
            return;
        }

        if (!IsSpotlightOpen)
        {
            ShowSpotlight();
            return;
        }

        DismissFromGlobalShortcut();
    }

    internal void DismissFromGlobalShortcut()
    {
        if (!IsSpotlightOpen) return;
        if (_isClosing)
        {
            // A repeated dismissal must finish an in-flight deactivation
            // immediately. Invalidate its remaining animation callbacks.
            ++_animationGeneration;
            CompleteHide();
            _lastDismissedQuery = null;
            return;
        }

        HideSpotlight();
        // Toggling Spotlight away is an explicit abandon, unlike a focus loss;
        // the next open starts fresh.
        _lastDismissedQuery = null;
    }

    internal void HandleGlobalEscape()
    {
        if (!IsSpotlightOpen) return;
        _pendingLaunchQuery = null;
        if (!_isClosing && !string.IsNullOrEmpty(SearchBox.Text))
        {
            // First Escape clears the query; a second one dismisses the window.
            SearchBox.Clear();
            SearchBox.Focus();
            return;
        }

        DismissFromGlobalShortcut();
    }

    internal void HideSpotlight()
    {
        if (!IsSpotlightOpen || _isClosing) return;
        _lastDismissedQuery = SearchBox.Text;
        _lastDismissedAtUtc = DateTime.UtcNow;
        _isClosing = true;
        InvalidateLaunchAttempt();
        _viewModel.CancelPendingSearch();
        SearchBox.IsEnabled = false;
        int generation = ++_animationGeneration;
        if (CancelPendingFreshEntrance())
        {
            // The live source was never hidden, so there is no morph frame to
            // reverse. Retire the transparent, freshly shown HWND directly.
            CompleteHide();
            return;
        }
        PlayExit(generation);
    }

    internal void Shutdown()
    {
        ++_animationGeneration;
        _allowClose = true;
        CancelPendingFreshEntrance();
        ClearMorphAnimations();
        ReleaseMorphSession();
        _viewModel.Dispose();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideSpotlight();
        }
        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WindowProc);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hwndSource?.RemoveHook(WindowProc);
        _hwndSource = null;
        base.OnClosed(e);
    }

    private IntPtr WindowProc(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (_isParked && message == WM_NCHITTEST)
        {
            handled = true;
            return new IntPtr(HTTRANSPARENT);
        }

        return IntPtr.Zero;
    }

    private void ParkWindow()
    {
        if (_isParked || !IsVisible) return;

        _unparkedWindowOpacity = Opacity;
        _unparkedWindowHitTesting = IsHitTestVisible;
        _unparkedWindowFocusable = Focusable;
        _isParked = true;

        Keyboard.ClearFocus();
        Focusable = false;
        IsHitTestVisible = false;
        Opacity = 0;

        // Hide() used to return activation to the previous foreground app.
        // A parked HWND stays alive for composition, so reproduce that focus
        // handoff explicitly without stealing focus from an app just launched
        // through a Spotlight result.
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero
            && GetForegroundWindow() == hwnd
            && _previousForegroundWindow != IntPtr.Zero
            && _previousForegroundWindow != hwnd)
        {
            SetForegroundWindow(_previousForegroundWindow);
        }
    }

    private void UnparkWindow()
    {
        if (!_isParked) return;

        Opacity = _unparkedWindowOpacity;
        Focusable = _unparkedWindowFocusable;
        IsHitTestVisible = _unparkedWindowHitTesting;

        _isParked = false;
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlayTypingAnimation();
        UpdateGlowingCaret();
        CancelSearchingGrace();
        PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        // The visible suggestion belongs to the previous query; hide it until
        // the new results publish.
        AutocompleteText.Visibility = Visibility.Collapsed;
        _pendingLaunchQuery = null;
        ClearLaunchFailure();

        _searchDebounceCts?.Cancel();

        string currentText = SearchBox.Text;

        if (string.IsNullOrEmpty(currentText))
        {
            _searchDebounceCts = null;
            await _viewModel.SearchAsync(currentText);
            ScheduleStatusRefresh();
            return;
        }

        // Until the new query publishes, the visible rows answer the old one.
        if (_viewModel.Results.Count > 0)
            SetResultsDimmed(true);

        var cts = new CancellationTokenSource();
        _searchDebounceCts = cts;
        CancellationToken token = cts.Token;

        try
        {
            await Task.Delay(120, token);
            if (token.IsCancellationRequested) return;

            await _viewModel.SearchAsync(currentText);
            ScheduleStatusRefresh();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Cancelled due to rapid typing or window closure
        }
    }

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => UpdateGlowingCaret();

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e) => GlowingCaret.Visibility = Visibility.Collapsed;

    private void SearchBox_SelectionChanged(object sender, RoutedEventArgs e) => UpdateGlowingCaret();

    private void UpdateGlowingCaret()
    {
        if (!SearchBox.IsFocused || !IsVisible || SearchBox.SelectionLength > 0)
        {
            GlowingCaret.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            Rect rect = SearchBox.GetRectFromCharacterIndex(SearchBox.CaretIndex, true);
            double left = (!rect.IsEmpty && double.IsFinite(rect.Left)) ? Math.Max(2, rect.Left) : 2;
            GlowingCaret.Margin = new Thickness(left, 0, 0, 0);
            GlowingCaret.Visibility = Visibility.Visible;
            ResetGlowingCaretBlink();
        }
        catch
        {
            GlowingCaret.Margin = new Thickness(2, 0, 0, 0);
            GlowingCaret.Visibility = Visibility.Visible;
        }
    }

    private void ResetGlowingCaretBlink()
    {
        GlowingCaret.BeginAnimation(OpacityProperty, null);
        GlowingCaret.Opacity = 1.0;

        if (AnimationConfig.ReduceMotion) return;

        var blinkAnim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.15,
            Duration = TimeSpan.FromMilliseconds(520),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(blinkAnim, AnimationConfig.TargetFps);
        GlowingCaret.BeginAnimation(OpacityProperty, blinkAnim);
    }

    private void PlayTypingAnimation()
    {
        if (AnimationConfig.ReduceMotion) return;

        // 1. Search box scale pop & horizontal recoil
        var scaleYAnim = new DoubleAnimationUsingKeyFrames();
        scaleYAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.025, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        scaleYAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(scaleYAnim, AnimationConfig.TargetFps);

        var scaleXAnim = new DoubleAnimationUsingKeyFrames();
        scaleXAnim.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.018, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(45)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        scaleXAnim.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(scaleXAnim, AnimationConfig.TargetFps);

        var recoilAnim = new DoubleAnimationUsingKeyFrames();
        recoilAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        recoilAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-1.5, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        recoilAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(recoilAnim, AnimationConfig.TargetFps);

        SearchBoxScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        SearchBoxScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
        SearchBoxTranslate.BeginAnimation(TranslateTransform.XProperty, recoilAnim);

        // 2. Caret height pop & glowing flash burst
        var caretHeightScale = new DoubleAnimationUsingKeyFrames();
        caretHeightScale.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        caretHeightScale.KeyFrames.Add(new EasingDoubleKeyFrame(1.25, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        caretHeightScale.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(caretHeightScale, AnimationConfig.TargetFps);
        CaretScale.BeginAnimation(ScaleTransform.ScaleYProperty, caretHeightScale);

        var caretGlowBurst = new DoubleAnimationUsingKeyFrames();
        caretGlowBurst.KeyFrames.Add(new LinearDoubleKeyFrame(10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        caretGlowBurst.KeyFrames.Add(new EasingDoubleKeyFrame(18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(35)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        caretGlowBurst.KeyFrames.Add(new EasingDoubleKeyFrame(10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(caretGlowBurst, AnimationConfig.TargetFps);
        CaretGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, caretGlowBurst);

        // 3. Search icon pulse & subtle rotation wiggle
        var iconScale = new DoubleAnimationUsingKeyFrames();
        iconScale.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        iconScale.KeyFrames.Add(new EasingDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        iconScale.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(iconScale, AnimationConfig.TargetFps);

        var iconRotate = new DoubleAnimationUsingKeyFrames();
        iconRotate.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(0))));
        iconRotate.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(50)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        iconRotate.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(130)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        Timeline.SetDesiredFrameRate(iconRotate, AnimationConfig.TargetFps);

        SearchIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, iconScale);
        SearchIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, iconScale);
        SearchIconRotate.BeginAnimation(RotateTransform.AngleProperty, iconRotate);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Navigation keys must be intercepted on the tunnel: the search box's
        // editing-command bindings consume Up/Down/PageUp/PageDown during the
        // bubbling KeyDown, so a KeyDown handler never sees them.
        if (e.Key == Key.Down || (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None))
        {
            MoveSelection(1);
            e.Handled = true;
        }
        else if (e.Key == Key.Up || (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            MoveSelection(-1);
            e.Handled = true;
        }
        else if (e.Key == Key.PageDown)
        {
            MoveSelection(PageJump);
            e.Handled = true;
        }
        else if (e.Key == Key.PageUp)
        {
            MoveSelection(-PageJump);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            HandleGlobalEscape();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ModifierKeys modifiers = Keyboard.Modifiers;
            if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift)) LaunchSelectedElevated();
            else if ((modifiers & ModifierKeys.Control) != 0) RevealSelected();
            else LaunchSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            CopySelected();
            e.Handled = true;
        }
        else if (e.Key is >= Key.D1 and <= Key.D9 && Keyboard.Modifiers == ModifierKeys.Control)
        {
            int index = e.Key - Key.D1;
            if (index < _viewModel.Results.Count)
            {
                _viewModel.SelectedResult = _viewModel.Results[index];
                LaunchSelected();
            }
            e.Handled = true;
        }
    }

    private void ResultItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SpotlightSearchItem item }) return;
        _viewModel.SelectedResult = item;
        LaunchSelected();
        // If the launch failed and the window stays open, typing must keep working.
        if (IsSpotlightOpen && !_isClosing) SearchBox.Focus();
    }

    private void ResultItem_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SpotlightSearchItem item } container) return;
        _viewModel.SelectedResult = item;

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("SpotlightContextMenuStyle"),
            PlacementTarget = container
        };

        if (item.Kind == SpotlightResultKind.Calculation)
        {
            AddMenuItem(menu, Loc.Get("spotlight.copy"), () =>
            {
                if (TryCopyToClipboard(item.Target)) HideSpotlight();
            });
        }
        else
        {
            AddMenuItem(menu, Loc.Get("spotlight.open"), LaunchSelected);
            if (SpotlightLauncher.CanLaunchElevated(item))
                AddMenuItem(menu, Loc.Get("spotlight.runAsAdmin"), LaunchSelectedElevated);
            if (SpotlightLauncher.CanReveal(item))
                AddMenuItem(menu, Loc.Get("spotlight.reveal"), RevealSelected);
            if (SpotlightLauncher.GetCopyableText(item) != null)
                AddMenuItem(menu, Loc.Get("spotlight.copyPath"), CopySelected);
        }

        menu.IsOpen = true;
        e.Handled = true;
    }

    private void AddMenuItem(ContextMenu menu, string header, Action action)
    {
        var menuItem = new MenuItem
        {
            Header = header,
            Style = (Style)FindResource("SpotlightMenuItemStyle")
        };
        menuItem.Click += (_, _) => action();
        menu.Items.Add(menuItem);
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // ApplicationIdle can be starved by the notch's continuous media/render
        // work. Input priority guarantees an outside click dismisses Spotlight.
        // Capture the current session: a queued deactivation from the previous
        // hide must not close a Spotlight session opened immediately afterward.
        int generation = _animationGeneration;
        Dispatcher.BeginInvoke(() =>
        {
            if (generation != _animationGeneration || !IsSpotlightOpen || _isClosing) return;
            HideSpotlight();
        }, DispatcherPriority.Input);
    }

    private void MoveSelection(int direction)
    {
        _pendingLaunchQuery = null;
        int count = _viewModel.Results.Count;
        if (count == 0) return;
        int current = ResultsList.SelectedIndex;
        int next;
        if (current < 0) next = direction > 0 ? 0 : count - 1;
        else if (Math.Abs(direction) == 1) next = (current + direction + count) % count;
        else next = Math.Clamp(current + direction, 0, count - 1);
        ResultsList.SelectedIndex = next;
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private async void LaunchSelected()
    {
        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        if (selected == null)
        {
            // Honor a fast type-and-Enter: launch the top result when the
            // in-flight search publishes it.
            if (_viewModel.IsSearching && !string.IsNullOrWhiteSpace(SearchBox.Text))
                _pendingLaunchQuery = SearchBox.Text;
            return;
        }
        if (_launchInFlight) return;

        if (selected.Kind == SpotlightResultKind.Calculation)
        {
            if (TryCopyToClipboard(selected.Target)) HideSpotlight();
            return;
        }

        int launchGeneration = ++_launchGeneration;
        int sessionGeneration = _animationGeneration;
        _launchInFlight = true;
        try
        {
            // ShellExecute can block for hundreds of ms on cold starts; keep
            // the dispatcher free so the exit morph starts instantly.
            bool launched = await Task.Run(() => _launcher.TryLaunch(selected));
            if (!CanCompleteLaunch(launchGeneration, sessionGeneration)) return;
            if (launched)
            {
                _viewModel.RecordLaunch(selected);
                HideSpotlight();
            }
            else
            {
                ShowLaunchFailure(selected);
            }
        }
        finally
        {
            if (launchGeneration == _launchGeneration) _launchInFlight = false;
        }
    }

    private async void LaunchSelectedElevated()
    {
        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        if (selected == null || _launchInFlight) return;
        if (!SpotlightLauncher.CanLaunchElevated(selected))
        {
            // Store apps cannot take the runas verb; a plain launch beats a dead key.
            LaunchSelected();
            return;
        }

        int launchGeneration = ++_launchGeneration;
        int sessionGeneration = _animationGeneration;
        _launchInFlight = true;
        try
        {
            bool launched = await Task.Run(() => _launcher.TryLaunchElevated(selected));
            if (!CanCompleteLaunch(launchGeneration, sessionGeneration)) return;
            if (launched)
            {
                _viewModel.RecordLaunch(selected);
                HideSpotlight();
            }
            else
            {
                ShowLaunchFailure(selected);
            }
        }
        finally
        {
            if (launchGeneration == _launchGeneration) _launchInFlight = false;
        }
    }

    private async void RevealSelected()
    {
        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        if (selected == null || _launchInFlight || !SpotlightLauncher.CanReveal(selected)) return;

        int launchGeneration = ++_launchGeneration;
        int sessionGeneration = _animationGeneration;
        _launchInFlight = true;
        try
        {
            bool revealed = await Task.Run(() => _launcher.TryRevealInExplorer(selected));
            if (!CanCompleteLaunch(launchGeneration, sessionGeneration)) return;
            if (revealed) HideSpotlight();
            else ShowLaunchFailure(selected);
        }
        finally
        {
            if (launchGeneration == _launchGeneration) _launchInFlight = false;
        }
    }

    private bool CanCompleteLaunch(int launchGeneration, int sessionGeneration) =>
        launchGeneration == _launchGeneration
        && sessionGeneration == _animationGeneration
        && IsSpotlightOpen
        && !_isClosing;

    private void InvalidateLaunchAttempt()
    {
        ++_launchGeneration;
        _launchInFlight = false;
    }

    private void CopySelected()
    {
        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        string? text = selected == null ? null : SpotlightLauncher.GetCopyableText(selected);
        if (text == null) return;
        if (TryCopyToClipboard(text)) HideSpotlight();
    }

    private static bool TryCopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text);
            return true;
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("SPOTLIGHT-COPY", ex, "Clipboard write failed");
            return false;
        }
    }

    private void ShowLaunchFailure(SpotlightSearchItem item)
    {
        ClearLaunchFailure();
        // The target is stale (moved or uninstalled); keep Enter useful by
        // dropping the dead row and telling the user what happened.
        _viewModel.RemoveResult(item);
        FailureText.Text = Loc.Get("spotlight.launchFailed", item.Title);
        FailureBar.Visibility = Visibility.Visible;
        PlayShake();
        int generation = ++_failureGeneration;
        var timer = new DispatcherTimer { Interval = FailureDisplayTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _failureGeneration || !ReferenceEquals(timer, _failureTimer)) return;
            _failureTimer = null;
            FailureBar.Visibility = Visibility.Collapsed;
        };
        _failureTimer = timer;
        timer.Start();
    }

    private void ClearLaunchFailure()
    {
        ++_failureGeneration;
        _failureTimer?.Stop();
        _failureTimer = null;
        if (FailureBar.Visibility != Visibility.Visible) return;
        FailureBar.Visibility = Visibility.Collapsed;
    }

    private void PlayShake()
    {
        if (AnimationConfig.ReduceMotion) return;

        double[] offsets = [0, -10, 8, -5, 2, 0];
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        for (int i = 0; i < offsets.Length; i++)
        {
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(
                offsets[i],
                KeyTime.FromPercent(i / (double)(offsets.Length - 1))));
        }
        Timeline.SetDesiredFrameRate(shake, AnimationConfig.TargetFps);
        ShellShake.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    private void OnResultsPublished()
    {
        SetResultsDimmed(false);
        if (_viewModel.SelectedResult != null)
        {
            // Container generation finishes after layout; defer the scroll so a
            // preserved selection can never sit off-screen.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_viewModel.SelectedResult != null && ResultsList.IsVisible)
                    ResultsList.ScrollIntoView(_viewModel.SelectedResult);
            });
        }
        // A publish can move the selected row without a SelectionChanged event.
        ScheduleGlideUpdate();
        UpdateAutocomplete();

        if (_pendingLaunchQuery != null
            && _pendingLaunchQuery == SearchBox.Text
            && _viewModel.Results.Count > 0)
        {
            _pendingLaunchQuery = null;
            LaunchSelected();
        }
        RefreshStatus();
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ScheduleGlideUpdate();

    private void ScheduleGlideUpdate()
    {
        // A closing shell resizes every frame; recomputing glide geometry per
        // frame is invisible work that competes with the exit morph.
        if (_glideUpdateQueued || _isClosing) return;
        _glideUpdateQueued = true;
        // Loaded priority runs after the layout pass, when containers have
        // real positions; batching also collapses select-then-reselect churn
        // from a publish into a single glide.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _glideUpdateQueued = false;
            if (_isClosing) return;
            UpdateSelectionGlide();
        });
    }

    private void UpdateSelectionGlide()
    {
        if (!EnsureGlideParts()) return;

        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        if (selected == null
            || !ResultsList.IsVisible
            || ResultsList.ItemContainerGenerator.ContainerFromItem(selected) is not ListBoxItem container
            || _selectionGlide!.Parent is not UIElement host)
        {
            HideSelectionGlide();
            return;
        }

        Point position = container.TranslatePoint(new Point(0, 0), host);
        double width = container.ActualWidth;
        double height = container.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            HideSelectionGlide();
            return;
        }

        _selectionGlide.Width = width;
        _selectionGlide.Height = height;
        _glideTransform!.X = position.X;

        if (_glideVisible && !AnimationConfig.ReduceMotion)
        {
            // A To-only animation departs from the current animated value, so
            // rapid arrow presses retarget mid-flight without a jump.
            var glide = new DoubleAnimation(position.Y, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 }
            };
            Timeline.SetDesiredFrameRate(glide, AnimationConfig.TargetFps);
            _glideTransform.BeginAnimation(TranslateTransform.YProperty, glide);
        }
        else
        {
            _glideTransform.BeginAnimation(TranslateTransform.YProperty, null);
            _glideTransform.Y = position.Y;
            _selectionGlide.BeginAnimation(OpacityProperty, null);
            if (AnimationConfig.ReduceMotion)
            {
                _selectionGlide.Opacity = 1;
            }
            else
            {
                var fadeIn = CreateAnimation(0, 1, TimeSpan.FromMilliseconds(140),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut });
                _selectionGlide.BeginAnimation(OpacityProperty, fadeIn);
            }
        }
        _glideVisible = true;
    }

    private void HideSelectionGlide()
    {
        if (_selectionGlide == null || !_glideVisible) return;
        _glideVisible = false;
        _glideTransform?.BeginAnimation(TranslateTransform.YProperty, null);
        if (AnimationConfig.ReduceMotion)
        {
            _selectionGlide.BeginAnimation(OpacityProperty, null);
            _selectionGlide.Opacity = 0;
            return;
        }

        var fade = CreateAnimation(_selectionGlide.Opacity, 0, TimeSpan.FromMilliseconds(100),
            new QuadraticEase { EasingMode = EasingMode.EaseOut });
        _selectionGlide.BeginAnimation(OpacityProperty, fade);
    }

    private void ResetSelectionGlide()
    {
        _glideVisible = false;
        if (_selectionGlide == null) return;
        _selectionGlide.BeginAnimation(OpacityProperty, null);
        _selectionGlide.Opacity = 0;
        _glideTransform?.BeginAnimation(TranslateTransform.YProperty, null);
    }

    private bool EnsureGlideParts()
    {
        if (_selectionGlide != null && _glideTransform != null) return true;
        ResultsList.ApplyTemplate();
        _selectionGlide = ResultsList.Template.FindName("SelectionGlide", ResultsList)
            as System.Windows.Controls.Border;
        _glideTransform = ResultsList.Template.FindName("SelectionGlideTransform", ResultsList)
            as TranslateTransform;
        return _selectionGlide != null && _glideTransform != null;
    }

    private void SetResultsDimmed(bool dimmed, bool animate = true)
    {
        if (_resultsDimmed == dimmed) return;
        _resultsDimmed = dimmed;
        double target = dimmed ? StaleResultsOpacity : 1.0;
        if (!animate || AnimationConfig.ReduceMotion)
        {
            ResultsList.BeginAnimation(OpacityProperty, null);
            ResultsList.Opacity = target;
            return;
        }

        var fade = CreateAnimation(ResultsList.Opacity, target, TimeSpan.FromMilliseconds(120),
            new QuadraticEase { EasingMode = EasingMode.EaseOut });
        ResultsList.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Shows the top result's remaining characters as a dim inline completion
    /// behind the typed text (Flow Launcher style). Only prefix matches qualify.
    /// </summary>
    private void UpdateAutocomplete()
    {
        string query = SearchBox.Text;
        string? title = _viewModel.Results.Count > 0 ? _viewModel.Results[0].Title : null;
        if (string.IsNullOrEmpty(query)
            || title == null
            || title.Length <= query.Length
            || !title.StartsWith(query, StringComparison.CurrentCultureIgnoreCase))
        {
            AutocompleteText.Visibility = Visibility.Collapsed;
            return;
        }

        // The transparent prefix mirrors what the TextBox displays so the dim
        // tail lines up with the caret regardless of typed casing.
        AutocompleteTypedRun.Text = query;
        AutocompleteSuffixRun.Text = title.Substring(query.Length);
        AutocompleteText.Visibility = Visibility.Visible;
    }

    private void RefreshStatus()
    {
        int resultCount = _viewModel.Results.Count;
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        bool hasResults = resultCount > 0;

        // "Searching\u2026" only earns its panel after a grace period; fast queries
        // go straight from nothing to results without a flash of status.
        bool searchingEligible = hasQuery && !hasResults && _viewModel.IsSearching;
        UpdateSearchingGrace(searchingEligible);
        bool showSearching = searchingEligible && _searchingPanelArmed;
        bool showStatus = showSearching
                          || (hasQuery && !hasResults && !_viewModel.IsSearching
                              && (_viewModel.IsWindowsSearchUnavailable || _viewModel.HasNoResults));
        bool isSearchingInFlight = hasQuery && _viewModel.IsSearching && _contentShown;
        bool showContent = hasResults || showStatus || isSearchingInFlight;

        // Children first: the reveal/resize animations below measure the
        // region's natural height, which depends on these visibilities.
        ResultsList.Visibility = (hasResults || isSearchingInFlight) ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        if (showStatus)
        {
            string status = _viewModel.IsSearching
                ? "searching"
                : _viewModel.IsWindowsSearchUnavailable
                    ? "unavailable"
                    : "noResults";
            StatusGlyph.Text = status switch
            {
                "searching" => "\uE895",
                "unavailable" => "\uE7BA",
                _ => "\uE721"
            };
            StatusTitle.Text = Loc.Get($"spotlight.{status}");
            StatusHint.Text = Loc.Get($"spotlight.{status}.hint");
        }
        SetStatusPulse(showStatus && _viewModel.IsSearching);

        bool contentWasShown = _contentShown;
        SetContentShown(showContent);
        // A result-count change while the panel is open resizes it smoothly.
        if (showContent && contentWasShown) ScheduleContentResize();
        SetEscBadgeVisible(!showContent);
    }

    private void ScheduleStatusRefresh()
    {
        if (_statusRefreshQueued || Dispatcher.HasShutdownStarted) return;
        _statusRefreshQueued = true;

        // Loaded runs after the current collection notification and its queued
        // data-binding/layout work. Coalescing also prevents ApplyDiff's series
        // of Insert/Move/Remove events from forcing nested layout passes.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _statusRefreshQueued = false;
            if (!Dispatcher.HasShutdownStarted) RefreshStatus();
        });
    }

    /// <summary>
    /// Expands or collapses the results region with an animated height so the
    /// auto-sized window grows/shrinks smoothly instead of snapping.
    /// </summary>
    private void SetContentShown(bool shown)
    {
        // While the notch morph locks the shell's height, revealing content
        // would overflow the shell; hold the reveal until the morph lands.
        if (_entranceActive)
        {
            _pendingContentReveal = shown;
            return;
        }
        if (_contentShown == shown) return;
        _contentShown = shown;
        int generation = ++_contentSizeGeneration;

        if (AnimationConfig.ReduceMotion || (!shown && (!IsSpotlightOpen || _isClosing)))
        {
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Height = double.NaN;
            ContentRegion.ClipToBounds = false;
            ContentRegion.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (shown)
        {
            // Mid-collapse re-shows continue from the current visual height.
            double from = ContentRegion.Visibility != Visibility.Collapsed
                ? ContentRegion.ActualHeight
                : 0;
            ContentRegion.Visibility = Visibility.Visible;
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Height = double.NaN;
            ContentRegion.UpdateLayout();
            BeginContentHeightAnimation(from, ContentRegion.ActualHeight, generation);
            PlayContentReveal();
        }
        else
        {
            ContentRegion.ClipToBounds = true;
            var collapse = CreateAnimation(ContentRegion.ActualHeight, 0,
                TimeSpan.FromMilliseconds(340),
                new CubicBezierEase(0.36, -0.15, 0.64, 1.15) { EasingMode = EasingMode.EaseIn });
            collapse.Completed += (_, _) =>
            {
                if (generation != _contentSizeGeneration) return;
                ContentRegion.BeginAnimation(HeightProperty, null);
                ContentRegion.Height = double.NaN;
                ContentRegion.ClipToBounds = false;
                ContentRegion.Visibility = Visibility.Collapsed;
            };
            ContentRegion.BeginAnimation(HeightProperty, collapse);
        }
    }

    private void PrepareEntranceContentReservation()
    {
        if (!_entranceActive || !_pendingContentReveal || _contentShown)
            return;

        // A restored instant query can already have results before the fresh
        // entrance starts. Measure them once at the final Spotlight width,
        // then keep a fixed Hidden region in layout. The shell can morph
        // directly to its final results height without rendering or repeatedly
        // measuring the ListBox on every geometry tick.
        ContentRegion.BeginAnimation(WidthProperty, null);
        ContentRegion.BeginAnimation(HeightProperty, null);
        ContentRegion.BeginAnimation(OpacityProperty, null);
        ContentRegionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ContentRegion.Width = double.NaN;
        ContentRegion.Height = double.NaN;
        ContentRegion.HorizontalAlignment = HorizontalAlignment.Stretch;
        ContentRegion.ClipToBounds = false;
        ContentRegion.Opacity = 0;
        ContentRegionTranslate.Y = -6;
        ContentRegion.Visibility = Visibility.Visible;
        ContentRegion.UpdateLayout();

        double reservedWidth = ContentRegion.ActualWidth;
        double reservedHeight = ContentRegion.ActualHeight;
        if (!double.IsFinite(reservedWidth) || reservedWidth <= 0
            || !double.IsFinite(reservedHeight) || reservedHeight <= 0)
        {
            ContentRegion.Opacity = 1;
            ContentRegionTranslate.Y = 0;
            ContentRegion.Visibility = Visibility.Collapsed;
            return;
        }

        ++_contentSizeGeneration;
        _entranceContentReserved = true;
        ContentRegion.Width = reservedWidth;
        ContentRegion.Height = reservedHeight;
        ContentRegion.HorizontalAlignment = HorizontalAlignment.Left;
        ContentRegion.ClipToBounds = true;
        ContentRegion.Visibility = Visibility.Hidden;

        RuntimeLog.Debug(
            "SPOTLIGHT-MORPH",
            $"Entrance content reserve: {reservedWidth:F1}x{reservedHeight:F1}, " +
            $"sourcePending={_pendingContentReveal}, gen={_animationGeneration}");
    }

    private bool RevealEntranceContentReservation()
    {
        if (!_entranceContentReserved) return false;

        double reservedHeight = ContentRegion.Height;
        if (!double.IsFinite(reservedHeight) || reservedHeight <= 0)
            reservedHeight = Math.Max(1, ContentRegion.ActualHeight);
        double landedShellHeight = Math.Max(1, Shell.ActualHeight);

        _entranceContentReserved = false;
        _contentShown = true;
        int generation = ++_contentSizeGeneration;

        // Make the frozen region renderable while it is still transparent,
        // then measure the latest results under a fixed landed shell. A query
        // can change during the 560ms entrance; if its natural height differs
        // from the original reservation, animate that delta instead of snapping
        // when the morph clock is removed.
        Shell.Height = landedShellHeight;
        ContentRegion.BeginAnimation(WidthProperty, null);
        ContentRegion.BeginAnimation(HeightProperty, null);
        ContentRegion.BeginAnimation(OpacityProperty, null);
        ContentRegionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ContentRegion.Opacity = 0;
        ContentRegionTranslate.Y = -6;
        ContentRegion.Visibility = Visibility.Visible;
        ContentRegion.Width = double.NaN;
        ContentRegion.Height = double.NaN;
        ContentRegion.HorizontalAlignment = HorizontalAlignment.Stretch;
        ContentRegion.ClipToBounds = true;
        double availableWidth = Math.Max(1, Shell.ActualWidth);
        ContentRegion.Measure(new Size(availableWidth, double.PositiveInfinity));
        double naturalHeight = ContentRegion.DesiredSize.Height;
        if (!double.IsFinite(naturalHeight) || naturalHeight <= 0)
            naturalHeight = reservedHeight;

        // Restore the reservation before releasing the shell back to auto
        // height, so there is no intermediate compositor frame at the new size.
        ContentRegion.Height = reservedHeight;
        ContentRegion.UpdateLayout();
        Shell.Height = double.NaN;
        if (Math.Abs(naturalHeight - reservedHeight) <= 1)
        {
            ContentRegion.Height = double.NaN;
            ContentRegion.ClipToBounds = false;
        }
        else
        {
            BeginContentHeightAnimation(reservedHeight, naturalHeight, generation);
        }

        PlayContentReveal();
        ScheduleGlideUpdate();
        return true;
    }

    private void ClearEntranceContentReservation()
    {
        if (!_entranceContentReserved) return;

        _entranceContentReserved = false;
        int generation = ++_contentSizeGeneration;
        double from = ContentRegion.Height;
        if (!double.IsFinite(from) || from <= 0)
            from = Math.Max(1, ContentRegion.ActualHeight);

        // The query may have been cleared while the reserved entrance was in
        // flight. Keep the landed shell stable, then retire the blank reserve
        // with a short height animation rather than snapping back to 66px.
        ContentRegion.Visibility = Visibility.Hidden;
        ContentRegion.ClipToBounds = true;
        if (!IsSpotlightOpen || AnimationConfig.ReduceMotion)
        {
            ContentRegion.BeginAnimation(WidthProperty, null);
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Width = double.NaN;
            ContentRegion.Height = double.NaN;
            ContentRegion.HorizontalAlignment = HorizontalAlignment.Stretch;
            ContentRegion.Opacity = 1;
            ContentRegionTranslate.Y = 0;
            ContentRegion.ClipToBounds = false;
            ContentRegion.Visibility = Visibility.Collapsed;
            return;
        }

        var collapse = CreateAnimation(from, 0, TimeSpan.FromMilliseconds(180),
            new CubicEase { EasingMode = EasingMode.EaseIn });
        collapse.Completed += (_, _) =>
        {
            if (generation != _contentSizeGeneration) return;
            ContentRegion.BeginAnimation(WidthProperty, null);
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Width = double.NaN;
            ContentRegion.Height = double.NaN;
            ContentRegion.HorizontalAlignment = HorizontalAlignment.Stretch;
            ContentRegion.Opacity = 1;
            ContentRegionTranslate.Y = 0;
            ContentRegion.ClipToBounds = false;
            ContentRegion.Visibility = Visibility.Collapsed;
        };
        ContentRegion.BeginAnimation(HeightProperty, collapse);
    }

    private void ScheduleContentResize()
    {
        if (_contentResizeQueued || AnimationConfig.ReduceMotion) return;
        _contentResizeQueued = true;
        // ActualHeight is still the pre-change height: the layout pass for the
        // new results has not run yet inside this dispatcher frame.
        double oldHeight = ContentRegion.ActualHeight;
        int generation = _contentSizeGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _contentResizeQueued = false;
            if (generation != _contentSizeGeneration || !_contentShown) return;
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Height = double.NaN;
            ContentRegion.UpdateLayout();
            double target = ContentRegion.ActualHeight;
            if (Math.Abs(target - oldHeight) < 1) return;
            BeginContentHeightAnimation(oldHeight, target, generation);
        });
    }

    private void BeginContentHeightAnimation(double from, double to, int generation)
    {
        ContentRegion.ClipToBounds = true;
        var resize = CreateAnimation(from, to, TimeSpan.FromMilliseconds(380),
            new CubicBezierEase(0.18, 1.25, 0.22, 1.0) { EasingMode = EasingMode.EaseIn });
        resize.Completed += (_, _) =>
        {
            if (generation != _contentSizeGeneration) return;
            // Back to auto-size so later content changes are never clamped.
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Height = double.NaN;
            ContentRegion.ClipToBounds = false;
        };
        ContentRegion.BeginAnimation(HeightProperty, resize);
    }

    private void ResetContentRegion()
    {
        ++_contentSizeGeneration;
        _contentShown = false;
        _entranceContentReserved = false;
        _contentResizeQueued = false;
        ContentRegion.BeginAnimation(WidthProperty, null);
        ContentRegion.BeginAnimation(HeightProperty, null);
        ContentRegion.BeginAnimation(OpacityProperty, null);
        ContentRegionTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        ContentRegion.Width = double.NaN;
        ContentRegion.Height = double.NaN;
        ContentRegion.HorizontalAlignment = HorizontalAlignment.Stretch;
        ContentRegion.Opacity = 1;
        ContentRegionTranslate.Y = 0;
        ContentRegion.ClipToBounds = false;
        ContentRegion.Visibility = Visibility.Collapsed;
    }

    private void UpdateSearchingGrace(bool searchingEligible)
    {
        if (!searchingEligible)
        {
            CancelSearchingGrace();
            return;
        }

        if (_searchingPanelArmed || _searchingGraceTimer?.IsEnabled == true) return;
        int generation = ++_searchingGraceGeneration;
        int sessionGeneration = _animationGeneration;
        string query = SearchBox.Text;
        var timer = new DispatcherTimer { Interval = SearchingPanelGrace };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (generation != _searchingGraceGeneration
                || !ReferenceEquals(timer, _searchingGraceTimer))
            {
                return;
            }
            _searchingGraceTimer = null;
            if (sessionGeneration != _animationGeneration
                || !IsSpotlightOpen
                || _isClosing
                || SearchBox.Text != query
                || !_viewModel.IsSearching
                || _viewModel.Results.Count != 0)
            {
                return;
            }
            _searchingPanelArmed = true;
            RefreshStatus();
        };
        _searchingGraceTimer = timer;
        timer.Start();
    }

    private void CancelSearchingGrace()
    {
        ++_searchingGraceGeneration;
        _searchingGraceTimer?.Stop();
        _searchingGraceTimer = null;
        _searchingPanelArmed = false;
    }

    private void SetEscBadgeVisible(bool visible)
    {
        if (_escBadgeVisible == visible) return;
        _escBadgeVisible = visible;
        double target = visible ? 1 : 0;
        if (AnimationConfig.ReduceMotion)
        {
            EscBadge.BeginAnimation(OpacityProperty, null);
            EscBadge.Opacity = target;
            return;
        }

        var fade = CreateAnimation(EscBadge.Opacity, target, TimeSpan.FromMilliseconds(140),
            new QuadraticEase { EasingMode = EasingMode.EaseOut });
        EscBadge.BeginAnimation(OpacityProperty, fade);
    }

    private void PlayContentReveal()
    {
        if (AnimationConfig.ReduceMotion) return;

        var ease = new CubicBezierEase(0.18, 1.2, 0.22, 1.0) { EasingMode = EasingMode.EaseIn };
        var fade = CreateAnimation(0, 1, TimeSpan.FromMilliseconds(300), ease);
        var slide = CreateAnimation(-10, 0, TimeSpan.FromMilliseconds(340), ease);
        ContentRegion.BeginAnimation(OpacityProperty, fade);
        ContentRegionTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void SetStatusPulse(bool active)
    {
        if (_statusPulseActive == active) return;
        _statusPulseActive = active;

        if (active && !AnimationConfig.ReduceMotion)
        {
            var pulse = new DoubleAnimation(1, 0.4, TimeSpan.FromMilliseconds(620))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Timeline.SetDesiredFrameRate(pulse, 30);
            StatusGlyph.BeginAnimation(OpacityProperty, pulse);
        }
        else
        {
            StatusGlyph.BeginAnimation(OpacityProperty, null);
            StatusGlyph.Opacity = 1;
        }
    }

    private (double Left, double Top) GetSpotlightTarget()
    {
        POINT point;
        if (!GetCursorPos(out point))
        {
            point = default;
        }

        IntPtr monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
        {
            Rect workArea = SystemParameters.WorkArea;
            return (workArea.Left + (workArea.Width - Width) / 2.0,
                workArea.Top + Math.Max(72, workArea.Height * 0.18));
        }

        double scale = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0
            ? dpiX / 96.0
            : 1.0;
        int width = (int)Math.Round(Width * scale);
        int left = info.rcWork.Left + (info.rcWork.Right - info.rcWork.Left - width) / 2;
        int top = info.rcWork.Top + Math.Max((int)Math.Round(72 * scale),
            (int)Math.Round((info.rcWork.Bottom - info.rcWork.Top) * 0.18));
        return (left / scale, top / scale);
    }

    private Action PrepareEntrance(double finalLeft, double finalTop, int generation)
    {
        if (AnimationConfig.ReduceMotion)
        {
            ResetNotchMorphSnapshot();
            Left = finalLeft;
            Top = finalTop;
            Shell.Opacity = 1;
            Shell.Visibility = Visibility.Visible;
            ShellScale.ScaleX = ShellScale.ScaleY = 1;
            ShellCornerRadius = ExpandedCornerRadius;
            ShellTopCornerRadius = ExpandedCornerRadius;
            ShellContent.Opacity = 1;
            ContentTranslate.Y = 0;
            RestoreShadow(animate: false);
            // Commit the final no-motion frame before handing source visibility
            // to Spotlight, for the same reused-layered-HWND reason as the
            // animated path below.
            return () => SetNotchMorphActive(true);
        }

        _entranceActive = true;
        var morphEase = CreateMorphEase();
        var contentEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        double startLeft = finalLeft;
        double startTop = finalTop;
        var finalShellSize = MeasureEntranceShell();
        double finalShellWidth = finalShellSize.Width;
        double finalShellHeight = finalShellSize.Height;
        double startShellWidth = finalShellWidth * 0.97;
        double startShellHeight = finalShellHeight * 0.82;
        double startTopRadius = 10;
        double startBottomRadius = 10;
        bool morphsFromNotch = TryGetNotchRect(out var notch);
        if (morphsFromNotch)
        {
            startShellWidth = notch.Width;
            startShellHeight = notch.Height;
            startTopRadius = Math.Max(0, notch.TopCornerRadius);
            startBottomRadius = Math.Max(0, notch.BottomCornerRadius);
            startLeft = notch.Left + notch.Width / 2.0 - ActualWidth / 2.0;
            startTop = notch.Top;
        }
        bool hasNotchSnapshot = morphsFromNotch
            && PrepareNotchMorphSnapshot();

        Shell.RenderTransformOrigin = new Point(0.5, 0.0);
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Center;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        if (morphsFromNotch)
        {
            SetMorphShadow(NotchShadowBlurRadius, NotchShadowDepth, NotchShadowOpacity);
            SolidColorBrush border = EnsureShellBorderBrush();
            border.BeginAnimation(Brush.OpacityProperty, null);
            border.Opacity = 0;
        }
        else
        {
            SetMorphShadow(
                SpotlightShadowBlurRadius,
                SpotlightShadowDepth,
                opacity: 0);
        }

        // Seed bases from the first rendered frame. WPF does not apply a new
        // animation clock until its first tick, so target-valued bases can be
        // exposed for one frame when Alt+Space is pressed repeatedly.
        Left = startLeft;
        Top = startTop;
        Shell.Opacity = 1;
        // If capture failed, leave the real source unobscured until the render
        // barrier releases the entrance. A valid snapshot is safe to present
        // over the source because it is pixel-identical at this first frame.
        Shell.Visibility = !morphsFromNotch || hasNotchSnapshot
            ? Visibility.Visible
            : Visibility.Hidden;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Width = startShellWidth;
        Shell.Height = startShellHeight;
        ShellCornerRadius = startBottomRadius;
        ShellTopCornerRadius = startTopRadius;
        if (morphsFromNotch) Shell.BorderThickness = new Thickness(0);
        ShellContent.Opacity = 0;
        ContentTranslate.Y = 8;
        var contentBlur = new System.Windows.Media.Effects.BlurEffect { Radius = 10 };
        ShellContent.Effect = contentBlur;

        var expandWidth = CreateAnimation(startShellWidth, finalShellWidth,
            MorphDuration, morphEase, synchronizedMorph: true);
        var expandHeight = CreateAnimation(startShellHeight, finalShellHeight,
            MorphDuration, morphEase, synchronizedMorph: true);
        var moveLeft = CreateAnimation(startLeft, finalLeft, MorphDuration, morphEase, synchronizedMorph: true);
        var moveTop = CreateAnimation(startTop, finalTop, MorphDuration, morphEase, synchronizedMorph: true);
        var corner = CreateAnimation(startBottomRadius, ExpandedCornerRadius, MorphDuration, morphEase, synchronizedMorph: true);
        var cornerTop = CreateAnimation(startTopRadius, ExpandedCornerRadius, MorphDuration, morphEase, synchronizedMorph: true);

        var contentFade = CreateAnimation(0, 1, TimeSpan.FromMilliseconds(300), contentEase);
        // The captured notch covers the short content delay. If capture is not
        // available, reveal Spotlight immediately instead of showing an empty
        // shell for 170 ms.
        contentFade.BeginTime = TimeSpan.FromMilliseconds(
            morphsFromNotch && hasNotchSnapshot ? 60 : morphsFromNotch ? 0 : 60);
        var contentSlide = CreateAnimation(8, 0, TimeSpan.FromMilliseconds(340), contentEase);
        contentSlide.BeginTime = contentFade.BeginTime;
        var blurOut = CreateAnimation(10, 0, TimeSpan.FromMilliseconds(340), contentEase);
        blurOut.BeginTime = contentFade.BeginTime;
        DoubleAnimation? notchFade = hasNotchSnapshot
            ? CreateAnimation(1, 0, TimeSpan.FromMilliseconds(260),
                new CubicEase { EasingMode = EasingMode.EaseInOut })
            : null;

        return () =>
        {
            if (generation != _animationGeneration || _isClosing || !IsSpotlightOpen) return;

            Shell.Visibility = Visibility.Visible;
            if (morphsFromNotch)
            {
                AnimateMorphShadow(
                    SpotlightShadowBlurRadius,
                    SpotlightShadowDepth,
                    SpotlightShadowOpacity,
                    MorphDuration);
            }
            else
            {
                RestoreShadow(animate: true);
            }

            expandWidth.Completed += (_, _) =>
            {
                if (generation != _animationGeneration || _isClosing || !IsSpotlightOpen) return;
                CompleteEntrance(finalLeft, finalTop);
            };

            Shell.BeginAnimation(WidthProperty, expandWidth);
            Shell.BeginAnimation(HeightProperty, expandHeight);
            BeginAnimation(LeftProperty, moveLeft);
            BeginAnimation(TopProperty, moveTop);
            BeginAnimation(ShellCornerRadiusProperty, corner);
            BeginAnimation(ShellTopCornerRadiusProperty, cornerTop);
            ShellContent.BeginAnimation(OpacityProperty, contentFade);
            ContentTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);
            contentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurOut);
            if (notchFade != null)
                NotchMorphSnapshot.BeginAnimation(OpacityProperty, notchFade);
            // The notch has no light outline; the border only belongs to the
            // expanded panel, so it fades in as the shell departs.
            if (morphsFromNotch) AnimateShellBorder(0, 1, MorphDuration);
            if (morphsFromNotch) SetNotchMorphActive(true);
        };
    }

    private void ReverseExitToEntrance()
    {
        PlaySpotlightClickSfx();
        int generation = ++_animationGeneration;
        MorphSnapshot current = FreezeCurrentMorphState();
        var target = GetSpotlightTarget();

        // PlayExit froze the results region into a fixed clipped box (and
        // collapses it once its fade lands). Restore auto layout before
        // measuring, or the reopen target misses the results panel height.
        if (!_entranceContentReserved)
        {
            ++_contentSizeGeneration;
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Width = double.NaN;
            ContentRegion.Height = double.NaN;
            ContentRegion.HorizontalAlignment = HorizontalAlignment.Stretch;
            ContentRegion.ClipToBounds = false;
            if (_contentShown) ContentRegion.Visibility = Visibility.Visible;
        }

        // Measure the expanded auto-height without exposing an intermediate
        // layout frame. The current explicit morph size is restored below as
        // the animation's starting value.
        Shell.Width = double.NaN;
        Shell.Height = double.NaN;
        Size finalSize = MeasureEntranceShell();

        _isClosing = false;
        _entranceActive = true;
        _lastDismissedQuery = null;
        SearchBox.IsEnabled = true;
        SetResultsDimmed(false, animate: false);

        Shell.RenderTransformOrigin = new Point(0.5, 0.0);
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Center;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        ResetNotchMorphSnapshot();
        AnimateMorphShadow(
            SpotlightShadowBlurRadius,
            SpotlightShadowDepth,
            SpotlightShadowOpacity,
            MorphDuration);

        // Keep the exact interrupted presentation as the base until the first
        // tick of the replacement clock.
        Left = current.Left;
        Top = current.Top;
        Shell.Opacity = current.ShellOpacity;
        Shell.Visibility = Visibility.Visible;
        ShellScale.ScaleX = ShellScale.ScaleY = 1;
        Shell.Width = current.Width;
        Shell.Height = current.Height;
        ShellCornerRadius = current.CornerRadius;
        ShellTopCornerRadius = current.TopCornerRadius;
        Shell.BorderThickness = new Thickness(0);
        ShellContent.Opacity = current.ContentOpacity;
        double currentEarOpacity = ShellLeftEar?.Opacity ?? 0;
        AnimateMorphEars(currentEarOpacity, 0, TimeSpan.FromMilliseconds(200), TimeSpan.Zero);
        ContentTranslate.Y = current.ContentTranslateY;
        var contentBlur = EnsureContentBlurEffect();
        contentBlur.Radius = current.ContentBlurRadius;

        var morphEase = CreateMorphEase();
        var contentEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };
        var expandWidth = CreateAnimation(current.Width, finalSize.Width,
            MorphDuration, morphEase, synchronizedMorph: true);
        var expandHeight = CreateAnimation(current.Height, finalSize.Height,
            MorphDuration, morphEase, synchronizedMorph: true);
        var moveLeft = CreateAnimation(current.Left, target.Left,
            MorphDuration, morphEase, synchronizedMorph: true);
        var moveTop = CreateAnimation(current.Top, target.Top,
            MorphDuration, morphEase, synchronizedMorph: true);
        var corner = CreateAnimation(current.CornerRadius, ExpandedCornerRadius,
            MorphDuration, morphEase, synchronizedMorph: true);
        var cornerTop = CreateAnimation(current.TopCornerRadius, ExpandedCornerRadius,
            MorphDuration, morphEase, synchronizedMorph: true);
        var shellReveal = CreateAnimation(current.ShellOpacity, 1,
            MorphDuration, morphEase, synchronizedMorph: true);
        var contentFade = CreateAnimation(current.ContentOpacity, 1,
            TimeSpan.FromMilliseconds(240), contentEase);
        var contentSlide = CreateAnimation(current.ContentTranslateY, 0,
            TimeSpan.FromMilliseconds(280), contentEase);
        var blurClear = CreateAnimation(current.ContentBlurRadius, 0,
            TimeSpan.FromMilliseconds(280), contentEase);

        expandWidth.Completed += (_, _) =>
        {
            if (generation != _animationGeneration || _isClosing || !IsSpotlightOpen) return;
            CompleteEntrance(target.Left, target.Top);
        };

        Shell.BeginAnimation(WidthProperty, expandWidth);
        Shell.BeginAnimation(HeightProperty, expandHeight);
        BeginAnimation(LeftProperty, moveLeft);
        BeginAnimation(TopProperty, moveTop);
        BeginAnimation(ShellCornerRadiusProperty, corner);
        BeginAnimation(ShellTopCornerRadiusProperty, cornerTop);
        Shell.BeginAnimation(OpacityProperty, shellReveal);
        ShellContent.BeginAnimation(OpacityProperty, contentFade);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);
        contentBlur.BeginAnimation(BlurEffect.RadiusProperty, blurClear);
        AnimateShellBorder(current.BorderOpacity, 1, MorphDuration);
        SetNotchMorphActive(true);

        string query = SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(query)) _ = _viewModel.SearchAsync(query);
        FocusSearchBox(generation);
    }

    private Size MeasureEntranceShell()
    {
        // ActualSize can still describe the final notch-sized frame when a
        // hidden layered window is shown again in the same dispatcher turn.
        // Measure from the fixed Spotlight canvas instead of reusing that
        // stale arranged size.
        double width = ActualWidth;
        if (!double.IsFinite(width) || width <= 0) width = Width;
        if (!double.IsFinite(width) || width <= 0) width = 720;

        Shell.Measure(new Size(width, double.PositiveInfinity));
        double height = Shell.DesiredSize.Height;
        if (!double.IsFinite(height) || height <= 0)
            height = Math.Max(1, Shell.ActualHeight);

        RuntimeLog.Debug(
            "SPOTLIGHT-MORPH",
            $"Entrance measure: target={width:F1}x{height:F1}, actual={Shell.ActualWidth:F1}x{Shell.ActualHeight:F1}, gen={_animationGeneration}");
        return new Size(width, height);
    }

    private void PlayExit(int generation)
    {
        if (AnimationConfig.ReduceMotion)
        {
            CompleteHide();
            return;
        }

        if (!TryGetNotchRect(out var notch))
        {
            double fromOpacity = Math.Clamp(Shell.Opacity, 0, 1);
            Shell.Opacity = fromOpacity;
            var fade = CreateAnimation(fromOpacity, 0, TimeSpan.FromMilliseconds(120),
                new QuadraticEase { EasingMode = EasingMode.EaseIn });
            fade.Completed += (_, _) =>
            {
                if (generation == _animationGeneration) CompleteHide();
            };
            Shell.BeginAnimation(OpacityProperty, fade);
            return;
        }

        MorphSnapshot current = FreezeCurrentMorphState();
        var morphEase = CreateMorphEase();
        var contentEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        Shell.RenderTransformOrigin = new Point(0.5, 0.0);
        Shell.CacheMode = null;
        ShellContent.CacheMode = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = true };
        Shell.HorizontalAlignment = HorizontalAlignment.Center;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        AnimateMorphShadow(
            NotchShadowBlurRadius,
            NotchShadowDepth,
            NotchShadowOpacity,
            MorphDuration);

        double targetWidth = Math.Max(1, notch.Width);
        double targetHeight = Math.Max(1, notch.Height);
        double targetBottomRadius = Math.Max(0, notch.BottomCornerRadius);
        double targetTopRadius = Math.Max(0, notch.TopCornerRadius);
        double targetLeft = notch.Left + notch.Width / 2.0 - ActualWidth / 2.0;
        double targetTop = notch.Top;

        // Preserve the live presentation as the replacement clock's base. The
        // return handoff commits the notch-sized target before clearing clocks.
        Left = current.Left;
        Top = current.Top;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Opacity = current.ShellOpacity;
        Shell.Width = current.Width;
        Shell.Height = current.Height;
        ShellCornerRadius = current.CornerRadius;
        ShellTopCornerRadius = current.TopCornerRadius;
        Shell.BorderThickness = new Thickness(0);
        ShellContent.Opacity = current.ContentOpacity;
        ContentTranslate.Y = current.ContentTranslateY;
        var contentBlur = EnsureContentBlurEffect();
        contentBlur.Radius = current.ContentBlurRadius;

        // The entrance morphs a content-free shell (results reveal only after
        // it lands), but an exit can start with the full results panel live.
        // Re-measuring the list on every width tick and re-rendering the blur
        // ramp over it costs more than a frame budget on weaker machines, so
        // the 560ms morph renders a handful of frames and reads as "skipped".
        // Freeze ShellContent's width so the search box and content don't squish
        // and trigger expensive text layout recalculations on every tick.
        ShellContent.Width = Math.Max(1, ShellContent.ActualWidth);
        ShellContent.HorizontalAlignment = HorizontalAlignment.Left;

        bool hadVisibleContent = ContentRegion.Visibility == Visibility.Visible;
        if (hadVisibleContent)
        {
            ++_contentSizeGeneration;
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Width = Math.Max(1, ContentRegion.ActualWidth);
            ContentRegion.Height = Math.Max(1, ContentRegion.ActualHeight);
            ContentRegion.HorizontalAlignment = HorizontalAlignment.Left;
            ContentRegion.ClipToBounds = true;
        }

        var shrinkWidth = CreateAnimation(current.Width, targetWidth,
            MorphDuration, morphEase, synchronizedMorph: true);
        var shrinkHeight = CreateAnimation(current.Height, targetHeight,
            MorphDuration, morphEase, synchronizedMorph: true);
        var moveLeft = CreateAnimation(current.Left, targetLeft, MorphDuration, morphEase, synchronizedMorph: true);
        var moveTop = CreateAnimation(current.Top, targetTop, MorphDuration, morphEase, synchronizedMorph: true);
        var corner = CreateAnimation(current.CornerRadius, targetBottomRadius, MorphDuration, morphEase, synchronizedMorph: true);
        var cornerTop = CreateAnimation(current.TopCornerRadius, targetTopRadius, MorphDuration, morphEase, synchronizedMorph: true);
        var shellNormalize = CreateAnimation(current.ShellOpacity, 1,
            MorphDuration, morphEase, synchronizedMorph: true);
        var contentFade = CreateAnimation(current.ContentOpacity, 0,
            TimeSpan.FromMilliseconds(170), contentEase);
        var contentSlide = CreateAnimation(current.ContentTranslateY, 0,
            TimeSpan.FromMilliseconds(210), contentEase);

        if (hadVisibleContent) contentFade.Completed += (_, _) =>
        {
            if (generation != _animationGeneration || !_isClosing) return;
            // The content is invisible from here on; dropping the frozen
            // region out of layout removes its render cost entirely.
            ContentRegion.Visibility = Visibility.Collapsed;
        };
        shrinkWidth.Completed += (_, _) =>
        {
            if (generation == _animationGeneration) BeginReturnHandoff(generation);
        };

        Shell.BeginAnimation(WidthProperty, shrinkWidth);
        Shell.BeginAnimation(HeightProperty, shrinkHeight);
        BeginAnimation(LeftProperty, moveLeft);
        BeginAnimation(TopProperty, moveTop);
        BeginAnimation(ShellCornerRadiusProperty, corner);
        BeginAnimation(ShellTopCornerRadiusProperty, cornerTop);
        Shell.BeginAnimation(OpacityProperty, shellNormalize);
        ShellContent.BeginAnimation(OpacityProperty, contentFade);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);
        // The blur ramp softens the collapse. Because we froze the layout width
        // and enabled BitmapCache, this per-frame GPU pass is now extremely cheap
        // even over a live results panel, so we no longer drop frames here.
        var blurIn = CreateAnimation(current.ContentBlurRadius, 12,
            TimeSpan.FromMilliseconds(210), contentEase);
        contentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurIn);
        // Shed the panel outline early so the shell arrives looking like the
        // borderless notch.
        AnimateShellBorder(current.BorderOpacity, 0, TimeSpan.FromMilliseconds(200));

        if (targetTopRadius == 0)
            AnimateMorphEars(0, 1, TimeSpan.FromMilliseconds(260), TimeSpan.FromMilliseconds(260));
        else
            AnimateMorphEars(0, 0, TimeSpan.Zero, TimeSpan.Zero);
    }

    private void BeginReturnHandoff(int generation)
    {
        if (generation != _animationGeneration || !IsSpotlightOpen) return;
        var handoffDuration = TimeSpan.FromMilliseconds(180);
        MorphSnapshot current = FreezeCurrentMorphState();
        double fromOpacity = current.ShellOpacity;

        // Keep the morph shell on the exact notch frame while the real notch takes
        // ownership underneath it. Both layers cross-fade on the same clock so
        // the fake shell never visibly snaps to the real dynamic island.
        // Preserve the handoff's first frame until the new clock ticks. Hide()
        // retires the shell before this clock is ever cleared at completion.
        Shell.Opacity = fromOpacity;
        // ClearMorphAnimations restored the border's base opacity; the shell
        // must stay borderless while it fades out over the real notch.
        if (_shellBorderBrush != null) _shellBorderBrush.Opacity = 0;
        GetMorphHost()?.BeginSpotlightReturnHandoff(handoffDuration);

        ShellContent.CacheMode = null;
        ShellContent.Effect = null;
        var handoffFade = CreateAnimation(fromOpacity, 0, handoffDuration,
            new CubicEase { EasingMode = EasingMode.EaseInOut }, synchronizedMorph: true);
        handoffFade.Completed += (_, _) =>
        {
            if (generation == _animationGeneration) CompleteHide();
        };
        Shell.BeginAnimation(OpacityProperty, handoffFade);
    }

    private void CompleteEntrance(double finalLeft, double finalTop)
    {
        ClearMorphAnimations();
        ResetNotchMorphSnapshot();
        Left = finalLeft;
        Top = finalTop;
        Shell.Opacity = 1;
        Shell.Visibility = Visibility.Visible;
        ShellScale.ScaleX = ShellScale.ScaleY = 1;
        Shell.Width = double.NaN;
        Shell.Height = double.NaN;
        ShellCornerRadius = ExpandedCornerRadius;
        ShellTopCornerRadius = ExpandedCornerRadius;
        Shell.BorderThickness = new Thickness(1);
        if (_shellBorderBrush != null) _shellBorderBrush.Opacity = 1;
        AnimateMorphEars(0, 0, TimeSpan.Zero, TimeSpan.Zero);
        ShellContent.Opacity = 1;
        ContentTranslate.Y = 0;
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        ShellContent.Effect = null;
        Shell.Effect = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        // Top-aligned auto-height: the shell hugs its content inside the
        // fixed-size transparent window, so growth never resizes the HWND.
        Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.RenderTransformOrigin = new Point(0.5, 0.5);
        RestoreShadow(animate: false);

        // Results that arrived mid-morph waited for the shell to land. A
        // reserved restored query reveals in place; an unreserved async result
        // uses the normal height reveal.
        _entranceActive = false;
        if (_pendingContentReveal)
        {
            _pendingContentReveal = false;
            if (!RevealEntranceContentReservation())
                SetContentShown(true);
        }
        else
        {
            ClearEntranceContentReservation();
        }
    }

    private void CompleteHide()
    {
        // Set base values to 0 before removing animation clocks. A completed
        // WPF animation otherwise reveals its old base value for the compositor
        // frame. We avoid Visibility.Hidden to prevent DWM white-frame flashes.
        CancelPendingFreshEntrance();
        Shell.Opacity = 0;
        NotchMorphSnapshot.Opacity = 0;
        ClearMorphAnimations();
        ReleaseMorphSession();
        _pendingLaunchQuery = null;
        ClearLaunchFailure();
        SetResultsDimmed(false, animate: false);
        UpdateSearchingGrace(false);
        ResetSelectionGlide();
        SearchBox.Text = string.Empty;
        _viewModel.Reset();
        ResetContentRegion();
        SearchBox.IsEnabled = true;
        _isClosing = false;
        ResetMorphVisuals();
        ParkWindow();
    }

    private void ResetMorphVisuals()
    {
        CancelPendingFreshEntrance();
        _entranceActive = false;
        _pendingContentReveal = false;
        ClearMorphAnimations();
        ResetNotchMorphSnapshot();
        Shell.Effect = null;
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        ShellContent.Effect = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.RenderTransformOrigin = new Point(0.5, 0.0);
        Shell.Opacity = 0;
        ShellScale.ScaleX = ShellScale.ScaleY = 1;
        ShellShake.X = 0;
        Shell.Width = double.NaN;
        Shell.Height = double.NaN;
        ShellCornerRadius = ExpandedCornerRadius;
        ShellTopCornerRadius = ExpandedCornerRadius;
        if (_shellBorderBrush != null) _shellBorderBrush.Opacity = 1;
        ShellContent.Opacity = 1;
        ContentTranslate.Y = 0;
    }

    private bool PrepareNotchMorphSnapshot()
    {
        ResetNotchMorphSnapshot();
        ISpotlightMorphHost? morphHost = GetMorphHost();
        if (morphHost == null) return false;

        ImageSource? source = morphHost.CaptureSpotlightMorphVisual();
        if (source == null) return false;

        NotchMorphSnapshotBrush.ImageSource = source;
        // The rounded Border is the animated viewport. Keep the bitmap at its
        // captured DIP size (ImageBrush.Stretch=None) so a 600x310 clock,
        // timer, camera, or secondary view is cropped rather than distorted
        // while the shell heads toward the 720x66 Spotlight target.
        NotchMorphSnapshot.Width = double.NaN;
        NotchMorphSnapshot.Height = double.NaN;
        // The source must already be visible before the first composition.
        // Relying on a From=1 animation over a zero base can expose a blank
        // frame when query/results cleanup delays the animation clock.
        NotchMorphSnapshot.Opacity = 1;
        NotchMorphSnapshot.Visibility = Visibility.Visible;
        return true;
    }

    private void ResetNotchMorphSnapshot()
    {
        NotchMorphSnapshot.BeginAnimation(OpacityProperty, null);
        NotchMorphSnapshot.BeginAnimation(WidthProperty, null);
        NotchMorphSnapshot.BeginAnimation(HeightProperty, null);
        NotchMorphSnapshot.Opacity = 0;
        NotchMorphSnapshotBrush.ImageSource = null;
        NotchMorphSnapshot.Width = double.NaN;
        NotchMorphSnapshot.Height = double.NaN;
    }

    private MorphSnapshot FreezeCurrentMorphState()
    {
        var shadow = Shell.Effect as DropShadowEffect;
        double presentedWidth = Shell.Width;
        if (!double.IsFinite(presentedWidth) || presentedWidth <= 0)
            presentedWidth = Math.Max(1, Shell.ActualWidth);
        double presentedHeight = Shell.Height;
        if (!double.IsFinite(presentedHeight) || presentedHeight <= 0)
            presentedHeight = Math.Max(1, Shell.ActualHeight);

        var snapshot = new MorphSnapshot(
            Left, Top, presentedWidth, presentedHeight,
            Math.Clamp(Shell.Opacity, 0, 1),
            Math.Clamp(_shellBorderBrush?.Opacity ?? 1, 0, 1),
            ShellCornerRadius, ShellTopCornerRadius,
            ShellContent.Opacity, ContentTranslate.Y,
            (ShellContent.Effect as BlurEffect)?.Radius ?? 0,
            shadow?.BlurRadius ?? NotchShadowBlurRadius,
            shadow?.ShadowDepth ?? NotchShadowDepth,
            shadow?.Opacity ?? 0);
        ClearMorphAnimations();
        // Clearing a fade reveals its base value. Retire the opening snapshot
        // explicitly before an interrupted entrance is redirected into exit.
        ResetNotchMorphSnapshot();
        Left = snapshot.Left;
        Top = snapshot.Top;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Opacity = snapshot.ShellOpacity;
        Shell.Width = snapshot.Width;
        Shell.Height = snapshot.Height;
        ShellCornerRadius = snapshot.CornerRadius;
        ShellTopCornerRadius = snapshot.TopCornerRadius;
        if (_shellBorderBrush != null) _shellBorderBrush.Opacity = snapshot.BorderOpacity;
        ShellContent.Opacity = snapshot.ContentOpacity;
        ContentTranslate.Y = snapshot.ContentTranslateY;
        EnsureContentBlurEffect().Radius = snapshot.ContentBlurRadius;
        SetMorphShadow(snapshot.ShadowBlurRadius, snapshot.ShadowDepth, snapshot.ShadowOpacity);
        return snapshot;
    }

    private BlurEffect EnsureContentBlurEffect()
    {
        if (ShellContent.Effect is BlurEffect blur)
            return blur;

        blur = new BlurEffect { Radius = 0 };
        ShellContent.Effect = blur;
        return blur;
    }

    private void ClearMorphAnimations()
    {
        Shell.BeginAnimation(OpacityProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ShellShake.BeginAnimation(TranslateTransform.XProperty, null);
        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(ShellCornerRadiusProperty, null);
        BeginAnimation(ShellTopCornerRadiusProperty, null);
        _shellBorderBrush?.BeginAnimation(Brush.OpacityProperty, null);
        ShellContent.BeginAnimation(OpacityProperty, null);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        NotchMorphSnapshot.Visibility = Visibility.Hidden;
        NotchMorphSnapshot.BeginAnimation(OpacityProperty, null);
        NotchMorphSnapshot.BeginAnimation(WidthProperty, null);
        NotchMorphSnapshot.BeginAnimation(HeightProperty, null);
        NotchMorphSnapshot.Opacity = 0;
        if (Shell.Effect is DropShadowEffect shellShadow)
        {
            shellShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
            shellShadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, null);
            shellShadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        }
        if (ShellContent.Effect is System.Windows.Media.Effects.BlurEffect blur)
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
    }

    private bool TryGetNotchRect(
        out (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius) rect)
    {
        if (GetMorphHost() is { } morphHost)
        {
            rect = morphHost.GetSpotlightMorphRect();
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private void SetNotchMorphActive(bool active)
    {
        GetMorphHost()?.SetSpotlightMorphActive(active);
    }

    private void SetMorphSessionActive(bool active)
    {
        GetMorphHost()?.SetSpotlightMorphSessionActive(active);
    }

    private void ReleaseMorphSession()
    {
        SetNotchMorphActive(false);
        SetMorphSessionActive(false);
    }

    private ISpotlightMorphHost? GetMorphHost() =>
        MorphHostOverride ?? Owner as ISpotlightMorphHost;

    /// <summary>
    /// Swaps the shared border resource for a window-local brush once, so its
    /// opacity can animate without touching other users of the resource.
    /// </summary>
    private SolidColorBrush EnsureShellBorderBrush()
    {
        if (_shellBorderBrush != null) return _shellBorderBrush;
        _shellBorderBrush = new SolidColorBrush(System.Windows.Media.Colors.Transparent);
        Shell.BorderBrush = _shellBorderBrush;
        return _shellBorderBrush;
    }

    private void AnimateShellBorder(double from, double to, TimeSpan duration)
    {
        SolidColorBrush brush = EnsureShellBorderBrush();
        brush.BeginAnimation(Brush.OpacityProperty, null);
        brush.Opacity = from;
        var fade = CreateAnimation(from, to, duration,
            new QuadraticEase { EasingMode = EasingMode.EaseOut });
        brush.BeginAnimation(Brush.OpacityProperty, fade);
    }

    private void AnimateMorphEars(double fromOpacity, double toOpacity, TimeSpan duration, TimeSpan beginTime)
    {
        if (ShellLeftEar == null || ShellRightEar == null) return;

        ShellLeftEar.BeginAnimation(OpacityProperty, null);
        ShellRightEar.BeginAnimation(OpacityProperty, null);
        ShellLeftEar.Opacity = fromOpacity;
        ShellRightEar.Opacity = fromOpacity;

        if (Math.Abs(fromOpacity - toOpacity) < 0.001 || duration <= TimeSpan.Zero) return;

        var anim = new DoubleAnimation(fromOpacity, toOpacity, duration)
        {
            BeginTime = beginTime,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(anim, Math.Min(60, AnimationConfig.TargetFps));

        ShellLeftEar.BeginAnimation(OpacityProperty, anim);
        ShellRightEar.BeginAnimation(OpacityProperty, anim);
    }

    private void RestoreShadow(bool animate)
    {
        SetMorphShadow(
            SpotlightShadowBlurRadius,
            SpotlightShadowDepth,
            animate ? 0 : SpotlightShadowOpacity);
        if (!animate) return;

        var shadow = (DropShadowEffect)Shell.Effect;
        var fade = CreateAnimation(0, SpotlightShadowOpacity, TimeSpan.FromMilliseconds(180),
            new QuadraticEase { EasingMode = EasingMode.EaseOut });
        shadow.BeginAnimation(DropShadowEffect.OpacityProperty, fade);
    }

    private void SetMorphShadow(double blurRadius, double shadowDepth, double opacity)
    {
        var shadow = Shell.Effect as DropShadowEffect;
        if (shadow == null)
        {
            shadow = new DropShadowEffect
            {
                Color = Color.FromRgb(2, 4, 8),
                Direction = 270,
                RenderingBias = RenderingBias.Performance
            };
            Shell.Effect = shadow;
        }

        shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        shadow.BeginAnimation(DropShadowEffect.ShadowDepthProperty, null);
        shadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        shadow.BlurRadius = blurRadius;
        shadow.ShadowDepth = shadowDepth;
        shadow.Opacity = opacity;
    }

    private void AnimateMorphShadow(
        double targetBlurRadius,
        double targetShadowDepth,
        double targetOpacity,
        TimeSpan duration)
    {
        if (Shell.Effect is not DropShadowEffect shadow)
        {
            SetMorphShadow(targetBlurRadius, targetShadowDepth, targetOpacity);
            return;
        }

        double startBlurRadius = shadow.BlurRadius;
        double startShadowDepth = shadow.ShadowDepth;
        double startOpacity = shadow.Opacity;
        SetMorphShadow(startBlurRadius, startShadowDepth, startOpacity);

        var ease = CreateMorphEase();
        shadow.BeginAnimation(
            DropShadowEffect.BlurRadiusProperty,
            CreateAnimation(startBlurRadius, targetBlurRadius, duration, ease, synchronizedMorph: true));
        shadow.BeginAnimation(
            DropShadowEffect.ShadowDepthProperty,
            CreateAnimation(startShadowDepth, targetShadowDepth, duration, ease, synchronizedMorph: true));
        shadow.BeginAnimation(
            DropShadowEffect.OpacityProperty,
            CreateAnimation(startOpacity, targetOpacity, duration, ease, synchronizedMorph: true));
    }

    private static DoubleAnimation CreateAnimation(
        double from,
        double to,
        TimeSpan duration,
        IEasingFunction easing,
        bool synchronizedMorph = false)
    {
        var animation = new DoubleAnimation(from, to, duration) { EasingFunction = easing };
        Timeline.SetDesiredFrameRate(animation,
            synchronizedMorph ? Math.Min(60, AnimationConfig.TargetFps) : AnimationConfig.TargetFps);
        return animation;
    }

    private static IEasingFunction CreateMorphEase() =>
        new CubicBezierEase(0.16, 1.0, 0.3, 1.0) { EasingMode = EasingMode.EaseIn };

    public static readonly DependencyProperty ShellCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(ShellCornerRadius),
            typeof(double),
            typeof(SpotlightWindow),
            new PropertyMetadata(ExpandedCornerRadius, OnShellCornerRadiusChanged));

    // The classic notch has square top corners while the dynamic island is a
    // full pill; splitting top and bottom radii lets the morph land on either.
    public static readonly DependencyProperty ShellTopCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(ShellTopCornerRadius),
            typeof(double),
            typeof(SpotlightWindow),
            new PropertyMetadata(ExpandedCornerRadius, OnShellCornerRadiusChanged));

    public double ShellCornerRadius
    {
        get => (double)GetValue(ShellCornerRadiusProperty);
        set => SetValue(ShellCornerRadiusProperty, value);
    }

    public double ShellTopCornerRadius
    {
        get => (double)GetValue(ShellTopCornerRadiusProperty);
        set => SetValue(ShellTopCornerRadiusProperty, value);
    }

    private static void OnShellCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightWindow window)
        {
            double top = Math.Max(0, window.ShellTopCornerRadius);
            double bottom = Math.Max(0, window.ShellCornerRadius);
            var corners = new CornerRadius(top, top, bottom, bottom);
            window.Shell.CornerRadius = corners;
            window.NotchMorphSnapshot.CornerRadius = corners;
        }
    }

    private readonly record struct MorphSnapshot(
        double Left,
        double Top,
        double Width,
        double Height,
        double ShellOpacity,
        double BorderOpacity,
        double CornerRadius,
        double TopCornerRadius,
        double ContentOpacity,
        double ContentTranslateY,
        double ContentBlurRadius,
        double ShadowBlurRadius,
        double ShadowDepth,
        double ShadowOpacity);
}
