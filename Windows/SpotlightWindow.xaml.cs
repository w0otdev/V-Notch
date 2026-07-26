using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Spotlight;
using VNotch.ViewModels;
using static VNotch.Services.Win32Interop;

namespace VNotch;

public partial class SpotlightWindow : Window
{
    private const double ExpandedCornerRadius = 20;
    private const int PageJump = 4;
    private static readonly TimeSpan MorphDuration = TimeSpan.FromMilliseconds(560);
    private readonly SpotlightViewModel _viewModel;
    private readonly SpotlightLauncher _launcher;
    private bool _allowClose;
    private bool _isClosing;
    private bool _statusPulseActive;
    private int _animationGeneration;

    internal SpotlightWindow(SpotlightViewModel viewModel, SpotlightLauncher launcher)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _launcher = launcher;
        DataContext = viewModel;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        PlaceholderText.Text = Loc.Get("spotlight.placeholder");
        NavigateHintText.Text = Loc.Get("spotlight.navigate");
        OpenHintText.Text = Loc.Get("spotlight.open");
        RevealHintText.Text = Loc.Get("spotlight.reveal");
        EscHintText.Text = Loc.Get("spotlight.clear");
        SearchBox.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, Loc.Get("spotlight.placeholder"));

        var resultsView = System.Windows.Data.CollectionViewSource.GetDefaultView(_viewModel.Results);
        resultsView.GroupDescriptions.Add(
            new System.Windows.Data.PropertyGroupDescription(nameof(SpotlightSearchItem.SectionTitle)));

        // Activation from the global hotkey can land after ShowSpotlight has
        // already tried to focus; whenever the window becomes active the
        // search box must own the keyboard.
        Activated += (_, _) =>
        {
            if (!_isClosing && SearchBox.IsEnabled && !SearchBox.IsKeyboardFocused)
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }
        };

        _viewModel.Results.CollectionChanged += (_, _) => RefreshStatus();
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SpotlightViewModel.IsSearching)
                or nameof(SpotlightViewModel.HasNoResults)
                or nameof(SpotlightViewModel.IsWindowsSearchUnavailable))
            {
                RefreshStatus();
            }
            else if (args.PropertyName == nameof(SpotlightViewModel.SelectedResult))
            {
                UpdateRevealHint();
            }
        };
    }

    internal void ShowSpotlight()
    {
        if (_isClosing) return;

        int generation = ++_animationGeneration;
        ResetMorphVisuals();
        _viewModel.Reset();
        SearchBox.Text = string.Empty;
        RefreshStatus();
        Show();
        UpdateLayout();

        var target = GetSpotlightTarget();
        PlayEntrance(target.Left, target.Top, generation);
        FocusSearchBox(generation);
    }

    private void FocusSearchBox(int generation)
    {
        ForceForeground();
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        if (SearchBox.IsKeyboardFocused) return;

        // Windows can refuse the foreground switch while another process holds
        // the input lock; retry after the pending input queue settles.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (generation != _animationGeneration || !IsVisible || _isClosing) return;
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
        if (!IsVisible)
        {
            ShowSpotlight();
            return;
        }

        DismissFromGlobalShortcut();
    }

    internal void DismissFromGlobalShortcut()
    {
        if (!IsVisible) return;
        if (_isClosing)
        {
            // A repeated shortcut must never be swallowed by an in-flight
            // deactivation animation. Invalidate its callbacks and finish now.
            ++_animationGeneration;
            CompleteHide();
            return;
        }

        HideSpotlight();
    }

    internal void HandleGlobalEscape()
    {
        if (!IsVisible) return;
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
        if (!IsVisible || _isClosing) return;
        _isClosing = true;
        _viewModel.CancelPendingSearch();
        SearchBox.IsEnabled = false;
        PlayExit(++_animationGeneration);
    }

    internal void Shutdown()
    {
        ++_animationGeneration;
        _allowClose = true;
        ClearMorphAnimations();
        SetNotchMorphActive(false);
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

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        PlaceholderText.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        await _viewModel.SearchAsync(SearchBox.Text);
        RefreshStatus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.None:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Tab when Keyboard.Modifiers == ModifierKeys.Shift:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                MoveSelection(PageJump);
                e.Handled = true;
                break;
            case Key.PageUp:
                MoveSelection(-PageJump);
                e.Handled = true;
                break;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HandleGlobalEscape();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) RevealSelected();
            else LaunchSelected();
            e.Handled = true;
        }
    }

    private void ResultItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: SpotlightSearchItem item }) return;
        _viewModel.SelectedResult = item;
        LaunchSelected();
        // If the launch failed and the window stays open, typing must keep working.
        if (IsVisible && !_isClosing) SearchBox.Focus();
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        // ApplicationIdle can be starved by the notch's continuous media/render
        // work. Input priority guarantees an outside click dismisses Spotlight.
        Dispatcher.BeginInvoke(HideSpotlight, DispatcherPriority.Input);
    }

    private void MoveSelection(int direction)
    {
        int count = _viewModel.Results.Count;
        if (count == 0) return;
        int current = ResultsList.SelectedIndex;
        ResultsList.SelectedIndex = current < 0
            ? 0
            : Math.Clamp(current + direction, 0, count - 1);
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void LaunchSelected()
    {
        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        if (selected == null) return;
        if (_launcher.TryLaunch(selected)) HideSpotlight();
    }

    private void RevealSelected()
    {
        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        if (selected == null) return;
        if (_launcher.TryRevealInExplorer(selected)) HideSpotlight();
    }

    private void UpdateRevealHint()
    {
        bool canReveal = _viewModel.SelectedResult is { } selected && SpotlightLauncher.CanReveal(selected);
        RevealHintGroup.Visibility = canReveal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshStatus()
    {
        int resultCount = _viewModel.Results.Count;
        bool hasQuery = !string.IsNullOrWhiteSpace(SearchBox.Text);
        bool hasResults = resultCount > 0;
        bool showStatus = hasQuery && !hasResults &&
                          (_viewModel.IsSearching || _viewModel.IsWindowsSearchUnavailable || _viewModel.HasNoResults);

        bool contentWasVisible = ContentRegion.Visibility == Visibility.Visible;
        ContentRegion.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        if (hasQuery && !contentWasVisible) PlayContentReveal();

        ResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        StatusPanel.Visibility = showStatus ? Visibility.Visible : Visibility.Collapsed;
        UpdateRevealHint();
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
        SetStatusPulse(showStatus && _viewModel.IsSearching);
    }

    private void PlayContentReveal()
    {
        if (AnimationConfig.ReduceMotion) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = CreateAnimation(0, 1, TimeSpan.FromMilliseconds(200), ease);
        var slide = CreateAnimation(-6, 0, TimeSpan.FromMilliseconds(240), ease);
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

    private void PlayEntrance(double finalLeft, double finalTop, int generation)
    {
        if (AnimationConfig.ReduceMotion)
        {
            Left = finalLeft;
            Top = finalTop;
            Shell.Opacity = 1;
            ShellScale.ScaleX = ShellScale.ScaleY = 1;
            ShellCornerRadius = ExpandedCornerRadius;
            ShellContent.Opacity = 1;
            ContentTranslate.Y = 0;
            RestoreShadow(animate: false);
            SetNotchMorphActive(true);
            return;
        }

        var morphEase = CreateMorphEase();
        var contentEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        double startLeft = finalLeft;
        double startTop = finalTop;
        double finalShellWidth = Math.Max(1, Shell.ActualWidth);
        double finalShellHeight = Math.Max(1, Shell.ActualHeight);
        double startShellWidth = finalShellWidth * 0.97;
        double startShellHeight = finalShellHeight * 0.82;
        double startRadius = 16;
        bool morphsFromNotch = TryGetNotchRect(out var notch);
        if (morphsFromNotch)
        {
            startShellWidth = notch.Width;
            startShellHeight = notch.Height;
            startRadius = Math.Max(0, notch.CornerRadius);
            startLeft = notch.Left + notch.Width / 2.0 - ActualWidth / 2.0;
            startTop = notch.Top;
        }

        Shell.RenderTransformOrigin = new Point(0.5, 0.0);
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Center;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.Effect = null;

        // Set final base values first so clearing completed animations cannot snap back.
        Left = finalLeft;
        Top = finalTop;
        Shell.Opacity = 1;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Width = finalShellWidth;
        Shell.Height = finalShellHeight;
        ShellCornerRadius = ExpandedCornerRadius;
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
        var corner = CreateAnimation(startRadius, ExpandedCornerRadius, MorphDuration, morphEase, synchronizedMorph: true);

        var contentFade = CreateAnimation(0, 1, TimeSpan.FromMilliseconds(300), contentEase);
        contentFade.BeginTime = TimeSpan.FromMilliseconds(morphsFromNotch ? 170 : 60);
        var contentSlide = CreateAnimation(8, 0, TimeSpan.FromMilliseconds(340), contentEase);
        contentSlide.BeginTime = contentFade.BeginTime;
        var blurOut = CreateAnimation(10, 0, TimeSpan.FromMilliseconds(340), contentEase);
        blurOut.BeginTime = contentFade.BeginTime;

        expandWidth.Completed += (_, _) =>
        {
            if (generation != _animationGeneration || _isClosing || !IsVisible) return;
            CompleteEntrance(finalLeft, finalTop);
        };

        Shell.BeginAnimation(WidthProperty, expandWidth);
        Shell.BeginAnimation(HeightProperty, expandHeight);
        BeginAnimation(LeftProperty, moveLeft);
        BeginAnimation(TopProperty, moveTop);
        BeginAnimation(ShellCornerRadiusProperty, corner);
        ShellContent.BeginAnimation(OpacityProperty, contentFade);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);
        contentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurOut);
        if (morphsFromNotch) SetNotchMorphActive(true);
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
            var fade = CreateAnimation(Shell.Opacity, 0, TimeSpan.FromMilliseconds(120),
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
        ShellContent.CacheMode = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Center;
        Shell.VerticalAlignment = VerticalAlignment.Top;
        FadeOutShadow(generation);

        double targetWidth = Math.Max(1, notch.Width);
        double targetHeight = Math.Max(1, notch.Height);
        double targetRadius = Math.Max(0, notch.CornerRadius);
        double targetLeft = notch.Left + notch.Width / 2.0 - ActualWidth / 2.0;
        double targetTop = notch.Top;

        // Final base values keep the last frame stable until the window is hidden.
        Left = targetLeft;
        Top = targetTop;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Width = targetWidth;
        Shell.Height = targetHeight;
        ShellCornerRadius = targetRadius;
        ShellContent.Opacity = 0;
        ContentTranslate.Y = 9;
        var contentBlur = new System.Windows.Media.Effects.BlurEffect { Radius = 0 };
        ShellContent.Effect = contentBlur;

        var shrinkWidth = CreateAnimation(current.Width, targetWidth,
            MorphDuration, morphEase, synchronizedMorph: true);
        var shrinkHeight = CreateAnimation(current.Height, targetHeight,
            MorphDuration, morphEase, synchronizedMorph: true);
        var moveLeft = CreateAnimation(current.Left, targetLeft, MorphDuration, morphEase, synchronizedMorph: true);
        var moveTop = CreateAnimation(current.Top, targetTop, MorphDuration, morphEase, synchronizedMorph: true);
        var corner = CreateAnimation(current.CornerRadius, targetRadius, MorphDuration, morphEase, synchronizedMorph: true);
        var contentFade = CreateAnimation(current.ContentOpacity, 0,
            TimeSpan.FromMilliseconds(170), contentEase);
        var contentSlide = CreateAnimation(current.ContentTranslateY, 9,
            TimeSpan.FromMilliseconds(210), contentEase);
        var blurIn = CreateAnimation(0, 12, TimeSpan.FromMilliseconds(210), contentEase);

        shrinkWidth.Completed += (_, _) =>
        {
            if (generation == _animationGeneration) BeginReturnHandoff(generation);
        };

        Shell.BeginAnimation(WidthProperty, shrinkWidth);
        Shell.BeginAnimation(HeightProperty, shrinkHeight);
        BeginAnimation(LeftProperty, moveLeft);
        BeginAnimation(TopProperty, moveTop);
        BeginAnimation(ShellCornerRadiusProperty, corner);
        ShellContent.BeginAnimation(OpacityProperty, contentFade);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);
        contentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurIn);
    }

    private void BeginReturnHandoff(int generation)
    {
        if (generation != _animationGeneration || !IsVisible) return;

        // Keep the morph shell on the exact notch frame while the real notch takes
        // ownership underneath it. Fading only at this final frame prevents the
        // source notch from flashing before the moving window has arrived.
        ClearMorphAnimations();
        if (Owner is MainWindow mainWindow)
        {
            mainWindow.SetSpotlightMorphActive(false);
            mainWindow.PlayNotchReturnBounce();
        }

        ShellContent.CacheMode = null;
        ShellContent.Effect = null;
        var handoffFade = CreateAnimation(1, 0, TimeSpan.FromMilliseconds(100),
            new CubicEase { EasingMode = EasingMode.EaseOut }, synchronizedMorph: true);
        handoffFade.Completed += (_, _) =>
        {
            if (generation == _animationGeneration) CompleteHide();
        };
        Shell.BeginAnimation(OpacityProperty, handoffFade);
    }

    private void CompleteEntrance(double finalLeft, double finalTop)
    {
        ClearMorphAnimations();
        Left = finalLeft;
        Top = finalTop;
        Shell.Opacity = 1;
        ShellScale.ScaleX = ShellScale.ScaleY = 1;
        Shell.Width = double.NaN;
        Shell.Height = double.NaN;
        ShellCornerRadius = ExpandedCornerRadius;
        ShellContent.Opacity = 1;
        ContentTranslate.Y = 0;
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        ShellContent.Effect = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        Shell.VerticalAlignment = VerticalAlignment.Stretch;
        Shell.RenderTransformOrigin = new Point(0.5, 0.5);
        RestoreShadow(animate: true);
    }

    private void CompleteHide()
    {
        ClearMorphAnimations();
        SetNotchMorphActive(false);
        Hide();
        SearchBox.Text = string.Empty;
        _viewModel.Reset();
        SearchBox.IsEnabled = true;
        _isClosing = false;
        ResetMorphVisuals();
    }

    private void ResetMorphVisuals()
    {
        ClearMorphAnimations();
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        ShellContent.Effect = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        Shell.VerticalAlignment = VerticalAlignment.Stretch;
        Shell.RenderTransformOrigin = new Point(0.5, 0.0);
        Shell.Opacity = 0;
        ShellScale.ScaleX = ShellScale.ScaleY = 1;
        Shell.Width = double.NaN;
        Shell.Height = double.NaN;
        ShellCornerRadius = ExpandedCornerRadius;
        ShellContent.Opacity = 1;
        ContentTranslate.Y = 0;
    }

    private MorphSnapshot FreezeCurrentMorphState()
    {
        var snapshot = new MorphSnapshot(
            Left, Top, Math.Max(1, Shell.ActualWidth), Math.Max(1, Shell.ActualHeight),
            ShellCornerRadius,
            ShellContent.Opacity, ContentTranslate.Y);
        ClearMorphAnimations();
        Left = snapshot.Left;
        Top = snapshot.Top;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Width = snapshot.Width;
        Shell.Height = snapshot.Height;
        ShellCornerRadius = snapshot.CornerRadius;
        ShellContent.Opacity = snapshot.ContentOpacity;
        ContentTranslate.Y = snapshot.ContentTranslateY;
        return snapshot;
    }

    private void ClearMorphAnimations()
    {
        Shell.BeginAnimation(OpacityProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        Shell.BeginAnimation(WidthProperty, null);
        Shell.BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(ShellCornerRadiusProperty, null);
        ShellContent.BeginAnimation(OpacityProperty, null);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        if (ShellContent.Effect is System.Windows.Media.Effects.BlurEffect blur)
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
    }

    private bool TryGetNotchRect(
        out (double Left, double Top, double Width, double Height, double CornerRadius) rect)
    {
        if (Owner is MainWindow mainWindow)
        {
            rect = mainWindow.GetNotchScreenRect();
            return rect.Width > 0 && rect.Height > 0;
        }

        rect = default;
        return false;
    }

    private void SetNotchMorphActive(bool active)
    {
        if (Owner is MainWindow mainWindow)
            mainWindow.SetSpotlightMorphActive(active);
    }

    private void RestoreShadow(bool animate)
    {
        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Color.FromRgb(2, 4, 8),
            BlurRadius = 42,
            ShadowDepth = 14,
            Opacity = animate ? 0 : 0.78
        };
        Shell.Effect = shadow;
        if (!animate) return;

        var fade = CreateAnimation(0, 0.78, TimeSpan.FromMilliseconds(180),
            new QuadraticEase { EasingMode = EasingMode.EaseOut });
        shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, fade);
    }

    private void FadeOutShadow(int generation)
    {
        if (Shell.Effect is not System.Windows.Media.Effects.DropShadowEffect shadow)
        {
            Shell.Effect = null;
            return;
        }

        double currentOpacity = shadow.Opacity;
        var fade = CreateAnimation(currentOpacity, 0, TimeSpan.FromMilliseconds(150),
            new CubicEase { EasingMode = EasingMode.EaseOut });
        fade.Completed += (_, _) =>
        {
            if (generation == _animationGeneration && _isClosing)
                Shell.Effect = null;
        };
        shadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty, fade);
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

    private static ExponentialEase CreateMorphEase() =>
        new() { EasingMode = EasingMode.EaseOut, Exponent = 7 };

    public static readonly DependencyProperty ShellCornerRadiusProperty =
        DependencyProperty.Register(
            nameof(ShellCornerRadius),
            typeof(double),
            typeof(SpotlightWindow),
            new PropertyMetadata(ExpandedCornerRadius, OnShellCornerRadiusChanged));

    public double ShellCornerRadius
    {
        get => (double)GetValue(ShellCornerRadiusProperty);
        set => SetValue(ShellCornerRadiusProperty, value);
    }

    private static void OnShellCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SpotlightWindow window)
            window.Shell.CornerRadius = new CornerRadius((double)e.NewValue);
    }

    private readonly record struct MorphSnapshot(
        double Left,
        double Top,
        double Width,
        double Height,
        double CornerRadius,
        double ContentOpacity,
        double ContentTranslateY);
}
