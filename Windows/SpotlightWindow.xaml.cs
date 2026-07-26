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
    private const double ExpandedCornerRadius = 14;
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
    private bool _searchingPanelArmed;
    private DispatcherTimer? _failureTimer;
    private bool _resultsDimmed;
    private bool _escBadgeVisible = true;
    private System.Windows.Controls.Border? _selectionGlide;
    private TranslateTransform? _glideTransform;
    private bool _glideVisible;
    private bool _glideUpdateQueued;
    private bool _contentShown;
    private int _contentSizeGeneration;
    private bool _contentResizeQueued;
    private bool _entranceActive;
    private bool _pendingContentReveal;
    private SolidColorBrush? _shellBorderBrush;

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
            if (!_isClosing && SearchBox.IsEnabled && !SearchBox.IsKeyboardFocused)
            {
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
            }
        };

        // The entrance morph publishes results while the shell is still at
        // notch width; containers stretch as the shell expands, so the glide
        // must re-measure or it keeps the narrow mid-morph width.
        ResultsList.SizeChanged += (_, _) => ScheduleGlideUpdate();

        _viewModel.Results.CollectionChanged += (_, _) => RefreshStatus();
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

        int generation = ++_animationGeneration;
        ResetMorphVisuals();
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
        if (!IsVisible) return;
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
        if (!IsVisible || _isClosing) return;
        _lastDismissedQuery = SearchBox.Text;
        _lastDismissedAtUtc = DateTime.UtcNow;
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
        // The visible suggestion belongs to the previous query; hide it until
        // the new results publish.
        AutocompleteText.Visibility = Visibility.Collapsed;
        _pendingLaunchQuery = null;
        ClearLaunchFailure();
        // Until the new query publishes, the visible rows answer the old one.
        if (_viewModel.Results.Count > 0 && !string.IsNullOrEmpty(SearchBox.Text))
            SetResultsDimmed(true);
        await _viewModel.SearchAsync(SearchBox.Text);
        RefreshStatus();
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
        if (IsVisible && !_isClosing) SearchBox.Focus();
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
        Dispatcher.BeginInvoke(HideSpotlight, DispatcherPriority.Input);
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

        _launchInFlight = true;
        try
        {
            // ShellExecute can block for hundreds of ms on cold starts; keep
            // the dispatcher free so the exit morph starts instantly.
            bool launched = await Task.Run(() => _launcher.TryLaunch(selected));
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
            _launchInFlight = false;
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

        _launchInFlight = true;
        try
        {
            if (await Task.Run(() => _launcher.TryLaunchElevated(selected)))
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
            _launchInFlight = false;
        }
    }

    private async void RevealSelected()
    {
        SpotlightSearchItem? selected = _viewModel.SelectedResult;
        if (selected == null || _launchInFlight || !SpotlightLauncher.CanReveal(selected)) return;

        _launchInFlight = true;
        try
        {
            if (await Task.Run(() => _launcher.TryRevealInExplorer(selected))) HideSpotlight();
            else ShowLaunchFailure(selected);
        }
        finally
        {
            _launchInFlight = false;
        }
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
        // The target is stale (moved or uninstalled); keep Enter useful by
        // dropping the dead row and telling the user what happened.
        _viewModel.RemoveResult(item);
        FailureText.Text = Loc.Get("spotlight.launchFailed", item.Title);
        FailureBar.Visibility = Visibility.Visible;
        PlayShake();
        _failureTimer ??= CreateFailureTimer();
        _failureTimer.Stop();
        _failureTimer.Start();
    }

    private DispatcherTimer CreateFailureTimer()
    {
        var timer = new DispatcherTimer { Interval = FailureDisplayTime };
        timer.Tick += (_, _) => ClearLaunchFailure();
        return timer;
    }

    private void ClearLaunchFailure()
    {
        _failureTimer?.Stop();
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
        if (_glideUpdateQueued) return;
        _glideUpdateQueued = true;
        // Loaded priority runs after the layout pass, when containers have
        // real positions; batching also collapses select-then-reselect churn
        // from a publish into a single glide.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _glideUpdateQueued = false;
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
        bool showContent = hasResults || showStatus;

        // Children first: the reveal/resize animations below measure the
        // region's natural height, which depends on these visibilities.
        ResultsList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
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

        if (AnimationConfig.ReduceMotion || (!shown && (!IsVisible || _isClosing)))
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
            double from = ContentRegion.Visibility == Visibility.Visible
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
                TimeSpan.FromMilliseconds(180), new CubicEase { EasingMode = EasingMode.EaseIn });
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
        var resize = CreateAnimation(from, to, TimeSpan.FromMilliseconds(260),
            new CubicEase { EasingMode = EasingMode.EaseOut });
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
        _contentResizeQueued = false;
        ContentRegion.BeginAnimation(HeightProperty, null);
        ContentRegion.Height = double.NaN;
        ContentRegion.ClipToBounds = false;
        ContentRegion.Visibility = Visibility.Collapsed;
    }

    private void UpdateSearchingGrace(bool searchingEligible)
    {
        if (!searchingEligible)
        {
            _searchingGraceTimer?.Stop();
            _searchingPanelArmed = false;
            return;
        }

        if (_searchingPanelArmed || _searchingGraceTimer?.IsEnabled == true) return;
        _searchingGraceTimer ??= CreateSearchingGraceTimer();
        _searchingGraceTimer.Start();
    }

    private DispatcherTimer CreateSearchingGraceTimer()
    {
        var timer = new DispatcherTimer { Interval = SearchingPanelGrace };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _searchingPanelArmed = true;
            RefreshStatus();
        };
        return timer;
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
            ShellTopCornerRadius = ExpandedCornerRadius;
            ShellContent.Opacity = 1;
            ContentTranslate.Y = 0;
            RestoreShadow(animate: false);
            SetNotchMorphActive(true);
            return;
        }

        _entranceActive = true;
        var morphEase = CreateMorphEase();
        var contentEase = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

        double startLeft = finalLeft;
        double startTop = finalTop;
        double finalShellWidth = Math.Max(1, Shell.ActualWidth);
        double finalShellHeight = Math.Max(1, Shell.ActualHeight);
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
        ShellTopCornerRadius = ExpandedCornerRadius;
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
        BeginAnimation(ShellTopCornerRadiusProperty, cornerTop);
        ShellContent.BeginAnimation(OpacityProperty, contentFade);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);
        contentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurOut);
        // The notch has no light outline; the border only belongs to the
        // expanded panel, so it fades in as the shell departs.
        if (morphsFromNotch) AnimateShellBorder(0, 1, MorphDuration);
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
        double targetBottomRadius = Math.Max(0, notch.BottomCornerRadius);
        double targetTopRadius = Math.Max(0, notch.TopCornerRadius);
        double targetLeft = notch.Left + notch.Width / 2.0 - ActualWidth / 2.0;
        double targetTop = notch.Top;

        // Final base values keep the last frame stable until the window is hidden.
        Left = targetLeft;
        Top = targetTop;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Width = targetWidth;
        Shell.Height = targetHeight;
        ShellCornerRadius = targetBottomRadius;
        ShellTopCornerRadius = targetTopRadius;
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
        var corner = CreateAnimation(current.CornerRadius, targetBottomRadius, MorphDuration, morphEase, synchronizedMorph: true);
        var cornerTop = CreateAnimation(current.TopCornerRadius, targetTopRadius, MorphDuration, morphEase, synchronizedMorph: true);
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
        BeginAnimation(ShellTopCornerRadiusProperty, cornerTop);
        ShellContent.BeginAnimation(OpacityProperty, contentFade);
        ContentTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);
        contentBlur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurIn);
        // Shed the panel outline early so the shell arrives looking like the
        // borderless notch.
        AnimateShellBorder(1, 0, TimeSpan.FromMilliseconds(200));
    }

    private void BeginReturnHandoff(int generation)
    {
        if (generation != _animationGeneration || !IsVisible) return;

        // Keep the morph shell on the exact notch frame while the real notch takes
        // ownership underneath it. Fading only at this final frame prevents the
        // source notch from flashing before the moving window has arrived.
        ClearMorphAnimations();
        // ClearMorphAnimations restored the border's base opacity; the shell
        // must stay borderless while it fades out over the real notch.
        if (_shellBorderBrush != null) _shellBorderBrush.Opacity = 0;
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
        ShellTopCornerRadius = ExpandedCornerRadius;
        ShellContent.Opacity = 1;
        ContentTranslate.Y = 0;
        Shell.CacheMode = null;
        ShellContent.CacheMode = null;
        ShellContent.Effect = null;
        Shell.HorizontalAlignment = HorizontalAlignment.Stretch;
        // Top-aligned auto-height: the shell hugs its content inside the
        // fixed-size transparent window, so growth never resizes the HWND.
        Shell.VerticalAlignment = VerticalAlignment.Top;
        Shell.RenderTransformOrigin = new Point(0.5, 0.5);
        RestoreShadow(animate: true);

        // Results that arrived mid-morph waited for the shell to land; play
        // their reveal now that the height is free to animate.
        _entranceActive = false;
        if (_pendingContentReveal)
        {
            _pendingContentReveal = false;
            SetContentShown(true);
        }
    }

    private void CompleteHide()
    {
        ClearMorphAnimations();
        SetNotchMorphActive(false);
        Hide();
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
    }

    private void ResetMorphVisuals()
    {
        _entranceActive = false;
        _pendingContentReveal = false;
        ClearMorphAnimations();
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

    private MorphSnapshot FreezeCurrentMorphState()
    {
        var snapshot = new MorphSnapshot(
            Left, Top, Math.Max(1, Shell.ActualWidth), Math.Max(1, Shell.ActualHeight),
            ShellCornerRadius, ShellTopCornerRadius,
            ShellContent.Opacity, ContentTranslate.Y);
        ClearMorphAnimations();
        Left = snapshot.Left;
        Top = snapshot.Top;
        ShellScale.ScaleX = 1;
        ShellScale.ScaleY = 1;
        Shell.Width = snapshot.Width;
        Shell.Height = snapshot.Height;
        ShellCornerRadius = snapshot.CornerRadius;
        ShellTopCornerRadius = snapshot.TopCornerRadius;
        ShellContent.Opacity = snapshot.ContentOpacity;
        ContentTranslate.Y = snapshot.ContentTranslateY;
        return snapshot;
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
        if (ShellContent.Effect is System.Windows.Media.Effects.BlurEffect blur)
            blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, null);
    }

    private bool TryGetNotchRect(
        out (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius) rect)
    {
        if (Owner is MainWindow mainWindow)
        {
            rect = mainWindow.GetSpotlightMorphRect();
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

    /// <summary>
    /// Swaps the shared border resource for a window-local brush once, so its
    /// opacity can animate without touching other users of the resource.
    /// </summary>
    private SolidColorBrush EnsureShellBorderBrush()
    {
        if (_shellBorderBrush != null) return _shellBorderBrush;
        _shellBorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        Shell.BorderBrush = _shellBorderBrush;
        return _shellBorderBrush;
    }

    private void AnimateShellBorder(double from, double to, TimeSpan duration)
    {
        SolidColorBrush brush = EnsureShellBorderBrush();
        brush.BeginAnimation(Brush.OpacityProperty, null);
        var fade = CreateAnimation(from, to, duration,
            new QuadraticEase { EasingMode = EasingMode.EaseOut });
        brush.BeginAnimation(Brush.OpacityProperty, fade);
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
            double top = window.ShellTopCornerRadius;
            double bottom = window.ShellCornerRadius;
            window.Shell.CornerRadius = new CornerRadius(top, top, bottom, bottom);
        }
    }

    private readonly record struct MorphSnapshot(
        double Left,
        double Top,
        double Width,
        double Height,
        double CornerRadius,
        double TopCornerRadius,
        double ContentOpacity,
        double ContentTranslateY);
}
