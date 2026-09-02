using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using VNotch.Contracts;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Modules;
using VNotch.Services;
using VNotch.ViewModels;
using static VNotch.Services.AnimationPrimitives;
using static VNotch.Services.Win32Interop;
using EnumWindowsProc = VNotch.Services.Win32Interop.EnumWindowsProc;
using MONITORINFO = VNotch.Services.Win32Interop.MONITORINFO;
using POINT = VNotch.Services.Win32Interop.POINT;
using RECT = VNotch.Services.Win32Interop.RECT;
using WINDOWPLACEMENT = VNotch.Services.Win32Interop.WINDOWPLACEMENT;
using WinEventDelegate = VNotch.Services.Win32Interop.WinEventDelegate;
namespace VNotch;

public partial class MainWindow : Window
{
    #region Fields

    private readonly SettingsService _settingsService;
    private readonly NotchManager _notchManager;
    private readonly MediaDetectionService _mediaService;
    private readonly IUpdateService _updateService;
    private readonly ShellViewModel _viewModel;
    private readonly DispatcherTimer _updateTimer;
    private readonly DispatcherTimer _updateCheckTimer;
    private readonly ZOrderManager _zOrderManager;
    private readonly DispatcherTimer _hoverCollapseTimer;
    private readonly DispatcherTimer _hoverThumbnailDelayTimer;
    private readonly DispatcherTimer _compactThumbnailHoverLeaveTimer;

    private bool _isDraggingVolume = false;
    private NotchSettings _settings;

    private readonly NotchShellState _shellState = new();

    private bool _isNotchVisible
    {
        get => _shellState.IsNotchVisible;
        set => _shellState.IsNotchVisible = value;
    }
    private bool _isHiddenByFullscreen
    {
        get => _shellState.IsHiddenByFullscreen;
        set => _shellState.IsHiddenByFullscreen = value;
    }

    private IntPtr _hwnd
    {
        get => _shellState.Hwnd;
        set => _shellState.Hwnd = value;
    }

    private readonly OverlayWindowController _overlayWindow;
    private readonly ClipboardListenerController _clipboardListener;
    private readonly ISpotlightController _spotlightController;
    private bool _spotlightMorphSessionActive;
    private bool _spotlightMorphOwnsNotchVisibility;
    private bool _spotlightReturnHandoffActive;
    private double _spotlightRestoreNotchOpacity = 1;
    private double _spotlightRestoreShadowOpacity = 1;
    private bool _spotlightRestoreNotchHitTesting = true;
    private DispatcherTimer? _fullscreenRecheckTimer;
    private bool _isTrayMenuOpen
    {
        get => _shellState.IsTrayMenuOpen;
        set => _shellState.IsTrayMenuOpen = value;
    }

    private readonly BatteryModule _batteryModule;
    private readonly CalendarModule _calendarModule;
    private readonly BluetoothModule _bluetoothModule;
    private readonly PrivacyIndicatorModule _privacyModule;
    private readonly WeatherModule _weatherModule;
    private readonly SystemMonitorModule _systemMonitorModule;
    private readonly IModuleLifecycleManager _moduleHost;

    private readonly NotchStateManager _notchState = new();

    private readonly NotchAnimationController _animController;
    private readonly MusicWidgetController _musicController;
    private readonly MediaDisplayController _mediaDisplayController;
    private readonly FullscreenAutoHideController _fullscreenController;
    private readonly BluetoothNotificationController _bluetoothController;
    private DragDropController _dragDropController = null!;
    private readonly TimerManager _timerManager;

    private bool _isAnimating
    {
        get => _notchState.IsAnimating;
        set
        {
            _notchState.IsAnimating = value;
            // Drive the liquid-glass refresh rate off notch motion: full rate while
            UpdateGlassMotionState();
        }
    }

    private bool _isExpandedBacking;
    private bool _isExpanded
    {
        get => _notchState.IsExpanded;
        set
        {
            _isExpandedBacking = value;
            UpdateGlassMotionState();
        }
    }

    private bool _isStartupLayoutReady = false;
    private bool _pendingStartupClickToggle = false;
    private double _collapsedWidth
    {
        get => _shellState.CollapsedWidth;
        set => _shellState.CollapsedWidth = value;
    }
    private double _collapsedHeight
    {
        get => _shellState.CollapsedHeight;
        set => _shellState.CollapsedHeight = value;
    }
    private bool _modeTransitionPending;
    private double _expandedWidth
    {
        get => _shellState.ExpandedWidth;
        set => _shellState.ExpandedWidth = value;
    }
    private double _expandedHeight
    {
        get => _shellState.ExpandedHeight;
        set => _shellState.ExpandedHeight = value;
    }
    private double _cornerRadiusCollapsed
    {
        get => _shellState.CornerRadiusCollapsed;
        set => _shellState.CornerRadiusCollapsed = value;
    }
    private double _cornerRadiusExpanded
    {
        get => _shellState.CornerRadiusExpanded;
        set => _shellState.CornerRadiusExpanded = value;
    }
    private int _fixedX
    {
        get => _shellState.FixedX;
        set => _shellState.FixedX = value;
    }
    private int _fixedY
    {
        get => _shellState.FixedY;
        set => _shellState.FixedY = value;
    }
    private int _windowWidth { get => _shellState.WindowWidth; set => _shellState.WindowWidth = value; }
    private int _windowHeight { get => _shellState.WindowHeight; set => _shellState.WindowHeight = value; }

    private MediaInfo? _currentMediaInfo;
    private bool _isMusicCompactMode = false;
    private bool _isCompactThumbnailHovered = false;
    private const double CompactThumbnailHoverExitMargin = 22.0;
    private DateTime _lastMediaActionTime = DateTime.MinValue;

    private bool _isDraggingNotchDebug = false;
    private bool _hasActuallyDragged = false;
    private Point _debugDragStartMouse;
    private int _debugDragStartFixedX;
    private int _debugDragStartFixedY;

    private readonly VNotch.Controllers.CompactPillArbiter _compactPillArbiter = new();

    private int _viewTransitionGeneration;
    private int NextViewTransitionGeneration() => ++_viewTransitionGeneration;

    private static readonly TimeSpan ProgressRenderInterval = TimeSpan.FromMilliseconds(16);
    private static readonly TimeSpan LyricsUpdateInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan VolumeSyncInterval = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _lyricsTimer;
    private readonly DispatcherTimer _volumeSyncTimer;

    private static readonly SolidColorBrush _brushCharging = CreateFrozenBrush(48, 209, 88);
    private static readonly SolidColorBrush _brushLowBattery = CreateFrozenBrush(255, 59, 48);
    private static readonly SolidColorBrush _brushWhite = CreateFrozenBrush(255, 255, 255);
    private static readonly SolidColorBrush _brushBlack = CreateFrozenBrush(0, 0, 0);
    private static readonly SolidColorBrush _brushTransparent = CreateFrozenBrush(0, 0, 0, 0);
    private static readonly SolidColorBrush _brushGray = CreateFrozenBrush(102, 102, 102);

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private bool _isUpdateAvailable = false;
    private UpdateInfo? _availableUpdate = null;
    private bool _isUpdateInstalling = false;
    private int _updateCheckGeneration;
    private int _updateVisibilityGeneration;
    private DispatcherTimer? _updatePulseTimer;
    private DateTime _updatePulseStartedAtUtc = DateTime.MinValue;
    private bool _isUpdateTooltipOpen = false;
    private DateTime _suspendTopmostUntilUtc
    {
        get => _shellState.SuspendTopmostUntilUtc;
        set => _shellState.SuspendTopmostUntilUtc = value;
    }

    #endregion

    public MainWindow(
        ISettingsService settingsService,
        IMediaDetectionService mediaService,
        ShellViewModel viewModel,
        IUpdateService updateService,
        IModuleLifecycleManager moduleHost,
        BatteryModule batteryModule,
        CalendarModule calendarModule,
        BluetoothModule bluetoothModule,
        PrivacyIndicatorModule privacyIndicatorModule,
        WeatherModule weatherModule,
        SystemMonitorModule systemMonitorModule,
        ISpotlightController spotlightController)
    {
        InitializeComponent();
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        _viewModel = viewModel;
        DataContext = _viewModel;
        _viewModel.IsExpandedCheck = () => _isExpanded || _isMusicExpanded;
        _viewModel.MediaInfoUpdated += ViewModel_MediaInfoUpdated;
        AnimationPrimitives.ApplyFpsToTree(this);
        _settingsService = (SettingsService)settingsService;
        _settings = _settingsService.Load();
        _notchManager = new NotchManager(this, _settings);
        _mediaService = (MediaDetectionService)mediaService;
        _updateService = updateService;
        _spotlightController = spotlightController;

        _animController = new NotchAnimationController(_notchState);
        _musicController = new MusicWidgetController(_notchState);
        _mediaDisplayController = new MediaDisplayController();
        InitializeCameraController();
        _fullscreenController = new FullscreenAutoHideController(() => _hwnd, _settings);
        _fullscreenController.HideStateChanged += FullscreenController_HideStateChanged;
        _fullscreenController.RecheckNeeded += ScheduleFullscreenRecheck;
        _timerManager = new TimerManager(Dispatcher);
        _moduleHost = moduleHost;
        _batteryModule = batteryModule;
        _batteryModule.BatteryUpdated += BatteryModule_BatteryUpdated;
        AnimationConfig.ReduceMotionChanged += OnReduceMotionChanged;

        _calendarModule = calendarModule;

        _bluetoothModule = bluetoothModule;
        _bluetoothModule.DeviceConnected += BluetoothModule_DeviceConnected;
        _bluetoothModule.DeviceDisconnected += BluetoothModule_DeviceDisconnected;

        _privacyModule = privacyIndicatorModule;
        _privacyModule.StateChanged += PrivacyModule_StateChanged;
        _notchState.StateChanged += NotchState_PrivacyVisibilityChanged;

        _weatherModule = weatherModule;
        _weatherModule.WeatherUpdated += WeatherModule_WeatherUpdated;
        if (!_settings.EnableWeather)
        {
            ShowWeatherStatus(isEnabled: false);
        }

        _systemMonitorModule = systemMonitorModule;
        _systemMonitorModule.StatsUpdated += SystemMonitorModule_StatsUpdated;

        _collapsedWidth = GetCollapsedWidth();
        _collapsedHeight = GetCollapsedHeight();
        _expandedHeight = _settings.EnableDynamicIslandMode ? 154 : 147;
        _cornerRadiusCollapsed = GetCollapsedCornerRadius();

        _updateTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

        _updateCheckTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(90)
        };
        _updateCheckTimer.Tick += UpdateCheckTimer_Tick;

