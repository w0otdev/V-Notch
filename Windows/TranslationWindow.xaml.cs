using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.Translation;

namespace VNotch;

public partial class TranslationWindow : Window
{
    private bool _loading, _expanded;
    private int _presentationVersion;
    private bool _dismissing;
    private bool _entrancePending, _popupAnimating;
    private int _motionVersion;
    private DispatcherOperation? _presentationOperation;
    private TaskCompletionSource? _entranceCompletion;
    private TranslationResult? _deferredResult;
    private string? _deferredStatus, _deferredActivity;
    private bool _placementDirty;
    private Rect _presentationBounds, _workArea;
    private const int EntranceMilliseconds = 600;
    private const int ExitMilliseconds = 500;
    private TranslationSelection? _selection;
    private TranslationResult? _result;
    internal event Action? TranslateRequested;
    internal event Action? CancelRequested;
    internal event Action? ReplaceRequested;
    internal event Action? Dismissed;
    internal event Action<string, string>? LanguagesChanged;
    internal string SourceLanguage => SourceLanguageCombo.SelectedValue as string ?? "auto";
    internal string TargetLanguage => TargetLanguageCombo.SelectedValue as string ?? "vi";
    internal bool IsExpanded => _expanded;
    internal bool IsPresentationAnimating => _popupAnimating || _placementAnimating;

    internal TranslationWindow(string source, string target)
    {
        WindowAnimationPolicy.Attach(this);
        InitializeComponent();
        SourceLanguageCombo.DropDownOpened += LanguageMenuMotion;
        SourceLanguageCombo.DropDownClosed += LanguageMenuMotion;
        TargetLanguageCombo.DropDownOpened += LanguageMenuMotion;
        TargetLanguageCombo.DropDownClosed += LanguageMenuMotion;
        Closed += (_, _) =>
        {
            ++_presentationVersion; ++_motionVersion;
            _presentationOperation?.Abort();
            _entranceCompletion?.TrySetCanceled();
            _entranceCompletion = null;
            ClearDeferredPresentation();
            EndPopupDrag();
            CancelPopupPlacement();
            ResetPopupExpansion();
            StopResultReveal();
            SetLoading(false);
        };
        SizeChanged += (_, _) => { if (IsVisible && !_dismissing) SchedulePresentation(); };
        foreach (FrameworkElement child in TranslationPanel.Children)
            if (Grid.GetRow(child) != 3)
                child.SizeChanged += (_, _) => { if (IsVisible && !_dismissing) SchedulePresentation(); };
        IsVisibleChanged += (_, _) =>
        {
            RefreshLoadingMotion();
            if (!IsVisible) StopResultReveal();
        };
        ResultText.PreviewMouseDown += (_, _) => StopResultReveal();
        ResultText.SizeChanged += (_, _) => { if (_resultRevealBrushes != null) StopResultReveal(); };
        ResultScroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.HorizontalChange != 0) StopResultReveal();
            UpdateResultScrollFades();
        };
        ResultScroll.SizeChanged += (_, _) => UpdateResultScrollFades();
        AnimationConfig.ReduceMotionChanged += ResultScrollMotionChanged;
        Closed += (_, _) => AnimationConfig.ReduceMotionChanged -= ResultScrollMotionChanged;
        AnimationConfig.ReduceMotionChanged += ResultRevealMotionChanged;
        Closed += (_, _) => AnimationConfig.ReduceMotionChanged -= ResultRevealMotionChanged;
        AnimationConfig.ReduceMotionChanged += RefreshLoadingMotion;
        Closed += (_, _) => AnimationConfig.ReduceMotionChanged -= RefreshLoadingMotion;
        AnimationConfig.ReduceMotionChanged += PopupMotionPreferenceChanged;
        Closed += (_, _) => AnimationConfig.ReduceMotionChanged -= PopupMotionPreferenceChanged;
        ConfigureLanguages(source, target);
        RefreshLocalization();
        ResultText.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, e) => { CopyText(ResultText.SelectedText.Length > 0 ? ResultText.SelectedText : ResultText.Text); e.Handled = true; },
            (_, e) => { e.CanExecute = _result != null; e.Handled = true; }));
    }

    internal void ConfigureLanguages(string source, string target)
    {
        _loading = true;
        SourceLanguageCombo.ItemsSource = new[] { new TranslationLanguages.Language("auto", Loc.Get("translation.autoDetect")) }.Concat(TranslationLanguages.All);
        TargetLanguageCombo.ItemsSource = TranslationLanguages.All;
        SourceLanguageCombo.SelectedValue = TranslationLanguages.Normalize(source, true);
        TargetLanguageCombo.SelectedValue = TranslationLanguages.Normalize(target);
        _loading = false;
        UpdateLanguageSummary();
    }

    internal void RefreshLocalization()
    {
        Title = Loc.Get("translation.title");
        OpenButton.Content = Loc.Get("translation.translate");
        ReplaceLabel.Text = Loc.Get("translation.replace");
        CopyLabel.Text = Loc.Get("translation.copy");
        TranslateLabel.Text = Loc.Get("translation.translate");
        CancelLabel.Text = Loc.Get("translation.cancel");
        OriginalExpander.Header = Loc.Get("translation.original");
        CloseButton.ToolTip = Loc.Get("translation.close");
        System.Windows.Automation.AutomationProperties.SetName(CloseButton, Loc.Get("translation.close"));
        System.Windows.Automation.AutomationProperties.SetName(SourceLanguageCombo, Loc.Get("translation.source"));
        System.Windows.Automation.AutomationProperties.SetName(TargetLanguageCombo, Loc.Get("translation.target"));
        UpdateLanguageSummary();
    }

    internal void ShowSelection(TranslationSelection selection, bool auto)
    {
        ClearDeferredPresentation();
        StopResultReveal();
        _selection = selection;
        _result = null;
        UpdateLanguageSummary();
        OriginalText.Text = selection.Text;
        ResultText.Clear();
        CopyButton.IsEnabled = ReplaceButton.IsEnabled = false;
        _expanded = auto;
        ApplyStatus(auto ? "translation.working" : "translation.pressEnter");
        UpdatePresentation(selection.Bounds, newPresentation: true);
    }

    internal void ShowClipboard(string text)
    {
        ClearDeferredPresentation();
        StopResultReveal();
        _selection = null; _result = null;
        UpdateLanguageSummary();
        OriginalText.Text = text; ResultText.Clear();
        CopyButton.IsEnabled = ReplaceButton.IsEnabled = false;
        Expand();
    }

    private void UpdatePresentation(Rect bounds, bool newPresentation = false)
    {
        bool wasVisible = IsVisible;
        bool changingMode = wasVisible && (TranslationPanel.Visibility == Visibility.Visible) != _expanded;
        bool expandInPlace = changingMode && _expanded && !_dismissing;
        bool reveal = newPresentation || !wasVisible || _dismissing || changingMode;
        bool retarget = wasVisible && (_dismissing || _popupAnimating) && !changingMode && bounds == _presentationBounds;
        if (reveal)
        {
            _entranceCompletion?.TrySetCanceled();
            _entranceCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ++_motionVersion;
            if (expandInPlace) PreparePopupExpansion();
            else PreparePopupEntrance(retarget);
            _entrancePending = true;
            _popupAnimating = true;
        }
        bool reposition = !wasVisible || _dismissing || (!changingMode && newPresentation) || bounds != _presentationBounds;
        if (reposition) { EndPopupDrag(); CancelPopupPlacement(); _anchorPlacementPending = true; }
        ++_presentationVersion;
        _dismissing = false;
        _presentationBounds = bounds;
        PopupSurface.IsHitTestVisible = true;
        TranslationPanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        SelectionChip.Visibility = _expanded ? Visibility.Collapsed : Visibility.Visible;
        var point = new System.Drawing.Point((int)bounds.Right, (int)bounds.Bottom);
        var screen = reposition ? System.Windows.Forms.Screen.FromPoint(point) : System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(this).Handle);
        double scale = MonitorSelection.GetScale(screen);
        var work = screen.WorkingArea;
        _workArea = new Rect(work.X, work.Y, work.Width, work.Height);
        Width = Math.Min(_expanded ? 536 : 182, Math.Max(100, work.Width / scale - 16));
        MaxHeight = Math.Max(60, work.Height / scale - 16);
        TranslationPanel.MaxHeight = Math.Max(40, MaxHeight - 90);
        double estimateHeight = _expanded ? 440 : 70;
        var placement = ClampPlacement(bounds, _workArea, Width * scale, estimateHeight * scale);
        if (reposition) { Left = placement.X / scale; Top = placement.Y / scale; }
        else UpdatePopupLimits();
        if (!IsVisible) Show();
        RefreshLoadingMotion();
        SchedulePresentation();
    }

    private void SchedulePresentation()
    {
        // Coalesce selection/result/SizeToContent updates. Let WPF finish its normal
        // layout pass before starting the clock, rather than spending entrance frames
        // in synchronous UpdateLayout and native window resizes.
        _presentationOperation?.Abort();
        if (_popupAnimating && !_entrancePending)
        {
            _placementDirty = true;
            return;
        }
        int version = _presentationVersion;
        _presentationOperation = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _presentationOperation = null;
            if (version != _presentationVersion || !IsVisible || _dismissing) return;
            _placementDirty = false;
            if (_expanded && UpdateResultViewportHeight()) { SchedulePresentation(); return; }
            PlaceMeasuredPopup();
            if (!_entrancePending) return;
            _entrancePending = false;
            if (_expansionPending) AnimatePopupExpansion();
            else AnimatePopupEntrance();
        }));
    }

    private void PlaceMeasuredPopup()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32Interop.GetWindowRect(hwnd, out var rect)) return;
        if (_draggingPopup) return;
        bool initialPlacement = _anchorPlacementPending;
        var placement = initialPlacement
            ? ClampPlacement(_presentationBounds, _workArea, rect.Right - rect.Left, rect.Bottom - rect.Top)
            : new Point(Math.Clamp(rect.Left, _workArea.Left, Math.Max(_workArea.Left, _workArea.Right - (rect.Right - rect.Left))),
                Math.Clamp(rect.Top, _workArea.Top, Math.Max(_workArea.Top, _workArea.Bottom - (rect.Bottom - rect.Top))));
        _anchorPlacementPending = false;
        MovePopupTo(placement, animate: !initialPlacement);
    }

    private static DoubleAnimation Motion(double from, double to, int milliseconds, IEasingFunction? easing = null) =>
        AnimationPrimitives.MakeAnim(from, to, new Duration(TimeSpan.FromMilliseconds(milliseconds)),
            easing ?? AnimationPrimitives._easeUiOut, AnimationConfig.TargetFps);

    private static void Reveal(FrameworkElement element, TranslateTransform offset, double distance, bool retarget = false)
    {
        double opacity = retarget ? element.Opacity : 0;
        double y = retarget ? offset.Y : distance;
        element.BeginAnimation(OpacityProperty, null);
        offset.BeginAnimation(TranslateTransform.YProperty, null);
        element.Opacity = 1;
        offset.Y = 0;
        if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation) return;
        var fade = Motion(opacity, 1, 260);
        var slide = Motion(y, 0, 260);
        fade.FillBehavior = slide.FillBehavior = FillBehavior.Stop;
        element.BeginAnimation(OpacityProperty, fade);
        offset.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void PreparePopupEntrance(bool retarget)
    {
        ResetPopupExpansion();
        double opacity = retarget ? PopupSurface.Opacity : 0;
        double x = retarget ? PopupOffset.X : -24;
        PopupSurface.BeginAnimation(OpacityProperty, null);
        PopupOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PopupOffset.BeginAnimation(TranslateTransform.YProperty, null);
        // Prepare before Show(): the first frame must not flash at the final position.
        bool reduced = AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation;
        PopupSurface.Opacity = reduced ? 1 : opacity;
        PopupOffset.X = reduced ? 0 : x;
        PopupOffset.Y = 0;
    }

    private void AnimatePopupEntrance()
    {
        double opacity = PopupSurface.Opacity, x = PopupOffset.X;
        PopupSurface.Opacity = 1;
        PopupOffset.X = 0;
        if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            CompletePopupMotion(_motionVersion);
            return;
        }
        int version = _motionVersion;
        // Follow the notch's expansion curve: accelerate into the motion, then settle gently.
        var slide = Motion(x, 0, EntranceMilliseconds, AnimationPrimitives._easeAppleOut);
        var fade = Motion(opacity, 1, EntranceMilliseconds, AnimationPrimitives._easeAppleOut);
        slide.FillBehavior = fade.FillBehavior = FillBehavior.Stop;
        slide.Completed += (_, _) => CompletePopupMotion(version);
        PopupOffset.BeginAnimation(TranslateTransform.XProperty, slide);
        PopupSurface.BeginAnimation(OpacityProperty, fade);
    }

    private void CompletePopupMotion(int version)
    {
        if (version != _motionVersion) return;
        _popupAnimating = false;
        // Results and status changes can resize the HWND. Apply them after the
        // entrance has settled so its native position stays fixed while sliding.
        var result = _deferredResult;
        var status = _deferredStatus;
        var activity = _deferredActivity;
        ClearDeferredPresentation();
        if (result != null) ShowResult(result);
        else
        {
            if (status != null) ApplyStatus(status);
            if (activity != null) StatusText.Text = activity;
        }
        if (_placementDirty && IsVisible && !_dismissing) SchedulePresentation();
        RefreshLoadingMotion();
        UpdateResultScrollFades();
        _entranceCompletion?.TrySetResult();
        _entranceCompletion = null;
    }

    internal async Task WaitForEntranceAsync(CancellationToken ct)
    {
        Dispatcher.VerifyAccess();
        do
        {
            ct.ThrowIfCancellationRequested();
            if (!IsVisible || _dismissing) throw new OperationCanceledException(ct);
            if (_entranceCompletion != null) await _entranceCompletion.Task.WaitAsync(ct);
            // Give WPF's layout and render queue the final frame before starting model work.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background, ct);
        } while (_popupAnimating || _entrancePending);
        ct.ThrowIfCancellationRequested();
        if (!IsVisible || _dismissing) throw new OperationCanceledException(ct);
    }

    private bool DeferPresentation => IsVisible && _popupAnimating && !_entrancePending && !_dismissing;

    private void ClearDeferredPresentation()
    {
        _deferredResult = null;
        _deferredStatus = _deferredActivity = null;
    }

    private void ButtonMotion(object sender, MouseEventArgs e)
    {
        if (sender is not Button button || button.Template.FindName("Surface", button) is not Border surface) return;
        double opacity = !button.IsEnabled ? 0.4 : button.IsMouseOver ? 0.85 : 1;
        bool pressed = e.RoutedEvent == PreviewMouseLeftButtonDownEvent;
        var scale = surface.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        surface.RenderTransform = scale;
        surface.RenderTransformOrigin = new Point(0.5, 0.5);
        if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            surface.BeginAnimation(OpacityProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            surface.Opacity = opacity; scale.ScaleX = scale.ScaleY = 1;
            if (button.Content is System.Windows.Shapes.Path staticIcon && staticIcon.RenderTransform is RotateTransform staticRotation)
            {
                staticRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                staticRotation.Angle = 0;
            }
            return;
        }
        var fade = Motion(surface.Opacity, opacity, 100);
        fade.FillBehavior = FillBehavior.Stop;
        surface.BeginAnimation(OpacityProperty, fade);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion(scale.ScaleX, pressed ? 0.96 : 1, 100));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion(scale.ScaleY, pressed ? 0.96 : 1, 100));
        if (button.Content is System.Windows.Shapes.Path icon)
        {
            var rotation = icon.RenderTransform as RotateTransform ?? new RotateTransform();
            icon.RenderTransform = rotation;
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            rotation.BeginAnimation(RotateTransform.AngleProperty, Motion(rotation.Angle, button.IsMouseOver ? 90 : 0, 180));
        }
    }

    private void LanguageMenuMotion(object? sender, EventArgs e)
    {
        if (sender is not ComboBox combo) return;
        static System.Windows.Shapes.Path? FindChevron(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is System.Windows.Shapes.Path path) return path;
                if (FindChevron(child) is { } found) return found;
            }
            return null;
        }
        if (FindChevron(combo) is not { } chevron) return;
        var rotation = chevron.RenderTransform as RotateTransform ?? new RotateTransform();
        chevron.RenderTransform = rotation;
        chevron.RenderTransformOrigin = new Point(0.5, 0.5);
        double target = combo.IsDropDownOpen ? 180 : 0;
        if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            rotation.Angle = target;
        }
        else rotation.BeginAnimation(RotateTransform.AngleProperty, Motion(rotation.Angle, target, 180));
    }

    private void OriginalExpansionChanged(object sender, RoutedEventArgs e)
    {
        OriginalExpander.ApplyTemplate();
        if (OriginalExpander.Template.FindName("OriginalToggle", OriginalExpander) is not ToggleButton toggle) return;
        toggle.ApplyTemplate();
        if (toggle.Template.FindName("OriginalChevron", toggle) is System.Windows.Shapes.Path chevron && chevron.RenderTransform is RotateTransform rotation)
        {
            if (rotation.IsFrozen) { rotation = rotation.Clone(); chevron.RenderTransform = rotation; }
            double target = OriginalExpander.IsExpanded ? 0 : -90;
            if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
            {
                rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                rotation.Angle = target;
            }
            else rotation.BeginAnimation(RotateTransform.AngleProperty, Motion(rotation.Angle, target, 180));
        }
        if (OriginalExpander.IsExpanded && OriginalExpander.Template.FindName("OriginalContent", OriginalExpander) is FrameworkElement content)
        {
            var offset = new TranslateTransform();
            content.RenderTransform = offset;
            Reveal(content, offset, 4);
        }
    }

    internal static Point ClampPlacement(Rect selection, Rect work, double width, double height)
    {
        double x = Math.Clamp(selection.Left, work.Left, Math.Max(work.Left, work.Right - width));
        double y = selection.Bottom + 8;
        if (y + height > work.Bottom) y = selection.Top - height - 8;
        return new(x, Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height)));
    }

    internal void Expand()
    {
        if (_expanded && IsVisible && !_dismissing)
        {
            ClearDeferredPresentation();
            ApplyStatus("translation.pressEnter");
            UpdatePresentation(_presentationBounds);
            return;
        }
        _expanded = true;
        var cursor = System.Windows.Forms.Cursor.Position;
        var bounds = IsVisible && !_dismissing ? _presentationBounds : _selection?.Bounds ?? new Rect(cursor.X, cursor.Y, 1, 1);
        ClearDeferredPresentation();
        ApplyStatus("translation.pressEnter");
        UpdatePresentation(bounds, newPresentation: true);
        Activate();
        TranslateButton.Focus();
    }

    internal void SetStatus(string key)
    {
        ClearDeferredPresentation();
        if (_dismissing) return;
        if (DeferPresentation)
        {
            _deferredStatus = key;
            return;
        }
        ApplyStatus(key);
    }

    private void ApplyStatus(string key)
    {
        SetLoading(key == "translation.working");
        StatusText.Text = Loc.Get(key);
        TranslateButton.IsEnabled = key != "translation.working";
        CancelButton.Visibility = key == "translation.working" ? Visibility.Visible : Visibility.Collapsed;
        if (key == "translation.working") { StopResultReveal(); _result = null; CopyButton.IsEnabled = ReplaceButton.IsEnabled = false; }
    }

    private void SetLoading(bool loading)
    {
        LoadingIndicator.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(LoadingIndicator, Loc.Get("translation.working"));
        RefreshLoadingMotion();
    }

    private void RefreshLoadingMotion()
    {
        bool animate = LoadingIndicator.Visibility == Visibility.Visible && IsVisible &&
            !_dismissing && !_popupAnimating && !AnimationConfig.ReduceMotion && SystemParameters.ClientAreaAnimation;
        LoadingIndicator.IsActive = animate;
    }

    internal void ShowActivity(TranslationStage stage, int seconds)
    {
        string key = stage switch
        {
            TranslationStage.Waiting => "translation.waitingElapsed",
            TranslationStage.CheckingModel => "translation.checkingElapsed",
            TranslationStage.LoadingModel => "translation.loadingElapsed",
            _ => "translation.workingElapsed"
        };
        if (_dismissing || _deferredResult != null) return;
        string text = Loc.Get(key, seconds);
        if (DeferPresentation) _deferredActivity = text;
        else StatusText.Text = text;
    }

    private static string LanguageName(string code) => code == "auto" ? Loc.Get("translation.autoDetect") :
        TranslationLanguages.All.FirstOrDefault(x => x.Code == code)?.Name ?? code;

    private void UpdateLanguageSummary() => LanguageSummary.Text = $"{LanguageName(SourceLanguage)} → {LanguageName(TargetLanguage)}";

    private bool _formatText = true;

    internal void ConfigureFormatting(bool enabled)
    {
        _formatText = enabled;
        if (_result != null) PresentResultText();
    }

    private void PresentResultText()
    {
        StopResultReveal();
        if (_result == null) return;
        string text = _result.Text;
        bool longText = text.Length >= 400 || text.Count(c => c == '\n') >= 5;
        ResultText.FontSize = longText ? 15 : 19;
        ResultText.FontWeight = FontWeights.Bold;
        ResultText.Text = _formatText ? TranslationTextFormatting.Format(text) : text;
        if (IsVisible) SchedulePresentation();
    }

    internal void ShowResult(TranslationResult result)
    {
        if (_dismissing) return;
        ClearDeferredPresentation();
        if (DeferPresentation)
        {
            _deferredResult = result;
            return;
        }
        SetLoading(false);
        _result = result;
        PresentResultText();
        ResultScroll.ScrollToTop();
        CopyButton.IsEnabled = true;
        ReplaceButton.IsEnabled = _selection?.CanReplace == true && result.LiteralsPreserved && result.WeekdaysPreserved;
        TranslateButton.IsEnabled = true;
        CancelButton.Visibility = Visibility.Collapsed;
        LanguageSummary.Text = $"{LanguageName(result.SourceLanguage)} → {LanguageName(result.TargetLanguage)}";
        StatusText.Text = !result.WeekdaysPreserved ? Loc.Get("translation.checkDates") : result.LiteralsPreserved ? "" : Loc.Get("translation.checkFacts");
        if (_selection != null) UpdatePresentation(_selection.Bounds);
        QueueResultReveal();
    }

    internal void DisableReplacement() => ReplaceButton.IsEnabled = false;
    internal void Dismiss()
    {
        if (_dismissing) return;
        StopResultReveal(revealImmediately: false);
        _entranceCompletion?.TrySetCanceled();
        _entranceCompletion = null;
        ClearDeferredPresentation();
        _placementDirty = false;
        _presentationOperation?.Abort();
        _entrancePending = false;
        _expansionPending = false;
        EndPopupDrag();
        CancelPopupPlacement();
        int version = ++_presentationVersion;
        ++_motionVersion;
        _dismissing = true;
        _popupAnimating = true;
        // Stop the spinner but preserve the visible layout until the exit settles.
        RefreshLoadingMotion();
        PopupSurface.IsHitTestVisible = false;
        SourceLanguageCombo.IsDropDownOpen = TargetLanguageCombo.IsDropDownOpen = false;
        _selection = null; _result = null; _expanded = false;
        Dismissed?.Invoke();
        void Finish()
        {
            if (version != _presentationVersion) return;
            Hide();
            SetLoading(false);
            OriginalText.Text = ""; ResultText.Clear();
            _dismissing = false;
            CompletePopupMotion(_motionVersion);
        }
        if (!IsVisible || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation) { Finish(); return; }
        // Match the notch collapse's exponential settling curve.
        var fade = Motion(PopupSurface.Opacity, 0, ExitMilliseconds, AnimationPrimitives._easeExpOut6);
        fade.Completed += (_, _) => Finish();
        PopupSurface.BeginAnimation(OpacityProperty, fade);
        var slide = Motion(PopupOffset.X, -24, ExitMilliseconds, AnimationPrimitives._easeExpOut6);
        PopupOffset.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void Open_Click(object sender, RoutedEventArgs e) => Expand();
    private void Close_Click(object sender, RoutedEventArgs e) => Dismiss();
    private void Translate_Click(object sender, RoutedEventArgs e) => TranslateRequested?.Invoke();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();
    private void Replace_Click(object sender, RoutedEventArgs e) => ReplaceRequested?.Invoke();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        CopyText(ResultText.Text);
    }
    private void CopyText(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new System.IO.MemoryStream(new byte[4]));
            data.SetData("CanIncludeInClipboardHistory", new System.IO.MemoryStream(new byte[4]));
            data.SetData("CanUploadToCloudClipboard", new System.IO.MemoryStream(new byte[4]));
            Clipboard.SetDataObject(data, true);
            StatusText.Text = Loc.Get("translation.copied");
        }
        catch (System.Runtime.InteropServices.COMException) { SetStatus("translation.clipboardBusy"); }
    }
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SourceLanguageCombo.SelectedValue == null || TargetLanguageCombo.SelectedValue == null) return;
        StopResultReveal();
        _result = null; ResultText.Clear(); CopyButton.IsEnabled = ReplaceButton.IsEnabled = false;
        UpdateLanguageSummary();
        SetStatus("translation.pressEnter");
        LanguagesChanged?.Invoke(SourceLanguage, TargetLanguage);
    }
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (SourceLanguageCombo.IsDropDownOpen || TargetLanguageCombo.IsDropDownOpen)
                SourceLanguageCombo.IsDropDownOpen = TargetLanguageCombo.IsDropDownOpen = false;
            else Dismiss();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && TranslateButton.IsEnabled &&
                 e.OriginalSource is not ComboBoxItem && !SourceLanguageCombo.IsDropDownOpen && !TargetLanguageCombo.IsDropDownOpen)
        { TranslateRequested?.Invoke(); e.Handled = true; }
    }
}