        _zOrderManager = new ZOrderManager(
            getHwnd: () => _hwnd,
            isEffectivelyVisible: () => IsEffectivelyNotchVisible,
            isSuspended: () => _isTrayMenuOpen || _isUpdateTooltipOpen || DateTime.UtcNow < _suspendTopmostUntilUtc,
            stayBehindWindows: () => ShouldStayOnDesktopLayer,
            onForegroundChanged: OnForegroundWindowChanged);
        _clipboardListener = new ClipboardListenerController(
            () => _hwnd,
            () =>
            {
                if (IsEffectivelyNotchVisible)
                    Dispatcher.BeginInvoke(new Action(PlayClipboardPeek));
            });
        _overlayWindow = new OverlayWindowController(
            this,
            _shellState,
            () => IsEffectivelyNotchVisible,
            () => ShouldStayOnDesktopLayer,
            () => _zOrderManager.EnsureTopmost(force: true),
            HandleAppDeactivated,
            () =>
            {
                InvalidateGlassDpiScale();
                PositionAtTop();
            },
            _clipboardListener.NotifyClipboardUpdated);
        _overlayWindow.IsPointInteractive = IsPointInteractive;

        _progressTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = ProgressRenderInterval
        };
        _progressTimer.Tick += ProgressTimer_Tick;

        _lyricsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = LyricsUpdateInterval
        };
        _lyricsTimer.Tick += LyricsTimer_Tick;

        _volumeSyncTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = VolumeSyncInterval
        };
        _volumeSyncTimer.Tick += VolumeSyncTimer_Tick;
        InputMonitorService.MouseActionTriggered += GlobalMouseHook_MouseLeftButtonDown;

        _hoverCollapseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelay)
        };
        _hoverCollapseTimer.Tick += (s, e) =>
        {
            _hoverCollapseTimer.Stop();
            if (_isDebugViewLocked || _spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;
            if (_isExpanded && !NotchWrapper.IsMouseOver)
            {
                if (DateTime.UtcNow < _suppressHoverCollapseUntilUtc)
                {
                    RuntimeLog.Log("COLLAPSE-BLOCKED",
                        $"HoverCollapseTimer suppressed at fire time: remaining={(_suppressHoverCollapseUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms");
                    return;
                }

                if (_hwnd != IntPtr.Zero && IsCursorInsideWindow())
                {
                    RuntimeLog.Log("COLLAPSE-BLOCKED",
                        $"HoverCollapseTimer: WPF says IsMouseOver=False but cursor is inside window rect â€” suppressing");
                    return;
                }

                RuntimeLog.Log("COLLAPSE-TRIGGER",
                    $"HoverCollapseTimer fired: isExpanded={_isExpanded} isMouseOver={NotchWrapper.IsMouseOver} " +
                    $"isSecondary={_isSecondaryView} isMusicExpanded={_isMusicExpanded}");
                CollapseNotch();
            }
        };

        _hoverThumbnailDelayTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_settings.HoverExpandDelay)
        };
        _hoverThumbnailDelayTimer.Tick += (s, e) =>
        {
            _hoverThumbnailDelayTimer.Stop();
            if (_settings.EnableHoverExpand && !_isExpanded && !_isAnimating)
            {
                ExpandNotch();
            }
            else if (!_settings.EnableHoverExpand && IsCursorInsideCompactThumbnailExitZone())
            {
                SetCompactThumbnailHover(true);
            }
        };

        _compactThumbnailHoverLeaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _compactThumbnailHoverLeaveTimer.Tick += (s, e) =>
        {
            if (!_isExpanded && !_isAnimating && _isMusicCompactMode && IsCursorInsideCompactThumbnailExitZone())
            {
                return;
            }

            _compactThumbnailHoverLeaveTimer.Stop();
            SetCompactThumbnailHover(false);
        };

        _notchManager.HoverService.HoverEnter += HoverService_HoverEnter;
        _notchManager.HoverService.HoverLeave += HoverService_HoverLeave;
        _notchManager.HoverService.MousePositionChanged += HoverService_MousePositionChangedForDesktopReveal;

        Loaded += MainWindow_Loaded;
        Deactivated += MainWindow_Deactivated;

        InitializeFileShelfController();
        _dragDropController = new DragDropController(_fileShelf);
        InitializeDragDropController();
        InitializeGestureController();
        _bluetoothController = new BluetoothNotificationController();
        InitializeBluetoothNotificationController();
        InitializeIdleAutoHide();
    }

    #region Window Lifecycle

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _overlayWindow.Initialize();
        _spotlightController.Initialize(this, _settings);
        _clipboardListener.Start();

        try
        {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico");
            if (System.IO.File.Exists(iconPath))
            {
                var icon = new System.Drawing.Icon(iconPath);
                TrayIcon.Icon = icon;

                this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(iconPath));
            }
            else
            {
                TrayIcon.Icon = IconGenerator.CreateNotchIcon(16);
            }
        }
        catch
        {
            TrayIcon.Icon = IconGenerator.CreateNotchIcon(16);
        }

        ApplySettings();
        ConfigureOverlayWindow();
        PositionAtTop();
        StartZOrderWatchdog();
        UpdateFullscreenAutoHideState(_overlayWindow.GetForegroundWindowHandle(), force: true);

        _updateTimer.Start();
        _updateCheckTimer.Start();
        CheckForUpdatesAsync().SafeFireAndForget("UPDATE-CHECK");

        if (IsEffectivelyNotchVisible)
        {
            PlayAppearAnimation();
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateLayout();
            UpdateNotchClip();
            UpdateMediaBackgroundFootprint();

            _isStartupLayoutReady = true;
            _pendingStartupClickToggle = false;

            if (!_isGreetingActive)
            {
                _viewModel.Initialize();
                StartCoreModules();
            }
        }), DispatcherPriority.ContextIdle);
    }

    private void HandleAppDeactivated()
    {
        if (!_isDebugViewLocked && ShouldCollapseOnDeactivation(
                _spotlightMorphSessionActive,
                _spotlightMorphOwnsNotchVisibility,
                _isSecondaryView,
                _isTimerView,
                _isExpanded,
                isMusicExpanded: false,
                isAnimating: _isAnimating))
        {
            RuntimeLog.Log("COLLAPSE-TRIGGER",
                $"WM_ACTIVATEAPP(deactivate) -> CollapseNotch: isSecondary={_isSecondaryView} isTimer={_isTimerView}");
            CollapseNotch();
        }
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        if (!_isDebugViewLocked && ShouldCollapseOnDeactivation(
                _spotlightMorphSessionActive,
                _spotlightMorphOwnsNotchVisibility,
                _isSecondaryView,
                _isTimerView,
                _isExpanded,
                _isMusicExpanded,
                _isAnimating))
        {
            RuntimeLog.Log("COLLAPSE-TRIGGER",
                $"Deactivated event -> CollapseAll: isExpanded={_isExpanded} isMusicExpanded={_isMusicExpanded} isSecondary={_isSecondaryView} isTimer={_isTimerView}");
            CollapseAll();
        }
    }

    internal static bool ShouldCollapseOnDeactivation(
        bool spotlightMorphSessionActive,
        bool spotlightMorphOwnsNotchVisibility,
        bool isSecondaryView,
        bool isTimerView,
        bool isExpanded,
        bool isMusicExpanded,
        bool isAnimating) =>
        !spotlightMorphSessionActive
        && !spotlightMorphOwnsNotchVisibility
        && (isSecondaryView || isTimerView)
        && (isExpanded || isMusicExpanded)
        && !isAnimating;

    private void CollapseAll()
    {
        if (_isDebugViewLocked) return;
        if (_isMusicExpanded) CollapseMusicWidget();
        if (_isExpanded) CollapseNotch();
    }

    private bool _cleanedUp;

    private void PerformCleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;

        _notchManager.HoverService.HoverEnter -= HoverService_HoverEnter;
        _notchManager.HoverService.HoverLeave -= HoverService_HoverLeave;
        _notchManager.HoverService.MousePositionChanged -= HoverService_MousePositionChangedForDesktopReveal;
        _viewModel.MediaInfoUpdated -= ViewModel_MediaInfoUpdated;
        _viewModel.Dispose();
        _batteryModule.BatteryUpdated -= BatteryModule_BatteryUpdated;
        AnimationConfig.ReduceMotionChanged -= OnReduceMotionChanged;
        DisposeCalendarPresenter();
        _privacyModule.StateChanged -= PrivacyModule_StateChanged;
        _notchState.StateChanged -= NotchState_PrivacyVisibilityChanged;
        _weatherModule.WeatherUpdated -= WeatherModule_WeatherUpdated;
        _systemMonitorModule.StatsUpdated -= SystemMonitorModule_StatsUpdated;

        InputMonitorService.MouseActionTriggered -= GlobalMouseHook_MouseLeftButtonDown;

        _clipboardListener.Dispose();
        _spotlightController.Dispose();
        _overlayWindow.Dispose();
        StopZOrderWatchdog();
        StopTitleGradientShift();
        _progressTimer?.Stop();
        _lyricsTimer?.Stop();
        _volumeSyncTimer?.Stop();
        _mediaService?.Dispose();
        _lyricsService?.Dispose();
        DisposeSpotifyCanvasLifecycle();
        _spotifyCanvasService?.Dispose();
        _notchManager?.Dispose();
        _zOrderManager?.Dispose();
        TrayIcon?.Dispose();
        _updateTimer?.Stop();
        _updateCheckTimer?.Stop();
        _hoverCollapseTimer?.Stop();
        _hoverThumbnailDelayTimer?.Stop();
        _compactThumbnailHoverLeaveTimer?.Stop();
        _desktopDemotionDelayTimer?.Stop();
        DetachDesktopTransparentFrameHandler();
        DisposeIdleAutoHide();
        _fullscreenController?.Dispose();
        _moduleHost?.Dispose();
        _camera?.Dispose();
        _timerManager?.Dispose();
        DisposeGestureController();
        DisposeAllShelfWatchers();
    }

    // The ViewModel is the single production subscriber to media state.  This window
    private void ViewModel_MediaInfoUpdated(object? sender, MediaInfo info) => OnMediaChanged(sender, info);

    protected override void OnClosed(EventArgs e)
    {
        PerformCleanup();
        base.OnClosed(e);
    }

    #endregion

    #region Window Configuration

    private void ConfigureOverlayWindow()
    {
        _overlayWindow.ConfigureOverlay();
    }

    private void EnableKeyboardInput()
    {
        _overlayWindow.SetKeyboardInput(true);
    }

    private void DisableKeyboardInput()
    {
        _overlayWindow.SetKeyboardInput(false);
    }

    private bool IsEffectivelyNotchVisible => _shellState.IsEffectivelyNotchVisible;

    private bool _fullscreenSlideVisible = true;
    private bool _isFullscreenSlideAnimating = false;

    private void ApplyNotchVisibilityState()
    {
        if (NotchContainer == null) return;

        bool shouldBeVisible = IsEffectivelyNotchVisible;

        if (shouldBeVisible == _fullscreenSlideVisible && !_isFullscreenSlideAnimating) return;

        _fullscreenSlideVisible = shouldBeVisible;
        _isFullscreenSlideAnimating = true;

        double slideDistance = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight + 10 : _collapsedHeight + 10;

        double currentY = NotchContainerTranslate.Y;
        double currentOpacity = NotchContainer.Opacity;
        bool wasCollapsed = NotchContainer.Visibility != Visibility.Visible;
        NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        NotchContainer.BeginAnimation(UIElement.OpacityProperty, null);
        NotchContainerTranslate.Y = currentY;
        NotchContainer.Opacity = currentOpacity;

        if (shouldBeVisible)
        {
            NotchContainer.Visibility = Visibility.Visible;
            if (wasCollapsed || currentY > 0 || currentY < -slideDistance)
            {
                NotchContainerTranslate.Y = -slideDistance;
                NotchContainer.Opacity = 0;
            }
            AnimateNotchFade(toOpacity: 1, durationMs: 300, easeOut: true);
            AnimateNotchSlide(toY: 0, durationMs: 350, easeOut: true, onComplete: () =>
            {
                _isFullscreenSlideAnimating = false;
            });
        }
        else
        {
            AnimateNotchFade(toOpacity: 0, durationMs: 220, easeOut: false);
            AnimateNotchSlide(toY: -slideDistance, durationMs: 250, easeOut: false, onComplete: () =>
            {
                _isFullscreenSlideAnimating = false;
                if (!_fullscreenSlideVisible)
                {
                    NotchContainer.Visibility = Visibility.Collapsed;
                    NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                    NotchContainerTranslate.Y = 0;
                    NotchContainer.BeginAnimation(UIElement.OpacityProperty, null);
                    NotchContainer.Opacity = 0;
                }
            });
        }
    }

    private void AnimateNotchSlide(double toY, int durationMs, bool easeOut, Action? onComplete)
    {
        var easing = easeOut
            ? (IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseIn };

        var anim = new DoubleAnimation(toY, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        if (onComplete != null)
        {
            anim.Completed += (s, e) => onComplete();
        }

        NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void AnimateNotchFade(double toOpacity, int durationMs, bool easeOut)
    {
        var easing = new CubicEase
        {
            EasingMode = easeOut ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        var anim = new DoubleAnimation(toOpacity, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = easing
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        NotchContainer.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void UpdateFullscreenAutoHideState(IntPtr foregroundHwnd = default, bool force = false)
    {
        _fullscreenController.Evaluate(foregroundHwnd, force);
    }

    private void FullscreenController_HideStateChanged(bool shouldHide)
    {
        if (_isDebugViewLocked) return;
        _isHiddenByFullscreen = shouldHide;

        if (_isHiddenByFullscreen)
        {
            if ((_isExpanded || _isMusicExpanded) && !_isAnimating)
            {
                RuntimeLog.Log("COLLAPSE-TRIGGER",
                    $"FullscreenAutoHide -> CollapseAll: shouldHide={shouldHide} isExpanded={_isExpanded} isMusicExpanded={_isMusicExpanded}");
                CollapseAll();
            }

            _hoverCollapseTimer.Stop();
            _hoverThumbnailDelayTimer.Stop();
        }

        ApplyNotchVisibilityState();

        RefreshAmbientAnimations();

        if (!_isHiddenByFullscreen && _isNotchVisible)
        {
            TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
            EnsureTopmost(force: true);
        }
    }

    private void ScheduleFullscreenRecheck()
    {
        if (_fullscreenRecheckTimer == null)
        {
            _fullscreenRecheckTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _fullscreenRecheckTimer.Tick += (s, e) =>
            {
                _fullscreenRecheckTimer!.Stop();
                UpdateFullscreenAutoHideState(force: true);
            };
        }
        _fullscreenRecheckTimer.Stop();
        _fullscreenRecheckTimer.Start();
    }

    private void EnsureTopmost()
    {
        _zOrderManager.EnsureTopmost(force: false);
    }

    private void EnsureTopmost(bool force)
    {
        _zOrderManager.EnsureTopmost(force: force);
    }

    private void StartZOrderWatchdog()
    {
        _zOrderManager.Start();
    }

    private void StopZOrderWatchdog()
    {
        _zOrderManager.Stop();
    }

    private void OnForegroundWindowChanged(IntPtr hwnd)
    {
        if (_hwnd == IntPtr.Zero) return;

        UpdateFullscreenAutoHideState(hwnd, force: true);
        ScheduleFullscreenRecheck();

        if (!IsEffectivelyNotchVisible) return;

        var processName = TryGetProcessName(hwnd);
        if (IsMyDockFinder(processName))
        {
            _zOrderManager.TriggerBurst(TimeSpan.FromSeconds(4), aggressive: true);
        }
        else
        {
            _zOrderManager.TriggerBurst(TimeSpan.FromMilliseconds(1200));
        }

        _zOrderManager.EnsureTopmost(force: true);
    }

    private void TriggerZOrderBurst(TimeSpan duration, bool aggressive = false)
    {
        _zOrderManager.TriggerBurst(duration, aggressive);
    }

    private void AssertTopmostNow()
    {
        _zOrderManager.AssertNow();
    }

    private static string TryGetProcessName(IntPtr hwnd) => FullscreenDetector.TryGetProcessName(hwnd);

    private static bool IsMyDockFinder(string processName)
    {
        return processName.Contains("mydockfinder", StringComparison.OrdinalIgnoreCase);
    }

    private void PositionAtTop()
    {
        double notchSurfaceWidth = Math.Max(Math.Max(_expandedWidth, _clockViewWidth), _audioViewWidth);
        _overlayWindow.PositionAtTop(notchSurfaceWidth, _expandedHeight);

        // Initialize the global hover/top-edge bounds at startup as well as after
        _notchManager.UpdatePosition();

        if (_hwnd != IntPtr.Zero)
        {
            UpdateFullscreenAutoHideState(_overlayWindow.GetForegroundWindowHandle(), force: true);
        }
    }

    private void ResetPosition()
    {
        PositionAtTop();
    }
    public (double Left, double Top, double Width, double Height, double CornerRadius) GetNotchScreenRect()
    {
        return _overlayWindow.GetNotchScreenRect(_collapsedWidth, _collapsedHeight, _cornerRadiusCollapsed);
    }

    /// <summary>
    /// Screen rect and per-edge corner radii of the currently visible notch,
    /// for the Spotlight morph. This must use the live arranged size: capturing
    /// an expanded media view and squeezing it into the collapsed envelope
    /// distorts the opening handoff before Spotlight begins to expand.
    /// </summary>
    internal (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius)
        GetSpotlightMorphRect()
    {
        double width = NotchBorder.ActualWidth;
        double height = NotchBorder.ActualHeight;
        if (!double.IsFinite(width) || width <= 0) width = _collapsedWidth;
        if (!double.IsFinite(height) || height <= 0) height = _collapsedHeight;

        CornerRadius corners = NotchBorder.CornerRadius;
        bool islandMode = _settings.EnableDynamicIslandMode;
        double topRadius = islandMode ? Math.Max(0, Math.Min(corners.TopLeft, corners.TopRight)) : 0;
        double bottomRadius = Math.Max(0, Math.Min(corners.BottomLeft, corners.BottomRight));
        var rect = _overlayWindow.GetNotchScreenRect(width, height, bottomRadius);

        double left = rect.Left;
        double top = rect.Top + (islandMode ? DynamicIslandTopMargin : 0);

        RuntimeLog.Debug("SPOTLIGHT-MORPH", () =>
            $"Source rect: {width:F1}x{height:F1}, expanded={_isExpanded}, " +
            $"musicExpanded={_isMusicExpanded}, topRadius={topRadius:F1}, bottomRadius={bottomRadius:F1}");

        return (left,
            top,
            rect.Width,
            rect.Height,
            topRadius,
            bottomRadius);
    }

    internal void SetSpotlightMorphSessionActive(bool active)
    {
        // A completed return fade used to leave its slightly longer scale
        if (active) CompleteSpotlightReturnScaleHandoff();
        _spotlightMorphSessionActive = active;
        if (!active) return;

        // This guard is acquired before Spotlight calls Show(). Showing or
        _hoverCollapseTimer.Stop();
        _hoverThumbnailDelayTimer.Stop();
    }

    internal void SetSpotlightMorphActive(bool active)
    {
        if (active)
        {
            CompleteSpotlightReturnScaleHandoff();
            // Spotlight now owns the visible notch snapshot. Do not let hover
            _hoverCollapseTimer.Stop();
            _hoverThumbnailDelayTimer.Stop();

            // Repeated ownership requests are common when a running exit morph
            if (!_spotlightMorphOwnsNotchVisibility)
            {
                // Preserve the animation targets, not a transient presentation
                _spotlightRestoreNotchOpacity =
                    (double)NotchWrapper.GetAnimationBaseValue(OpacityProperty);
                _spotlightRestoreShadowOpacity =
                    (double)NotchShadowWrapper.GetAnimationBaseValue(OpacityProperty);
                _spotlightRestoreNotchHitTesting = NotchWrapper.IsHitTestVisible;
            }
            _spotlightMorphOwnsNotchVisibility = true;

            // Change the base underneath any return-handoff animation before
            NotchWrapper.Opacity = 0;
            NotchShadowWrapper.Opacity = 0;
            NotchWrapper.BeginAnimation(OpacityProperty, null);
            NotchShadowWrapper.BeginAnimation(OpacityProperty, null);
            NotchWrapper.IsHitTestVisible = false;

            if (NotchContainerTranslate != null)
            {
                NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                NotchContainerTranslate.Y = 0;
            }
            return;
        }

        if (!_spotlightMorphOwnsNotchVisibility)
        {
            CompleteSpotlightReturnScaleHandoff();
            return;
        }

        // Likewise, make the restored values the bases before clearing a
        NotchWrapper.Opacity = _spotlightRestoreNotchOpacity;
        NotchShadowWrapper.Opacity = _spotlightRestoreShadowOpacity;
        NotchWrapper.BeginAnimation(OpacityProperty, null);
        NotchShadowWrapper.BeginAnimation(OpacityProperty, null);
        NotchWrapper.IsHitTestVisible = _spotlightRestoreNotchHitTesting;

        if (NotchContainerTranslate != null)
        {
            NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NotchContainerTranslate.Y = 0;
        }

        _spotlightMorphOwnsNotchVisibility = false;
        CompleteSpotlightReturnScaleHandoff();
    }

    internal void BeginSpotlightReturnHandoff(TimeSpan duration)
    {
        if (!_spotlightMorphOwnsNotchVisibility)
        {
            SetSpotlightMorphActive(false);
            return;
        }

        if (NotchContainerTranslate != null)
        {
            NotchContainerTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            NotchContainerTranslate.Y = 0;
        }

        // Ownership remains with Spotlight until its HWND is actually hidden.
        NotchWrapper.BeginAnimation(OpacityProperty, null);
        NotchShadowWrapper.BeginAnimation(OpacityProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        NotchWrapper.Opacity = _spotlightRestoreNotchOpacity;
        NotchShadowWrapper.Opacity = _spotlightRestoreShadowOpacity;
        NotchWrapper.IsHitTestVisible = _spotlightRestoreNotchHitTesting;
        NotchScale.ScaleX = NotchScale.ScaleY = 1;
        NotchShadowScale.ScaleX = NotchShadowScale.ScaleY = 1;
        _spotlightReturnHandoffActive = true;

        // Spotlight does not own the active notch view. In particular, do not

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var notchFade = new DoubleAnimation(0, _spotlightRestoreNotchOpacity, duration)
        {
            EasingFunction = ease
        };
        var shadowFade = new DoubleAnimation(0, _spotlightRestoreShadowOpacity, duration)
        {
            EasingFunction = ease
        };
        Timeline.SetDesiredFrameRate(notchFade, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(shadowFade, AnimationConfig.TargetFps);

        NotchWrapper.BeginAnimation(OpacityProperty, notchFade);
        NotchShadowWrapper.BeginAnimation(OpacityProperty, shadowFade);
    }

    private void CompleteSpotlightReturnScaleHandoff()
    {
        if (!_spotlightReturnHandoffActive) return;

        // Set the bases first so removing an interrupted or completed clock
        NotchScale.ScaleX = 1;
        NotchScale.ScaleY = 1;
        NotchShadowScale.ScaleX = 1;
        NotchShadowScale.ScaleY = 1;
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _spotlightReturnHandoffActive = false;
    }

    internal void PlayNotchReturnBounce()
    {
        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        NotchScale.ScaleX = 1;
        NotchScale.ScaleY = 1;
        NotchShadowScale.ScaleX = 1;
        NotchShadowScale.ScaleY = 1;

        if (CompactThumbnailBorder != null) CompactThumbnailBorder.Opacity = 1;
        if (ThumbnailBorder != null) ThumbnailBorder.Opacity = 1;
        _isAnimating = false;

        var bounce = new DoubleAnimationUsingKeyFrames();
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1.12,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)),
            _easeSoftSpring));
        Timeline.SetDesiredFrameRate(bounce, VNotch.Services.AnimationConfig.TargetFps);

        NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounce);
        NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounce);
    }

    private void OpenAppSettings()
    {
        var settingsWindow = new SettingsWindow(
            _settings,
            _settingsService,
            _bluetoothModule,
            _spotlightController.IsHotkeyRegistered)
        {
            Owner = this
        };

        settingsWindow.SettingsChanged += (s, newSettings) =>
        {
            bool sizeChanged = newSettings.Width != _settings.Width
                            || newSettings.DynamicIslandWidth != _settings.DynamicIslandWidth
                            || newSettings.DynamicIslandHeight != _settings.DynamicIslandHeight
                            || newSettings.Height != _settings.Height
                            || newSettings.CornerRadius != _settings.CornerRadius;
            bool languageChanged = newSettings.Language != _settings.Language;
            bool spotifyCanvasSettingsChanged =
                newSettings.EnableSpotifyCanvas != _settings.EnableSpotifyCanvas ||
                !string.Equals(newSettings.SpotifySpDc, _settings.SpotifySpDc, StringComparison.Ordinal);
            _modeTransitionPending = newSettings.EnableDynamicIslandMode != _settings.EnableDynamicIslandMode;
            bool oldIgnoreAuto = _settings.IgnoreYouTubeAutoSubtitles;
            string oldSubtitlePriority = _settings.SubtitlePriority ?? "";
            _settings = newSettings.Clone();
            _notchManager.UpdateSettings(_settings);
            _fileShelf.UpdateSettings(_settings);
            _fullscreenController.UpdateSettings(_settings);
            _spotlightController.ApplySettings(_settings);
            settingsWindow.SetSpotlightHotkeyStatus(_spotlightController.IsHotkeyRegistered);
            ApplySettings(sizeChanged);
            _weatherModule.OnSettingsChanged(_settings);

            _youtubeSubtitleService.SetMode(_settings.SubtitlePriority);
            _youtubeSubtitleService.SetIgnoreAutoGenerated(_settings.IgnoreYouTubeAutoSubtitles);

            if (spotifyCanvasSettingsChanged)
            {
                _spotifyCanvasService?.ClearCache();
                if (_settings.EnableSpotifyCanvas)
                    RefreshSpotifyCanvasForCurrentTrack();
                else
                    ResetSpotifyCanvas();
            }

            bool priorityChanged = !string.Equals(oldSubtitlePriority, _settings.SubtitlePriority, StringComparison.Ordinal);
            bool ignoreAutoChanged = oldIgnoreAuto != _settings.IgnoreYouTubeAutoSubtitles;
            if (priorityChanged || ignoreAutoChanged)
            {
                VNotch.Services.RuntimeLog.Log("SUBTITLE-PRIORITY",
                    $"Settings changed: priority='{oldSubtitlePriority}' -> '{_settings.SubtitlePriority}', ignoreAuto={_settings.IgnoreYouTubeAutoSubtitles} " +
                    $"hasMedia={_currentMediaInfo != null} videoId='{_lastKnownYouTubeVideoId}'");

                if (!string.IsNullOrEmpty(_lastKnownYouTubeVideoId))
                {
                    _youtubeSubtitleService.Reset();
                    _lyricsTrackKey = "";
                    _syncedTextSource = SyncedTextSource.None;
                    var refetchInfo = _currentMediaInfo ?? new VNotch.Models.MediaInfo();
                    if (string.IsNullOrEmpty(refetchInfo.YouTubeVideoId))
                        refetchInfo.YouTubeVideoId = _lastKnownYouTubeVideoId;
                    FetchSubtitlesForTrack(refetchInfo, force: true).SafeFireAndForget("SUBTITLES");
                }
            }

            if (languageChanged)
            {
                Loc.SetLanguage(_settings.Language);
                RefreshNotchLocalization();
            }
        };

        settingsWindow.AnimatedClosing += (s, e) =>
        {
        };

        settingsWindow.Closed += (s, e) =>
        {
            PlayNotchReturnBounce();
            MemoryOptimizerService.Instance.ScheduleTrim(600);
        };

        settingsWindow.ShowDialog();
    }

    private void ApplySettings(bool animatePulse = false)
    {
        VNotch.Services.AnimationConfig.Configure(_settings.AnimationFps);
        AnimationPrimitives.ApplyFpsToTree(this);
        VNotch.Controls.MusicVisualizer.ConfigureAudioDevice(_settings.VisualizerAudioDeviceId);
        ApplyPerformanceSettings();

        ResetDesktopEdgePromotionIfDisabled();

        // Reconfigure immediately so changing this option does not require an app
        if (_hwnd != IntPtr.Zero)
            ConfigureOverlayWindow();

        ApplyExpandedWidgetMode();

        _hoverCollapseTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverCollapseDelay);
        _hoverThumbnailDelayTimer.Interval = TimeSpan.FromMilliseconds(_settings.HoverExpandDelay);

        ApplyIdleAutoHideSettings();

        ToggleDebugMode(_settings.EnableDebugMode);

        _youtubeSubtitleService.SetMode(_settings.SubtitlePriority);
        _youtubeSubtitleService.SetIgnoreAutoGenerated(_settings.IgnoreYouTubeAutoSubtitles);

        if (_settings.DisableMouseLeaveAutoClose)
        {
            _hoverCollapseTimer.Stop();
        }

        if (_hwnd != IntPtr.Zero)
        {
            UpdateFullscreenAutoHideState(_overlayWindow.GetForegroundWindowHandle(), force: true);
        }

        _collapsedWidth = GetCollapsedWidth();
        _collapsedHeight = GetCollapsedHeight();
        _expandedHeight = _settings.EnableDynamicIslandMode ? 154 : 147;
        _cornerRadiusCollapsed = GetCollapsedCornerRadius();
        _cachedThumbnailExpandTarget = null;

        bool willModeTransition = _modeTransitionPending
                                  && !_isExpanded && !_isAnimating
                                  && NotchBorder.ActualWidth > 0 && NotchBorder.ActualHeight > 0;
        _modeTransitionPending = false;

        ApplyDynamicIslandLayout(animateTransition: willModeTransition);

        if (!_isExpanded && !_isAnimating)
        {
            double currentRadius = NotchBorder.CornerRadius.BottomLeft;
            if (currentRadius > 0)
                CurrentCornerRadius = currentRadius;
            else
                CurrentCornerRadius = _cornerRadiusCollapsed;

            NotchBorder.BeginAnimation(WidthProperty, null);
            NotchBorder.BeginAnimation(HeightProperty, null);
            this.BeginAnimation(CurrentCornerRadiusProperty, null);

            bool isFirstLayout = NotchBorder.ActualWidth <= 0 || NotchBorder.ActualHeight <= 0;
            int fps = VNotch.Services.AnimationConfig.TargetFps;

            if (isFirstLayout)
            {
                NotchBorder.Width = _collapsedWidth;
                NotchBorder.Height = _collapsedHeight;

                var cr = MakeNotchCornerRadius(_cornerRadiusCollapsed);
                NotchBorder.CornerRadius = cr;
                InnerClipBorder.CornerRadius = cr;
                NotchBackground.CornerRadius = cr;
                NotchBorderShadow.CornerRadius = cr;
                MediaBackground.CornerRadius = cr;
                MediaBackground2.CornerRadius = cr;
                SyncGlassCornerRadius(cr);
                CurrentCornerRadius = _cornerRadiusCollapsed;
            }
            else if (willModeTransition)
            {
                AnimateModeTransition(fps);
            }
            else if (_isModeTransitioning)
            {
            }
            else if (animatePulse && (Math.Abs(NotchBorder.ActualWidth - _collapsedWidth) > 0.5 ||
                                      Math.Abs(NotchBorder.ActualHeight - _collapsedHeight) > 0.5 ||
                                      Math.Abs(_cornerRadiusCollapsed - currentRadius) > 0.5))
            {
                var dur = _dur200;
                var easing = _easeExpOut6;

                var widthAnim = new DoubleAnimation(NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : _collapsedWidth, _collapsedWidth, dur)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                };
                Timeline.SetDesiredFrameRate(widthAnim, fps);
                widthAnim.Completed += (s, e) =>
                {
                    NotchBorder.BeginAnimation(WidthProperty, null);
                    NotchBorder.Width = _collapsedWidth;
                };

                var heightAnim = new DoubleAnimation(NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : _collapsedHeight, _collapsedHeight, dur)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop
                };
                Timeline.SetDesiredFrameRate(heightAnim, fps);
                heightAnim.Completed += (s, e) =>
                {
                    NotchBorder.BeginAnimation(HeightProperty, null);
                    NotchBorder.Height = _collapsedHeight;
                };

                NotchBorder.BeginAnimation(WidthProperty, widthAnim);
                NotchBorder.BeginAnimation(HeightProperty, heightAnim);

                double targetRadius = _cornerRadiusCollapsed;
                if (Math.Abs(targetRadius - currentRadius) > 0.5)
                {
                    CurrentCornerRadius = currentRadius;
                    var radiusAnim = new DoubleAnimation(currentRadius, targetRadius, dur)
                    {
                        EasingFunction = easing
                    };
                    Timeline.SetDesiredFrameRate(radiusAnim, fps);
                    this.BeginAnimation(CurrentCornerRadiusProperty, radiusAnim);
                }
                else
                {
                    var cr = MakeNotchCornerRadius(_cornerRadiusCollapsed);
                    NotchBorder.CornerRadius = cr;
                    InnerClipBorder.CornerRadius = cr;
                    NotchBackground.CornerRadius = cr;
                    NotchBorderShadow.CornerRadius = cr;
                    MediaBackground.CornerRadius = cr;
                    MediaBackground2.CornerRadius = cr;
                    SyncGlassCornerRadius(cr);
                    CurrentCornerRadius = _cornerRadiusCollapsed;
                }
            }
            else
            {
                NotchBorder.Width = _collapsedWidth;
                NotchBorder.Height = _collapsedHeight;
                var cr = MakeNotchCornerRadius(_cornerRadiusCollapsed);
                NotchBorder.CornerRadius = cr;
                InnerClipBorder.CornerRadius = cr;
                NotchBackground.CornerRadius = cr;
                NotchBorderShadow.CornerRadius = cr;
                MediaBackground.CornerRadius = cr;
                MediaBackground2.CornerRadius = cr;
                SyncGlassCornerRadius(cr);
                CurrentCornerRadius = _cornerRadiusCollapsed;
            }

            UpdateNotchClip();
            UpdateMediaBackgroundFootprint();

            if (animatePulse)
            {
                NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                var pulse = new DoubleAnimationUsingKeyFrames();
                pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.04,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(80)),
                    new QuadraticEase { EasingMode = EasingMode.EaseOut }));
                pulse.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,
                    KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
                    _easeSoftSpring));
                Timeline.SetDesiredFrameRate(pulse, fps);

                NotchScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                NotchScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                NotchShadowScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
            }
        }

        this.Opacity = _settings.Opacity;

        double lyricsImageOpacity = Math.Max(0.2, 1.0 - _settings.MediaBlurDarkOverlay);
        if (LyricsBlurImage != null)
        {
            LyricsBlurImage.BeginAnimation(UIElement.OpacityProperty, null);
            LyricsBlurImage.Opacity = lyricsImageOpacity;
        }
        ApplySpotifyCanvasBrightness();

        if (!_settings.EnableSpotifyLyrics)
        {
            if (_isLyricsActive && _syncedTextSource == SyncedTextSource.SpotifyLyrics)
            {
                _lyricsTrackKey = "";
                HideLyricsWidget();
            }
        }
        else if (_currentMediaInfo != null
                 && _currentMediaInfo.Platform == MediaPlatform.Spotify
                 && !string.IsNullOrEmpty(_currentMediaInfo.CurrentTrack)
                 && !_isLyricsActive)
        {
            _lyricsTrackKey = "";
            FetchLyricsForTrack(_currentMediaInfo).SafeFireAndForget("LYRICS");
        }

        if (!_settings.EnableYouTubeSubtitles)
        {
            if (_isLyricsActive && _syncedTextSource == SyncedTextSource.YouTubeSubtitles)
            {
                _lyricsTrackKey = "";
                HideLyricsWidget();
            }
        }
        else if (_currentMediaInfo != null
                 && _currentMediaInfo.Platform == MediaPlatform.YouTube
                 && !string.IsNullOrEmpty(_currentMediaInfo.YouTubeVideoId)
                 && !_isLyricsActive)
        {
            _lyricsTrackKey = "";
            FetchSubtitlesForTrack(_currentMediaInfo).SafeFireAndForget("SUBTITLES");
        }

        if (_currentMediaInfo != null && !_isExpanded)
        {
            UpdateMediaBackground(_currentMediaInfo, forceRefresh: true);
        }

        _mediaService.ArtworkService.ConfigureSmartCrop(_settings.EnableSmartCrop);

        if (!_settings.ShowMediaArtBackground)
            HideMediaBackground();

        ApplyLiquidGlassSkin();

        if (_hwnd != IntPtr.Zero)
        {
            _overlayWindow.ReassertBounds();
        }
    }

    private void ApplyPerformanceSettings()
    {
        if (_settings.EnableBlurEffects)
        {
            return;
        }

        DisableBlurEffectsImmediate();
    }

    private void DisableBlurEffectsImmediate()
    {
        _blurDissolveDebounce?.Stop();
        _pendingBlurResult = null;
        _lastBlurThumbnailRef = null;
        _blurTaskVersion++;
        _blurCrossfadeVersion++;

        ResetBlur(ExpandedContentBlur);
        ResetBlur(CollapsedContentBlur);
        ResetBlur(MusicCompactContentBlur);
        ResetBlur(CompactThumbnailOutBlur);
        ResetBlur(CompactThumbnailNextBlur);
        ResetBlur(ThumbnailOutBlur);
        ResetBlur(ThumbnailNextBlur);
        ResetBlur(CalendarGreetingContextBlur);
        ResetBlur(CameraPreviewBlur);
        ResetBlur(_mediaControlsHoverBlur);

        MediaControls.Effect = null;
        TimerContent.Effect = null;
        ExpandedContent.Effect = null;
        SecondaryContent.Effect = null;

        MediaBackground.BeginAnimation(OpacityProperty, null);
        MediaBackground2.BeginAnimation(OpacityProperty, null);
        MediaBackground.Opacity = 0;
        MediaBackground2.Opacity = 0;
        MediaBackground.Visibility = Visibility.Collapsed;
        MediaBackground2.Visibility = Visibility.Collapsed;

        MediaBackgroundImage.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImage2.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImageBack2.BeginAnimation(OpacityProperty, null);
        MediaBackgroundImage.Source = null;
        MediaBackgroundImage2.Source = null;
        MediaBackgroundImageBack.Source = null;
        MediaBackgroundImageBack2.Source = null;
        MediaBackgroundImage.Opacity = 0;
        MediaBackgroundImage2.Opacity = 0;
        MediaBackgroundImageBack.Opacity = 0;
        MediaBackgroundImageBack2.Opacity = 0;

        BrightnessDimOverlay.BeginAnimation(OpacityProperty, null);
        BrightnessDimOverlay2.BeginAnimation(OpacityProperty, null);
        BrightnessDimOverlay.Opacity = 0;
        BrightnessDimOverlay2.Opacity = 0;

        if (LyricsBlurBackground != null)
        {
            LyricsBlurBackground.BeginAnimation(OpacityProperty, null);
            LyricsBlurBackground.Opacity = 0;
            LyricsBlurBackground.Visibility = Visibility.Collapsed;
        }
    }

    private static void ResetBlur(BlurEffect? effect)
    {
        if (effect == null) return;

        effect.BeginAnimation(BlurEffect.RadiusProperty, null);
        effect.Radius = 0;
    }

    private void RefreshNotchLocalization()
    {
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.GetCulture().IetfLanguageTag);
        UpdateShelfCapacityIndicator();
        EventText.Text = Loc.Get("greeting.enjoyDay");
        ChargingStatusText.Text = Loc.Get("battery.charging");
        ClipboardCopiedText.Text = Loc.Get("clipboard.copied");
        CameraErrorText.Text = Loc.Get("notch.camera.error");
        CameraRetryText.Text = Loc.Get("notch.camera.retry");
        AudioLoadingText.Text = Loc.Get("audio.loading");
        ShelfUnlockButtonText.Text = Loc.Get("shelf.unlockButton");
        ShelfUnlockDismissText.Text = Loc.Get("shelf.unlockDismiss");
        ShelfUnlockSettingsHint.Text = Loc.Get("shelf.unlockSettingsHint");
        MenuToggleText.Text = Loc.Get(_isNotchVisible ? "tray.hide" : "tray.show");
        MenuResetText.Text = Loc.Get("tray.reset");
        MenuSettingsText.Text = Loc.Get("tray.settings");
        MenuRestartText.Text = Loc.Get("tray.restart");
        MenuExitText.Text = Loc.Get("tray.exit");
        _calendarPresenter?.RefreshLocale();
        _weatherModule.RefreshLocalization();
        RefreshAudioLocalization();
        RefreshClockViewLocale();
        WordClockWidget.RefreshLocalization();
    }

    private const double DynamicIslandTopMargin = 8.0;

    private void ApplyDynamicIslandLayout(bool animateTransition = false)
    {
        bool islandMode = _settings.EnableDynamicIslandMode;

        if (!animateTransition)
        {
            if (NotchContainer != null)
            {
                var current = NotchContainer.Margin;
                double targetTop = islandMode ? DynamicIslandTopMargin : 0;
                if (Math.Abs(current.Top - targetTop) > 0.01)
                {
                    NotchContainer.Margin = new Thickness(current.Left, targetTop, current.Right, current.Bottom);
                }
            }

            var earVisibility = islandMode ? Visibility.Collapsed : Visibility.Visible;
            if (LeftEar != null) LeftEar.Visibility = earVisibility;
            if (RightEar != null) RightEar.Visibility = earVisibility;
            if (LeftShadowEar != null) LeftShadowEar.Visibility = earVisibility;
            if (RightShadowEar != null) RightShadowEar.Visibility = earVisibility;
        }

        ApplyDynamicIslandContentAlignment(islandMode);
    }

    private void ApplyDynamicIslandContentAlignment(bool islandMode)
    {
        if (StatusBarTranslate != null)
        {
            StatusBarTranslate.Y = -ExpandedContentRestY;
        }

        if (MusicCompactContent != null)
        {
            double islandTop = Math.Max(0, (GetCollapsedHeight() - 22) / 2.0);
            MusicCompactContent.VerticalAlignment = VerticalAlignment.Top;
            MusicCompactContent.Margin = islandMode ? new Thickness(12, islandTop, 12, 0) : new Thickness(8, 4, 8, 4);
        }

        if (CompactHoverInfo != null)
        {
            CompactHoverInfo.Margin = islandMode ? new Thickness(0, 8, 0, 8) : new Thickness(-6, 14, -6, 8);
        }

        if (CompactThumbnailBorder != null)
        {
            CompactThumbnailBorder.RenderTransformOrigin = islandMode ? new Point(0.5, 0.5) : new Point(0, 0);
        }

        if (MusicViz != null)
        {
            MusicViz.VerticalAlignment = islandMode ? VerticalAlignment.Center : VerticalAlignment.Top;
            MusicViz.Margin = islandMode ? new Thickness(0, 0, -4, 0) : new Thickness(0, 2.5, -4, 0);
        }

        if (_privacyIndicatorsVisible) UpdatePrivacyDotPosition();

        if (ClipboardCheckIcon != null)
        {
            ClipboardCheckIcon.Margin = new Thickness(2, 4, -2, 0);
        }

        if (ClipboardCopiedText != null)
        {
            ClipboardCopiedText.Margin = new Thickness(0, 4, 0, 0);
        }

        if (BluetoothNotification?.Children.Count > 0 && BluetoothNotification.Children[0] is FrameworkElement bluetoothContent)
        {
            bluetoothContent.Margin = islandMode ? new Thickness(0) : new Thickness(0, 0, 0, 6);
        }

        if (BluetoothDisconnectNotification?.Children.Count > 0 && BluetoothDisconnectNotification.Children[0] is FrameworkElement bluetoothDisconnectContent)
        {
            bluetoothDisconnectContent.Margin = islandMode ? new Thickness(0) : new Thickness(0, 0, 0, 6);
        }

        if (ChargingNotification?.Children.Count > 0 && ChargingNotification.Children[0] is FrameworkElement chargingContent)
        {
            chargingContent.Margin = islandMode ? new Thickness(0) : new Thickness(0, 0, 0, 4);
        }
    }

    #endregion

    #region Click & Hover Handling

    private void NotchBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isDebugDraggable)
        {
            var hit = e.OriginalSource as DependencyObject;
            if (!IsInteractiveControl(hit))
            {
                _isDraggingNotchDebug = true;
                _hasActuallyDragged = false;
                Win32Interop.GetCursorPos(out var pt);
                _debugDragStartMouse = new Point(pt.X, pt.Y);
                _debugDragStartFixedX = _fixedX;
                _debugDragStartFixedY = _fixedY;
                NotchWrapper.CaptureMouse();
                e.Handled = true;
                return;
            }
        }

        if (_isAnimating)
        {
            e.Handled = true;
            return;
        }

        if (_isDraggingVolumeIndicator)
        {
            e.Handled = true;
            return;
        }

        if (!_isExpanded)
        {
            SuppressCompactVolumeWheelForClick();
        }

        if (_isVolumeIndicatorActive)
        {
            DismissVolumeIndicatorImmediate();
        }

        if (TryBeginGesture(e))
        {
            e.Handled = true;
            return;
        }

        if (!_isStartupLayoutReady)
        {
            if (!_pendingStartupClickToggle)
            {
                _pendingStartupClickToggle = true;
                int clickCount = e.ClickCount;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateLayout();
                    UpdateNotchClip();
                    _isStartupLayoutReady = true;
                    _pendingStartupClickToggle = false;

                    if (_isAnimating) return;
                    ToggleNotchFromClick(clickCount);
                }), DispatcherPriority.Render);
            }

            e.Handled = true;
            return;
        }

        ToggleNotchFromClick(e.ClickCount);
        e.Handled = true;
    }

    private static bool IsInteractiveControl(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is System.Windows.Controls.Primitives.ButtonBase ||
                element is System.Windows.Controls.Primitives.RangeBase ||
                element is System.Windows.Controls.Primitives.TextBoxBase ||
                element is System.Windows.Controls.TextBox ||
                element is System.Windows.Controls.PasswordBox ||
                element is System.Windows.Controls.ComboBox ||
                element is System.Windows.Controls.ListBox ||
                element is System.Windows.Controls.MenuItem)
            {
                return true;
            }
            if (element is Window) break;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    private void ToggleNotchFromClick(int clickCount)
    {
        if (_isDebugViewLocked) return;
        if (_isExpanded)
        {
            if (_isSecondaryView)
            {
                if (clickCount == 2) CollapseNotch();
            }
            else
            {
                CollapseNotch();
            }
        }
        else
        {
            ExpandNotch();
        }
    }

    private void NotchWrapper_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverCollapseTimer.Stop();
        AnimateNotchHover(true);
        QueueHoverExpand();
    }

    private void NotchWrapper_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverThumbnailDelayTimer.Stop();
        if (_spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;

        if (_settings.DisableMouseLeaveAutoClose)
        {
            if (!_isExpanded)
            {
                AnimateNotchHover(false);
            }
            return;
        }

        if (_isExpanded && !_isAnimating && !_isSecondaryView)
        {
            if (DateTime.UtcNow < _suppressHoverCollapseUntilUtc)
            {
                RuntimeLog.Log("COLLAPSE-BLOCKED",
                    $"MouseLeave suppressed during grace period: remaining={(_suppressHoverCollapseUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms");
                return;
            }

            if (IsCursorInsideWindow())
            {
                RuntimeLog.Log("COLLAPSE-BLOCKED",
                    $"MouseLeave: WPF fired leave but cursor still inside window rect â€” ignoring");
                return;
            }

            RuntimeLog.Log("COLLAPSE-HOVER",
                $"MouseLeave -> starting hoverCollapseTimer: interval={_hoverCollapseTimer.Interval.TotalMilliseconds}ms " +
                $"isExpanded={_isExpanded} isMusicExpanded={_isMusicExpanded}");
            _hoverCollapseTimer.Start();
        }
        else if (!_isExpanded)
        {
            AnimateNotchHover(false);
        }
    }

    private void QueueHoverExpand()
    {
        if (_settings.EnableHoverExpand && !_isExpanded && !_isAnimating)
        {
            _hoverThumbnailDelayTimer.Stop();
            _hoverThumbnailDelayTimer.Start();
        }
    }

    private void HoverService_HoverEnter(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(QueueHoverExpand));
    }

    private void HoverService_HoverLeave(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _hoverThumbnailDelayTimer.Stop();
            if (_spotlightMorphSessionActive || _spotlightMorphOwnsNotchVisibility) return;
            if (_settings.DisableMouseLeaveAutoClose) return;
            if (_isExpanded && !_isAnimating && !_isSecondaryView)
            {
                if (DateTime.UtcNow < _suppressHoverCollapseUntilUtc)
                {
                    RuntimeLog.Log("COLLAPSE-BLOCKED",
                        $"HoverService_HoverLeave suppressed during grace period: remaining={(_suppressHoverCollapseUntilUtc - DateTime.UtcNow).TotalMilliseconds:F0}ms");
                    return;
                }
                RuntimeLog.Log("COLLAPSE-HOVER",
                    $"HoverService_HoverLeave -> starting hoverCollapseTimer: interval={_hoverCollapseTimer.Interval.TotalMilliseconds}ms");
                _hoverCollapseTimer.Start();
            }
        }));
    }

    private void CompactThumbnailBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isExpanded && !_isAnimating && _isMusicCompactMode)
        {
            _compactThumbnailHoverLeaveTimer.Stop();
            _hoverThumbnailDelayTimer.Stop();
            _hoverThumbnailDelayTimer.Start();
        }
    }

    private void CompactThumbnailBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverThumbnailDelayTimer.Stop();
        if (!_isExpanded && !_isAnimating && _isMusicCompactMode)
        {
            _compactThumbnailHoverLeaveTimer.Stop();
            _compactThumbnailHoverLeaveTimer.Start();
        }
    }

    private void SetCompactThumbnailHover(bool isHovered)
    {
        if (_isCompactThumbnailHovered == isHovered) return;

        _isCompactThumbnailHovered = isHovered;
        if (!isHovered)
        {
            _compactThumbnailHoverLeaveTimer.Stop();
        }
        AnimateThumbnailHover(isHovered);
    }

    private bool IsCursorInsideCompactThumbnailExitZone()
    {
        if (CompactThumbnailBorder == null || NotchBorder == null) return false;

        Point cursor = Mouse.GetPosition(NotchBorder);

        Rect thumbnailBounds;
        try
        {
            thumbnailBounds = CompactThumbnailBorder
                .TransformToVisual(NotchBorder)
                .TransformBounds(new Rect(0, 0, CompactThumbnailBorder.ActualWidth, CompactThumbnailBorder.ActualHeight));
        }
        catch
        {
            return false;
        }

        Rect exitBounds = thumbnailBounds;

        if (_settings.EnableDynamicIslandMode && _isCompactThumbnailHovered)
        {
            double notchWidth = NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width;
            double notchHeight = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
            if (double.IsFinite(notchWidth) && double.IsFinite(notchHeight) && notchWidth > 0 && notchHeight > 0)
            {
                exitBounds.Union(new Rect(0, 0, notchWidth, notchHeight));
            }
        }
        else if (CompactHoverInfo?.Visibility == Visibility.Visible)
        {
            try
            {
                var hoverBounds = CompactHoverInfo
                    .TransformToVisual(NotchBorder)
                    .TransformBounds(new Rect(0, 0, CompactHoverInfo.ActualWidth, CompactHoverInfo.ActualHeight));
                exitBounds.Union(hoverBounds);
            }
            catch
            {
            }
        }

        exitBounds.Inflate(CompactThumbnailHoverExitMargin, CompactThumbnailHoverExitMargin);
        return exitBounds.Contains(cursor);
    }

    #endregion

    #region Battery & Calendar

    private void Battery_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:batterysaver") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RuntimeLog.Error("BATTERY-CLICK", ex.ToString());
        }
    }

    private void BatterySection_MouseEnter(object sender, MouseEventArgs e)
    {
        AnimateBatteryHover(true);
    }

    private void BatterySection_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateBatteryHover(false);
    }

    private void AnimateBatteryHover(bool isHovered)
    {
        if (BatterySectionScale == null) return;

        double targetScale = isHovered ? 1.1 : 1.0;
        var dur = isHovered ? TimeSpan.FromMilliseconds(220) : TimeSpan.FromMilliseconds(280);
        var ease = isHovered
            ? (IEasingFunction)new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
            : new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };

        var anim = new DoubleAnimation(targetScale, dur)
        {
            EasingFunction = ease
        };
        Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);

        BatterySectionScale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
        BatterySectionScale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenAppSettings();
    }

    private void Settings_Click(object sender, MouseButtonEventArgs e)
    {
        OpenAppSettings();
        e.Handled = true;
    }

    private void SettingsButton_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    private void SettingsButton_MouseEnter(object sender, MouseEventArgs e)
    {
        SettingsButton.CacheMode ??= new System.Windows.Media.BitmapCache(1.5);
        AnimateSettingsHover(true);
    }

    private void SettingsButton_MouseLeave(object sender, MouseEventArgs e)
    {
        AnimateSettingsHover(false);
    }

    private void AnimateSettingsHover(bool isEnter)
    {
        int fps = VNotch.Services.AnimationConfig.TargetFps;
        var dur = TimeSpan.FromMilliseconds(isEnter ? 360 : 300);
        var scaleDur = new Duration(dur);
        var rotateDur = new Duration(TimeSpan.FromMilliseconds(isEnter ? 520 : 360));
        var easeOut = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = isEnter ? 0.32 : 0.18 };
        var rotateEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };

        var scaleAnim = new DoubleAnimation
        {
            To = isEnter ? 1.18 : 1.0,
            Duration = scaleDur,
            EasingFunction = easeOut
        };
        Timeline.SetDesiredFrameRate(scaleAnim, fps);

        var rotateAnim = new DoubleAnimation
        {
            To = isEnter ? 132 : 45,
            Duration = rotateDur,
            EasingFunction = rotateEase
        };
        Timeline.SetDesiredFrameRate(rotateAnim, fps);

        var opacityAnim = new DoubleAnimation
        {
            To = isEnter ? 0.95 : 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(opacityAnim, fps);

        var bg = SettingsButton.Background as SolidColorBrush;
        if (bg == null || bg.IsFrozen)
        {
            bg = new SolidColorBrush(Colors.Transparent);
            SettingsButton.Background = bg;
        }

        var bgAnim = new ColorAnimation
        {
            To = isEnter ? Color.FromArgb(34, 255, 255, 255) : Colors.Transparent,
            Duration = new Duration(TimeSpan.FromMilliseconds(isEnter ? 160 : 240)),
            EasingFunction = _easeQuadOut
        };
        Timeline.SetDesiredFrameRate(bgAnim, fps);

        SettingsScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsRotate.BeginAnimation(RotateTransform.AngleProperty, rotateAnim, HandoffBehavior.SnapshotAndReplace);
        SettingsButton.BeginAnimation(OpacityProperty, opacityAnim, HandoffBehavior.SnapshotAndReplace);
        bg.BeginAnimation(SolidColorBrush.ColorProperty, bgAnim, HandoffBehavior.SnapshotAndReplace);
    }

    #endregion

    #region Timer Handlers

    private void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        if (_isMusicExpanded) SyncVolumeFromActiveSession();
        EnsureTopmost();
    }

    private void BatteryModule_BatteryUpdated(object? sender, BatteryInfo battery)
    {
        _viewModel.UpdateBatteryInfo(battery);
        HandleBatteryUpdate(battery);
    }

    #endregion

    #region Notch Clip

    private void NotchContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateNotchClip();
        UpdateMediaBackgroundFootprint();
    }

    private void UpdateMediaBackgroundFootprint()
    {
        if (MediaBackground == null || MediaBackground2 == null || NotchBorder == null) return;

        if (_isTimerView) return;

        double notchLength = NotchContent?.ActualWidth > 0
            ? NotchContent.ActualWidth
            : (NotchBorder.ActualWidth > 0 ? NotchBorder.ActualWidth : NotchBorder.Width);
        if (notchLength <= 0) return;

        double primaryLength = notchLength * (2.0 / 3.0);
        double secondaryLength = primaryLength * 0.84;

        double notchBreadth = NotchBorder.ActualHeight > 0 ? NotchBorder.ActualHeight : NotchBorder.Height;
        double primaryBreadth = Math.Clamp(notchBreadth * 1.24, 140.0, 220.0);
        double secondaryBreadth = primaryBreadth * 0.82;

        var dur = TimeSpan.FromMilliseconds(350);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        AnimateDouble(MediaBackground, WidthProperty, primaryLength, dur, ease);
        AnimateDouble(MediaBackground, HeightProperty, primaryBreadth, dur, ease);
        AnimateDouble(MediaBackground2, WidthProperty, secondaryLength, dur, ease);
        AnimateDouble(MediaBackground2, HeightProperty, secondaryBreadth, dur, ease);
    }

    private static void AnimateDouble(UIElement target, DependencyProperty prop, double to, TimeSpan duration, IEasingFunction ease)
    {
        var current = (double)((FrameworkElement)target).GetValue(prop);
        if (double.IsNaN(current) || Math.Abs(current - to) < 0.5)
        {
            ((FrameworkElement)target).BeginAnimation(prop, null);
            ((FrameworkElement)target).SetValue(prop, to);
            return;
        }

        var anim = new DoubleAnimation
        {
            To = to,
            Duration = duration,
            EasingFunction = ease
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
        ((FrameworkElement)target).BeginAnimation(prop, anim);
    }

    private void UpdateNotchClip()
    {
        if (NotchContent == null || NotchBorder == null) return;

        double w = NotchContent.ActualWidth;
        double h = NotchContent.ActualHeight;

        if (w <= 0 || h <= 0) return;

        var geometry = BuildNotchClipGeometry(w, h);
        if (geometry == null) return;

        NotchContent.Clip = geometry;
        HoverGlow.Clip = geometry;

        // Keep the complete glass material stack clipped to its current silhouette.
        UpdateGlassClip();
    }

    private double _lastGlassClipWidth = double.NaN;
    private double _lastGlassClipHeight = double.NaN;
    private double _lastGlassClipTopRadius = double.NaN;
    private double _lastGlassClipBottomRadius = double.NaN;

    /// <summary>
    /// Clips the complete glass material stack after all child effects have run.
    /// Keeping the clip on an effect-free ancestor prevents the backdrop and
    /// Fresnel blurs from spreading Touch Light beyond the rounded silhouette.
    /// </summary>
    private void UpdateGlassClip()
    {
        if (GlassMaterialClipHost == null) return;

        double w = GlassMaterialClipHost.ActualWidth;
        double h = GlassMaterialClipHost.ActualHeight;
        if (w <= 0 || h <= 0) return;

        double maxR = Math.Min(w, h) / 2.0;
        double rTop = Math.Clamp(NotchBorder.CornerRadius.TopLeft, 0, maxR);
        double rBottom = Math.Clamp(NotchBorder.CornerRadius.BottomRight, 0, maxR);

        if (Math.Abs(w - _lastGlassClipWidth) < 0.01 &&
            Math.Abs(h - _lastGlassClipHeight) < 0.01 &&
            Math.Abs(rTop - _lastGlassClipTopRadius) < 0.01 &&
            Math.Abs(rBottom - _lastGlassClipBottomRadius) < 0.01)
        {
            return;
        }

        var geometry = BuildRoundedNotchClipGeometry(w, h, rTop, rBottom);
        if (geometry != null)
        {
            GlassMaterialClipHost.Clip = geometry;
            _lastGlassClipWidth = w;
            _lastGlassClipHeight = h;
            _lastGlassClipTopRadius = rTop;
            _lastGlassClipBottomRadius = rBottom;
        }
    }

    private void GlassMaterialClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateGlassClip();
    }

    private StreamGeometry? BuildNotchClipGeometry(double w, double h)
    {
        if (w <= 0 || h <= 0) return null;

        // Clamp the arc radii to the element's bounds. Unlike a Border's built-in
        double maxR = Math.Min(w, h) / 2.0;
        double rBottom = Math.Max(0, Math.Min(NotchBorder.CornerRadius.BottomRight, maxR));
        double rTop = Math.Max(0, Math.Min(NotchBorder.CornerRadius.TopLeft, maxR));

        return BuildRoundedNotchClipGeometry(w, h, rTop, rBottom);
    }

    internal static StreamGeometry? BuildRoundedNotchClipGeometry(
        double w,
        double h,
        double rTop,
        double rBottom)
    {
        if (w <= 0 || h <= 0) return null;

        double maxR = Math.Min(w, h) / 2.0;
        rTop = Math.Clamp(rTop, 0, maxR);
        rBottom = Math.Clamp(rBottom, 0, maxR);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (rTop > 0)
            {
                ctx.BeginFigure(new Point(rTop, 0), true, true);
                ctx.LineTo(new Point(w - rTop, 0), true, false);
                ctx.ArcTo(new Point(w, rTop), new Size(rTop, rTop), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(0, 0), true, true);
                ctx.LineTo(new Point(w, 0), true, false);
            }

            ctx.LineTo(new Point(w, h - rBottom), true, false);
            if (rBottom > 0)
                ctx.ArcTo(new Point(w - rBottom, h), new Size(rBottom, rBottom), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(w, h), true, false);

            ctx.LineTo(new Point(rBottom, h), true, false);
            if (rBottom > 0)
                ctx.ArcTo(new Point(0, h - rBottom), new Size(rBottom, rBottom), 0, false, SweepDirection.Clockwise, true, false);
            else
                ctx.LineTo(new Point(0, h), true, false);

            if (rTop > 0)
            {
                ctx.LineTo(new Point(0, rTop), true, false);
                ctx.ArcTo(new Point(rTop, 0), new Size(rTop, rTop), 0, false, SweepDirection.Clockwise, true, false);
            }
            else
            {
                ctx.LineTo(new Point(0, 0), true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    #endregion

    #region Menu Actions

    private void TrayContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        _isTrayMenuOpen = true;
        MenuToggleText.Text = Loc.Get(_isNotchVisible ? "tray.hide" : "tray.show");
    }

    private void TrayContextMenu_Closed(object sender, RoutedEventArgs e)
    {
        _isTrayMenuOpen = false;
        TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
        EnsureTopmost(force: true);
    }

    private void ToggleNotch_Click(object sender, RoutedEventArgs e)
    {
        _isNotchVisible = !_isNotchVisible;
        if (!_isNotchVisible && (_isExpanded || _isMusicExpanded) && !_isAnimating)
        {
            CollapseAll();
        }

        ApplyNotchVisibilityState();

        RefreshAmbientAnimations();

        if (_isNotchVisible)
        {
            UpdateFullscreenAutoHideState(_overlayWindow.GetForegroundWindowHandle(), force: true);
            if (IsEffectivelyNotchVisible)
            {
                TriggerZOrderBurst(TimeSpan.FromMilliseconds(900));
                EnsureTopmost(force: true);
            }
        }
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        ResetPosition();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        CleanupBeforeShutdown();
        System.Windows.Application.Current.Shutdown();
    }

    private void Restart_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

            if (!string.IsNullOrEmpty(exePath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c ping -n 2 127.0.0.1 >nul & start \"\" \"{exePath}\" --restart",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Restart failed: {ex.Message}");
        }

        CleanupBeforeShutdown();
        System.Windows.Application.Current.Shutdown();
    }

    private void CleanupBeforeShutdown()
    {
        PerformCleanup();
    }

    private bool IsPointInteractive(Point windowPt)
    {
        if (System.Windows.Input.Mouse.Captured != null) return true;
        if (windowPt.X < 0 || windowPt.Y < 0 || windowPt.X > ActualWidth || windowPt.Y > ActualHeight)
            return false;

        HitTestResult result = VisualTreeHelper.HitTest(this, windowPt);
        if (result?.VisualHit == null) return false;

        DependencyObject? current = result.VisualHit;
        while (current != null && current != this)
        {
            if (ReferenceEquals(current, NotchWrapper) ||
                ReferenceEquals(current, NavIconsPanel) ||
                ReferenceEquals(current, AudioOverlay) ||
                ReferenceEquals(current, CompactHoverInfo) ||
                (ChargingNotification != null && ReferenceEquals(current, ChargingNotification)) ||
                (BluetoothNotification != null && ReferenceEquals(current, BluetoothNotification)) ||
                (BluetoothDisconnectNotification != null && ReferenceEquals(current, BluetoothDisconnectNotification)) ||
                (UpdateNotificationButton != null && ReferenceEquals(current, UpdateNotificationButton)))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    #endregion

}
