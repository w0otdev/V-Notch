using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray : UserControl, IDisposable
{
    public static readonly DependencyProperty NavigationWidthProperty = DependencyProperty.Register(
        nameof(NavigationWidth), typeof(double), typeof(ClipboardTray), new PropertyMetadata(0d),
        value => value is double width && double.IsFinite(width) && width >= 0);
    public double NavigationWidth
    {
        get => (double)GetValue(NavigationWidthProperty);
        set => SetValue(NavigationWidthProperty, value);
    }

    private static readonly TrayIconKind[] CategoryKinds =
    [
        TrayIconKind.All, TrayIconKind.Pin, TrayIconKind.Links, TrayIconKind.Text, TrayIconKind.Code,
        TrayIconKind.Images, TrayIconKind.Colors, TrayIconKind.Files, TrayIconKind.Work, TrayIconKind.Ideas,
        TrayIconKind.Email, TrayIconKind.Shopping, TrayIconKind.Receipts, TrayIconKind.Personal,
        TrayIconKind.Important, TrayIconKind.Archive
    ];
    private static readonly string[] Categories = CategoryKinds.Select(kind => kind.ToString()).ToArray();
    private readonly ClipboardCategoryViewModel[] _categoryItems = CategoryKinds.Select(kind => new ClipboardCategoryViewModel(kind)).ToArray();
    private readonly ClipboardCardCollection _cards = [];
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _ageTimer;
    private readonly CancellationTokenSource _contentLifetime = new();
    private readonly Queue<(ClipboardCapture Capture, long Size)> _approvals = new();
    private ClipboardHistoryController? _controller;
    private CancellationTokenSource? _searchCancellation;
    private string _category = "All";
    public string CurrentCategory => _category;
    private SecureString? _newPin;
    private Guid? _moveToPersonal;
    private Point? _dragStart;
    private ClipboardCardViewModel? _pressedCard;
    private bool _disposed;
    private int _refreshVersion;
    private readonly HashSet<ContextMenu> _openMenus = [];
    private readonly SemaphoreSlim _thumbnailSlots = new(2, 2);
    private IReadOnlyDictionary<string, int> _categoryCounts = new Dictionary<string, int>();
    private long _menuInteractionUntil;
    private bool _deleteConfirmationOpen;
    public bool IsContextMenuOpen => _deleteConfirmationOpen || _openMenus.Count != 0 || Environment.TickCount64 < _menuInteractionUntil;

    public bool ContainsMenuPoint(Point screenPoint)
    {
        foreach (var menu in _openMenus)
        {
            if (!menu.IsOpen || !menu.IsVisible) continue;
            if (ContainsVisualPoint(menu, screenPoint) || ContainsSubmenuPoint(menu, screenPoint)) return true;
        }
        return false;
    }

    private static bool ContainsSubmenuPoint(ItemsControl parent, Point screenPoint)
    {
        foreach (var item in parent.Items.OfType<MenuItem>())
        {
            if (ContainsVisualPoint(item, screenPoint)) return true;
            if (item.IsSubmenuOpen && ContainsSubmenuPoint(item, screenPoint)) return true;
        }
        return false;
    }

    private static bool ContainsVisualPoint(FrameworkElement visual, Point screenPoint)
    {
        if (!visual.IsVisible || PresentationSource.FromVisual(visual) == null) return false;
        try { return new Rect(visual.RenderSize).Contains(visual.PointFromScreen(screenPoint)); }
        catch (InvalidOperationException) { return false; }
    }

    public bool TryDismissOverlay()
    {
        if (_deleteConfirmationOpen || DeleteConfirmationPanel.Visibility == Visibility.Visible)
        { ResolveDeleteConfirmation(false); return true; }
        if (DetailPanel.Visibility == Visibility.Visible)
        {
            DismissDetails();
            return true;
        }
        if (ApprovalPanel.Visibility == Visibility.Visible)
        {
            SkipApproval();
            return true;
        }
        if (SearchBox.Text.Length == 0) return false;
        SearchBox.Clear();
        return true;
    }

    public ClipboardTray()
    {
        InitializeComponent();
        InitializeComponentMotion();
        InitializeCategoryScrolling();
        InitializeSelection();
        InitializeSearchEdgeFade();
        PasscodeBox.PasswordChanged += PasscodeBox_PasswordChanged;
        _statusTimer.Tick += StatusTimer_Tick;
        Cards.ItemsSource = _cards;
        CategoryPanel.ItemsSource = _categoryItems;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Interval = TimeSpan.FromMilliseconds(120);
            _ = SafeAsync(RefreshAsync);
        };
        Unloaded += Tray_Unloaded;
        Loaded += (_, _) => { if (!_disposed && _queryDirty) QueueRefresh(); };
        _ageTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _ageTimer.Tick += AgeTimer_Tick;
        Loaded += (_, _) => UpdateAgeTimer();
        Unloaded += (_, _) => { _ageTimer.Stop(); EndSelection(); };
        IsVisibleChanged += (_, _) =>
        {
            if (_disposed) return;
            UpdateAgeTimer();
            if (IsVisible)
            {
                _animateResults = false;
                if (_queryDirty)
                {
                    _searchTimer.Stop();
                    // Defer background query until entrance settles (420ms) for buttery 60/120fps opening
                    _searchTimer.Interval = TimeSpan.FromMilliseconds(420);
                    _searchTimer.Start();
                }
            }
            else
            {
                StopEntranceWork();
                StopCategoryScrolling(); CancelCardPositionAnimation();
                CancelPendingPersonalDrop();
                HidePrivateContent();
                if (_controller?.Store.IsPersonalUnlocked == true) _ = SafeAsync(_controller.LockPersonalAsync);
            }
        };
        ApplyLocalization();
        RebuildCategories();
    }

    public void Attach(ClipboardHistoryController controller)
    {
        _controller = controller;
        controller.Store.Changed += StoreChanged;
        controller.ApprovalRequired += RequestApproval;
        controller.StatusChanged += StatusChanged;
        controller.PersonalLocked += HidePrivateContent;
        controller.ImportProgress += OnImportProgress;
        QueueRefresh();
    }
    public void ApplyLocalization()
    {
        SearchHint.Text = Loc.Get("clipboard.search");
        EmptyText.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? Loc.Get("clipboard.empty") : Loc.Get("clipboard.noResults", SearchBox.Text);
        EmptyDropHint.Text = Loc.Get("clipboard.dropHint");
        ApproveButton.Content = Loc.Get("clipboard.saveCopy");
        SkipButton.Content = Loc.Get("clipboard.skip");
        ForgotPinButton.Content = Loc.Get("clipboard.forgotPin");
        ResetPersonalLabel.Text = Loc.Get("clipboard.reset");
        ResetConfirmationTitle.Text = Loc.Get("clipboard.resetTitle");
        ResetCloseButton.Content = Loc.Get("clipboard.cancel");
        ResetConfirmationHint.Text = Loc.Get("clipboard.typeDelete");
        PinBackButton.Content = Loc.Get("clipboard.pinBack");
        CloseDetailButton.Content = Loc.Get("clipboard.close");
        CopyDetailButton.Content = Loc.Get("clipboard.copy");
        ClearSearchButton.ToolTip = Loc.Get("settings.search.clear");
        System.Windows.Automation.AutomationProperties.SetName(ClearSearchButton, Loc.Get("settings.search.clear"));
        ConfirmDeleteButton.Content = Loc.Get("clipboard.delete");
        CancelDeleteButton.Content = Loc.Get("clipboard.cancel");
        KeyboardButton.ToolTip = Loc.Get("clipboard.shortcuts");
        InfoButton.ToolTip = Loc.Get("clipboard.information");
        System.Windows.Automation.AutomationProperties.SetName(SearchBox, Loc.Get("clipboard.search"));
        System.Windows.Automation.AutomationProperties.SetName(KeyboardButton, Loc.Get("clipboard.keyboardShortcuts"));
        System.Windows.Automation.AutomationProperties.SetName(InfoButton, Loc.Get("clipboard.information"));
        System.Windows.Automation.AutomationProperties.SetName(PasscodeBox, Loc.Get("clipboard.personalPasscode"));
        System.Windows.Automation.AutomationProperties.SetName(UnlockButton, Loc.Get("clipboard.unlockPersonal"));
        UpdatePauseLabel();
        LockTitle.Text = Loc.Get(_controller?.Store.HasPersonalPasscode == true ? "clipboard.locked" : "clipboard.setPin");
        UpdatePinStep();
        ShowApproval();
        if (_detailLocalizationKey != null) DetailText.Text = Loc.Get(_detailLocalizationKey);
        if (_deleteConfirmationOpen) DeleteConfirmationText.Text = Loc.Get("clipboard.deleteConfirm", _deleteConfirmationCount);
        RefreshVisibleCardAges();
        foreach (var category in _categoryItems) category.ApplyLocalization();
        foreach (var card in _cards) card.ApplyLocalization();
        foreach (var choice in _groupChoices) choice.ApplyLocalization();
        RebuildCategories();
    }
    private void StoreChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
    {
        if (_disposed) return;
        _queryDirty = true;
        if (IsVisible) QueueRefresh();
    }));
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _pendingSelectAll = null;
        if (SearchHint == null) return;
        UpdateSearchMotion();
        UpdateSearchEdgeFade();
        _animateResults = true;
        _searchTimer.Interval = TimeSpan.FromMilliseconds(120);
        QueueRefresh();
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
        UpdateSearchEdgeFade();
    }
    private bool _queryDirty = true;
    private void QueueRefresh() { _queryDirty = true; _searchCancellation?.Cancel(); _searchTimer.Stop(); _searchTimer.Start(); }
    private async Task RefreshAsync()
    {
        if (_controller == null || _disposed || !IsVisible) return;
        if (IsEntranceWorkDeferred) { _queryDirty = true; return; }
        if (_groupEntryId != null) { _queryDirty = true; return; }
        if (_selectionStart != null) { QueueRefresh(); return; }
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        var cancellation = _searchCancellation.Token;
        int version = ++_refreshVersion;
        if (UpdateCategoryLockState()) return;
        try
        {
            var entries = await _controller.Store.SearchAsync(SearchBox.Text, _category, cancellation);
            var counts = await Task.Run(_controller.Store.GetCategoryCounts, cancellation);
            if (version != _refreshVersion || cancellation.IsCancellationRequested || _disposed) return;
            await PrepareResultsAsync(cancellation);
            await AnimateCollectionChangesAsync(entries, cancellation);
            if (version != _refreshVersion || cancellation.IsCancellationRequested || _disposed || _trayLeaving) return;
            _categoryCounts = counts;
            if (_animateResults) FindScrollViewer(Cards)?.ScrollToHorizontalOffset(0);
            if (!entries.SequenceEqual(_cards.Select(card => card.Entry)))
            {
                var positions = CaptureCardPositions();
                ReconcileCards(entries);
                AnimateCardPositions(positions);
            }
            // Publish the new collection before revealing it. A private/public
            // category transition must never make the previous results visible.
            Cards.Visibility = Visibility.Visible;
            TrayDropBorder.Visibility = Visibility.Visible;
            RevealResults();
            RebuildCategories();
            bool isSearch = !string.IsNullOrWhiteSpace(SearchBox.Text);
            bool isEmpty = _cards.Count == 0;
            EmptyDropSilhouette.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            UpdateEmptyMessage(isEmpty, isSearch ? Loc.Get("clipboard.noResults", SearchBox.Text) : Loc.Get("clipboard.empty"));
            EmptyDropIcon.Visibility = isEmpty && !isSearch ? Visibility.Visible : Visibility.Collapsed;
            EmptyDropHint.Visibility = isEmpty && !isSearch ? Visibility.Visible : Visibility.Collapsed;
            _queryDirty = false;
            CompletePendingSelectAll();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        { StatusChanged(Loc.Get("clipboard.error")); }
    }

    private void ReconcileCards(IReadOnlyList<ClipboardEntry> entries)
    {
        var desired = entries.Select(entry => entry.Id).ToHashSet();
        _arrivingCards.RemoveWhere(id => !desired.Contains(id));
        var existing = _cards.ToDictionary(card => card.Entry.Id);
        // Count relative reorderings rather than shifted indices: inserting
        // one recent file should retain the existing containers and their motion.
        int changes = _cards.Count(card => !desired.Contains(card.Entry.Id));
        var survivingOrder = _cards.Where(card => desired.Contains(card.Entry.Id)).Select(card => card.Entry.Id).ToArray();
        int survivorIndex = 0;
        foreach (var entry in entries)
        {
            if (!existing.ContainsKey(entry.Id)) changes++;
            else if (survivingOrder[survivorIndex++] != entry.Id) changes++;
        }
        if (changes > 32)
        {
            var selected = Cards.SelectedItems.Cast<ClipboardCardViewModel>().Where(card => desired.Contains(card.Entry.Id)).ToArray();
            var scroll = FindScrollViewer(Cards);
            double offset = scroll?.HorizontalOffset ?? 0;
            var replacement = new ClipboardCardViewModel[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!existing.TryGetValue(entry.Id, out var card)) card = new ClipboardCardViewModel(entry);
                else card.UpdateEntry(entry);
                replacement[i] = card;
            }
            _cards.ReplaceAll(replacement);
            Cards.ApplySelection(selected);
            scroll?.ScrollToHorizontalOffset(offset);
            return;
        }
        for (int i = _cards.Count - 1; i >= 0; i--)
            if (!desired.Contains(_cards[i].Entry.Id)) _cards.RemoveAt(i);
        for (int index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!existing.TryGetValue(entry.Id, out var card))
            {
                _cards.Insert(index, new ClipboardCardViewModel(entry));
                continue;
            }
            int oldIndex = index < _cards.Count && ReferenceEquals(_cards[index], card) ? index : _cards.IndexOf(card);
            if (oldIndex != index) _cards.Move(oldIndex, index);
            if (card.Entry != entry)
                card.UpdateEntry(entry);
        }
    }

    private void RebuildCategories()
    {
        foreach (var category in _categoryItems)
        {
            category.Count = _categoryCounts.GetValueOrDefault(category.Name);
            category.IsSelected = _category == category.Name;
            category.IsUnlocked = _controller?.Store.IsPersonalUnlocked == true;
        }
        CategoryPanel_LayoutUpdated(this, EventArgs.Empty);
    }
    private void Category_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { DataContext: ClipboardCategoryViewModel category }) SelectCategory(category.Name); }
    private static string CategoryLabel(string name)
    {
        string key = "clipboard.category." + name.ToLowerInvariant();
        return Loc.Get(key);
    }
    private static readonly ConcurrentDictionary<string, Brush> CachedBrushes = new(StringComparer.Ordinal);
    private static Brush Brush(string value) => CachedBrushes.GetOrAdd(value, static color =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    });
    private void SelectCategory(string category)
    {
        _pendingSelectAll = null;
        if (category != "Personal") { CancelPendingPersonalDrop(); _moveToPersonal = null; }
        if (_category != category) ResolveDeleteConfirmation(false);
        if (_category == category) return;
        bool leavingPersonal = _category == "Personal";
        bool personalTransition = leavingPersonal || category == "Personal";
        CancelCardPositionAnimation();
        EndSelection();
        foreach (var menu in _openMenus.ToArray()) menu.IsOpen = false;
        Cards.SelectedItems.Clear();
        _selectionAnchor = null;
        _resultsDirection = Array.IndexOf(Categories, category) >= Array.IndexOf(Categories, _category) ? 1 : -1;
        _category = category;
        _animateResults = true;
        if (personalTransition)
        {
            ++_refreshVersion;
            _searchCancellation?.Cancel();
            Cards.BeginAnimation(OpacityProperty, null);
            Cards.Opacity = 0;
            Cards.Visibility = Visibility.Hidden;
            EmptyText.Visibility = Visibility.Collapsed;
            EmptyDropSilhouette.Visibility = Visibility.Collapsed;
            CancelThumbnailLoads(); ReleaseCardPreviews();
            _cards.Clear(); _arrivingCards.Clear();
        }
        CloseDetails();
        ClearPendingPin(); PasscodeBox.Clear();
        // PersonalLocked fires synchronously. Set the destination category and
        // hide the old content before it runs so it cannot re-show the lock UI.
        if (leavingPersonal && _controller != null) _ = SafeAsync(_controller.LockPersonalAsync);
        UpdateCategoryLockState();
        RebuildCategories(); QueueRefresh();
    }

    private bool UpdateCategoryLockState()
    {
        bool locked = _category == "Personal" && _controller?.Store.IsPersonalUnlocked != true;
        if (locked) ShowTransientLayer(LockPanel);
        else
        {
            if (PasscodeBox.IsKeyboardFocusWithin) SearchBox.Focus();
            DismissTransientLayer(LockPanel);
        }
        if (!locked)
        {
            TrayDropBorder.Visibility = Visibility.Visible;
            return false;
        }
        Cards.Visibility = Visibility.Collapsed;
        TrayDropBorder.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        EmptyDropSilhouette.Visibility = Visibility.Collapsed;
        _cards.Clear();
        LockTitle.Text = Loc.Get(_controller?.Store.HasPersonalPasscode == true ? "clipboard.locked" : "clipboard.setPin");
        UpdatePinStep();
        ForgotPinButton.Visibility = _controller?.Store.HasPersonalPasscode == true ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }
    private void HidePrivateContent()
    {
        _pendingSelectAll = null;
        _pendingMenuEntrances.Clear();
        _menuAnimationTargets.Clear();
        CancelThumbnailLoads(personalOnly: IsLoaded && IsVisible);
        ReleaseCardPreviews(personalOnly: IsLoaded && IsVisible);
        ClearGroupChoices();
        GroupChoices.Visibility = Visibility.Collapsed;
        EndSelection();
        CancelPendingPersonalDrop();
        // Pending approvals can expose private file names and target a locked store.
        _approvals.Clear(); ApprovalPanel.Visibility = Visibility.Collapsed;
        ResolveDeleteConfirmation(false, immediately: true); ClearStatus(immediately: true);
        _selectionAnchor = null;
        foreach (var menu in _openMenus.ToArray()) menu.IsOpen = false;
        foreach (var card in _cards) card.IsMenuOpen = false;
        ++_refreshVersion; _searchCancellation?.Cancel();
        ClearPendingPin(); _moveToPersonal = null; PasscodeBox.Clear(); ResetPinDots(immediately: true);
        _pressedCard = null; _dragStart = null;
        _copyOnRelease = false;
        _detailCard = null;
        _detailLocalizationKey = null;
        CopyDetailButton.Visibility = Visibility.Collapsed;
        DetailImage.Source = null; DetailImage.Visibility = Visibility.Collapsed;
        ResetPersonalButton.Visibility = Visibility.Collapsed;
        ResetConfirmationPanel.Visibility = Visibility.Collapsed;
        ResetConfirmationBox.Clear();
        DetailText.Clear(); DetailPanel.Visibility = Visibility.Collapsed;
        foreach (var card in _cards.Where(card => card.Entry.IsPersonal)) card.Image = null;
        if (_category == "Personal") { _queryDirty = true; _cards.Clear(); Cards.Visibility = Visibility.Collapsed; ShowTransientLayer(LockPanel); }
        else foreach (var card in _cards.Where(card => card.Entry.IsPersonal).ToArray()) _cards.Remove(card);
    }

    private async void Unlock_Click(object sender, RoutedEventArgs e) => await SafeAsync(UnlockPersonalAsync);

    private void ClearPendingPin() { _newPin?.Dispose(); _newPin = null; }

    private async Task UnlockPersonalAsync()
    {
        if (_controller == null || !UnlockButton.IsEnabled || _disposed) return;
        using var pin = PasscodeBox.SecurePassword;
        PasscodeBox.Clear();
        if (!ClipboardPasscode.IsValid(pin)) { LockHint.Text = Loc.Get("clipboard.sixDigits"); return; }
        bool create = !_controller.Store.HasPersonalPasscode;
        if (create && _newPin == null) { _newPin = pin.Copy(); _newPin.MakeReadOnly(); UpdatePinStep(); return; }
        if (create && !ClipboardPasscode.Matches(_newPin!, pin)) { LockHint.Text = Loc.Get("clipboard.confirmRetry"); PasscodeBox.Focus(); return; }
        ClearPendingPin(); UnlockButton.IsEnabled = false;
        int version = _refreshVersion;
        try
        {
            var result = await _controller.Store.UnlockPersonalWithResultAsync(pin, create);
            if (!result.Succeeded)
            {
                LockHint.Text = result.Status == ClipboardUnlockStatus.RateLimited
                    ? Loc.Get("clipboard.pinRetryAfter", Math.Max(1, (int)Math.Ceiling((result.RetryAfterUtc - DateTime.UtcNow).TotalSeconds)))
                    : Loc.Get("clipboard.wrongPin");
                return;
            }
            if (_disposed || !IsLoaded || !IsVisible || _category != "Personal" || version != _refreshVersion)
            { await _controller.LockPersonalAsync(); return; }
            if (_moveToPersonal is Guid id) { _moveToPersonal = null; await _controller.Store.MovePersonalAsync(id, true); }
            await CompletePersonalDropAsync();
            RebuildCategories(); await RefreshAsync();
        }
        catch (Exception ex) when (IsContentError(ex))
        { LockHint.Text = Loc.Get("clipboard.error"); }
        finally { UnlockButton.IsEnabled = true; }
    }
    private void UpdatePinStep()
    {
        bool create = _controller?.Store.HasPersonalPasscode != true;
        LockHint.Text = Loc.Get(!create ? "clipboard.enterPin" : _newPin == null ? "clipboard.createStep" : "clipboard.confirmStep");
        PinBackButton.Visibility = create && _newPin != null ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PinBack_Click(object sender, RoutedEventArgs e)
    { ClearPendingPin(); PasscodeBox.Clear(); UpdatePinStep(); PasscodeBox.Focus(); }

    private void Passcode_KeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { e.Handled = true; _ = SafeAsync(UnlockPersonalAsync); } }

    private Border[]? _pinDots;
    private int _previousPinLength;

    private void PasscodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        UpdatePinDotsAnimation();
    }

    private void UpdatePinDotsAnimation(bool immediately = false)
    {
        _pinDots ??= [PinDot0, PinDot1, PinDot2, PinDot3, PinDot4, PinDot5];
        // Each SecurePassword read allocates a fresh copy; dispose it immediately.
        int currentLength;
        using (var secure = PasscodeBox.SecurePassword)
            currentLength = Math.Clamp(secure.Length, 0, _pinDots.Length);
        bool reduced = immediately || AnimationConfig.ReduceMotion;

        if (currentLength > _previousPinLength)
        {
            for (int i = _previousPinLength; i < currentLength; i++)
                AnimatePinDotIn(_pinDots[i], reduced);
        }
        else if (currentLength < _previousPinLength)
        {
            for (int i = currentLength; i < _previousPinLength; i++)
                AnimatePinDotOut(_pinDots[i], reduced);
        }
        _previousPinLength = currentLength;
        UpdatePinCaret();
    }

    private static void AnimatePinDotIn(Border dot, bool reduced)
    {
        if (dot.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(0, 0);
            dot.RenderTransform = st;
        }

        if (reduced)
        {
            dot.BeginAnimation(UIElement.OpacityProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            dot.Opacity = 1.0;
            st.ScaleX = 1.0;
            st.ScaleY = 1.0;
            return;
        }

        dot.Opacity = 1.0;
        var opacityAnim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(160),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        dot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

        var scaleAnim = new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(0.2, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new SplineDoubleKeyFrame(1.22, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new KeySpline(0.2, 0.8, 0.4, 1.0)),
                new SplineDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)), new KeySpline(0.4, 0.0, 0.2, 1.0))
            }
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private static void AnimatePinDotOut(Border dot, bool reduced)
    {
        if (dot.RenderTransform is not ScaleTransform st)
        {
            st = new ScaleTransform(1, 1);
            dot.RenderTransform = st;
        }

        if (reduced)
        {
            dot.BeginAnimation(UIElement.OpacityProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            dot.Opacity = 0.0;
            st.ScaleX = 0.0;
            st.ScaleY = 0.0;
            return;
        }

        dot.Opacity = 0.0;
        var opacityAnim = new DoubleAnimation
        {
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        dot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);

        var scaleAnim = new DoubleAnimation
        {
            To = 0.15,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        st.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnim);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnim);
    }

    private void ResetPinDots(bool immediately = true)
    {
        _pinDots ??= [PinDot0, PinDot1, PinDot2, PinDot3, PinDot4, PinDot5];
        _previousPinLength = 0;
        foreach (var dot in _pinDots)
        {
            dot.BeginAnimation(UIElement.OpacityProperty, null);
            if (dot.RenderTransform is ScaleTransform st)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleX = 0;
                st.ScaleY = 0;
            }
            dot.Opacity = 0;
        }
    }

    private void Card_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Border border || e.NewSize.Width <= 0 || e.NewSize.Height <= 0) return;
        var bounds = new Rect(e.NewSize);
        if (border.Clip is RectangleGeometry existing && existing.Rect == bounds) return;
        var clip = new RectangleGeometry(bounds, 16, 16);
        clip.Freeze();
        border.Clip = clip;
    }
    private void Card_Down(object sender, MouseButtonEventArgs e)
    {
        if (IsCardAction(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement { DataContext: ClipboardCardViewModel card }) return;
        AnimateCardPress((FrameworkElement)sender, true);
        _copyOnRelease = e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && Cards.SelectedItems.Count <= 1;
        _pressedCard = card; _dragStart = e.GetPosition(this); SelectCard(card); e.Handled = true;
    }
    private async void Card_Up(object sender, MouseButtonEventArgs e)
    {
        if (IsCardAction(e.OriginalSource as DependencyObject)) return;
        if (sender is FrameworkElement element) AnimateCardPress(element, false);
        if (_dragStart == null || sender is not FrameworkElement { DataContext: ClipboardCardViewModel card } || card != _pressedCard) return;
        _dragStart = null; _pressedCard = null; e.Handled = true;
        bool copy = _copyOnRelease;
        _copyOnRelease = false;
        if (copy && Keyboard.Modifiers == ModifierKeys.None && !_selectionModified) await SafeAsync(() => CopyAsync(card));
    }
    private bool _copyOnRelease;
    private async void Card_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragStart is not Point start || _pressedCard == null || _controller == null || sender is not FrameworkElement element) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var card = _pressedCard; _dragStart = null; _pressedCard = null;
        _copyOnRelease = false;
        AnimateCardPress(element, false);
        await SafeAsync(() => _controller.DragManyAsync(element, SelectedCards().Select(item => item.Entry).ToArray()));
    }
    private async Task CopyAsync(ClipboardCardViewModel card)
    {
        if (_controller == null) return;
        try
        {
            await _controller.CopyAsync(card.Entry);
            card.IsCopied = true;
            await Task.Delay(1600); card.IsCopied = false;
        }
        catch (Exception ex) when (IsContentError(ex) || ex is System.Runtime.InteropServices.ExternalException)
        { StatusChanged(Loc.Get("clipboard.error")); }
    }
    private long _lastClosedMenuTime;
    private object? _lastClosedMenuTarget;
    private ContextMenu? _lastClosedMenu;
    private object? _lastClosedMenuCard;
    private FrameworkElement? _lastOpenedMenuTarget;
    private object? _lastOpenedMenuCard;
    private ContextMenu? _lastOpenedMenu;

    private static FrameworkElement? FindCardVisual(DependencyObject? element)
    {
        while (element != null)
        {
            if (element is Border { DataContext: ClipboardCardViewModel } border && Math.Abs(border.Width - 210) < 1)
                return border;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private static ContextMenu PrepareCardMenu(FrameworkElement card, Button button)
    {
        var menu = card.ContextMenu ?? button.ContextMenu ?? new ContextMenu();
        card.ContextMenu = menu;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = PlaceCardMenu;
        menu.PlacementTarget = card;
        return menu;
    }

    private void OpenCardMenu(FrameworkElement card, ContextMenu menu, Button trigger)
    {
        foreach (var other in _openMenus.ToArray())
            if (!ReferenceEquals(other, menu)) other.IsOpen = false;
        _menuAnimationTargets[menu] = trigger;
        PopulateCardMenu(card);
        menu.IsOpen = true;
        AnimateTriggerHaptic(trigger);
    }

    private void MoreActions_PointerDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        _pressedCard = null;
        _copyOnRelease = false;
        // Let Button receive the press so mouse and touch can raise Click.
    }

    private void MoreActions_Click(object sender, RoutedEventArgs e)
    {
        if (e.RoutedEvent != null) e.Handled = true;
        _dragStart = null; _pressedCard = null;
        _copyOnRelease = false;
        if (_disposed || _trayLeaving || _controller?.IsDragging == true || _selectionStart != null) return;
        if (sender is not Button button) return;
        var card = FindCardVisual(button) ?? (FrameworkElement)button;

        var menu = PrepareCardMenu(card, button);
        menu.StaysOpen = false;

        if (menu.IsOpen || _openMenus.Contains(menu))
        {
            menu.IsOpen = false;
            _lastClosedMenuTarget = card;
            _lastClosedMenuCard = card.DataContext;
            _lastClosedMenu = menu;
            _lastClosedMenuTime = Environment.TickCount64;
            if (card.DataContext is ClipboardCardViewModel cardVm) cardVm.IsMenuOpen = false;
            return;
        }

        bool wasJustClosed = Environment.TickCount64 - _lastClosedMenuTime < 450;
        if (wasJustClosed && (ReferenceEquals(_lastClosedMenu, menu) ||
                              ReferenceEquals(_lastClosedMenuCard, card.DataContext) ||
                              ReferenceEquals(_lastClosedMenuTarget, card) ||
                              ReferenceEquals(_lastClosedMenuTarget, button)))
        {
            _lastClosedMenuTarget = null;
            _lastClosedMenuCard = null;
            _lastClosedMenu = null;
            _lastClosedMenuTime = 0;
            return;
        }

        OpenCardMenu(card, menu, button);
    }

    private static System.Windows.Controls.Primitives.CustomPopupPlacement[] PlaceCardMenu(Size popupSize, Size targetSize, Point offset)
    {
        double xRight = targetSize.Width > 100 ? targetSize.Width + 6 : targetSize.Width + 14;
        double xLeft = -popupSize.Width - 6;
        double yTop = targetSize.Width > 100 ? 0 : -6;
        return
        [
            new(new Point(xRight, yTop), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
            new(new Point(xLeft, yTop), System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal),
            new(new Point(targetSize.Width - popupSize.Width, yTop), System.Windows.Controls.Primitives.PopupPrimaryAxis.Vertical)
        ];
    }

    private void Card_ContextMenu(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var menu = element.ContextMenu ??= new ContextMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = PlaceCardMenu;
            menu.PlacementTarget = element;
        }
        PopulateCardMenu(sender);
    }
    private void PopulateCardMenu(object sender)
    {
        if (sender is not FrameworkElement { DataContext: ClipboardCardViewModel card, ContextMenu: { } menu } || _controller == null) return;
        if (menu.Parent is System.Windows.Controls.Primitives.Popup popup)
            popup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None;
        _menuInteractionUntil = Environment.TickCount64 + 350;
        _lastOpenedMenu = menu;
        _lastOpenedMenuTarget = menu.PlacementTarget as FrameworkElement ?? (sender as FrameworkElement);
        _lastOpenedMenuCard = card;
        menu.Opened -= MenuOpened;
        menu.Closed -= MenuClosed;
        menu.Opened += MenuOpened;
        menu.Closed += MenuClosed;
        menu.Items.Clear();
        if (!Cards.SelectedItems.Contains(card)) { Cards.SelectedItems.Clear(); Cards.SelectedItems.Add(card); }
        var selection = SelectedCards();
        if (selection.Length > 1)
        {
            AddMenu(menu, "clipboard.copy", CopySelectionAsync);
            AddMenu(menu, "clipboard.pin", async () => { foreach (var item in selection) await _controller.Store.UpdateAsync(item.Entry.Id, pinned: true); });
            menu.Items.Add(new Separator());
            AddMenu(menu, "clipboard.archive", async () => { foreach (var item in selection) await _controller.Store.UpdateAsync(item.Entry.Id, archived: true); });
            AddMenu(menu, "clipboard.delete", () => ConfirmDeleteAsync(selection));
            return;
        }

        // Section 1: Quick Actions
        AddMenu(menu, "clipboard.copy", () => CopyAsync(card));
        AddMenu(menu, card.Entry.IsPinned ? "clipboard.unpin" : "clipboard.pin", () => _controller.Store.UpdateAsync(card.Entry.Id, pinned: !card.Entry.IsPinned));
        AddMenu(menu, "clipboard.details", () => ShowDetailsAsync(card));

        menu.Items.Add(new Separator());

        // Section 2: Organization & Categories
        var groups = new MenuItem
        {
            Header = Loc.Get("clipboard.groups"),
            Icon = new TrayIcon { Kind = TrayIconKind.Shopping, Width = 14, Height = 14 }
        };
        foreach (string group in ClipboardClassifier.GroupNames)
        {
            var groupKind = Enum.TryParse<TrayIconKind>(group, out var parsedKind) ? parsedKind : TrayIconKind.Files;
            var item = new MenuItem
            {
                Header = CategoryLabel(group),
                Tag = group,
                IsCheckable = true,
                IsChecked = card.Entry.Groups.Contains(group),
                Icon = new TrayIcon { Kind = groupKind, Width = 13, Height = 13 }
            };
            item.Click += async (_, _) => await SafeAsync(() => _controller.Store.UpdateAsync(card.Entry.Id,
                groups: groups.Items.OfType<MenuItem>().Where(choice => choice.IsChecked).Select(choice => (string)choice.Tag).ToArray()));
            groups.Items.Add(item);
        }
        menu.Items.Add(groups);
        AddMenu(menu, card.Entry.IsPersonal ? "clipboard.moveOut" : "clipboard.movePersonal", async () =>
        {
            if (!_controller.Store.IsPersonalUnlocked) { SelectCategory("Personal"); _moveToPersonal = card.Entry.Id; return; }
            await _controller.Store.MovePersonalAsync(card.Entry.Id, !card.Entry.IsPersonal);
        });
        AddMenu(menu, card.Entry.IsArchived ? "clipboard.unarchive" : "clipboard.archive", () => _controller.Store.UpdateAsync(card.Entry.Id, archived: !card.Entry.IsArchived));

        menu.Items.Add(new Separator());

        // Section 3: Destructive
        AddMenu(menu, "clipboard.delete", () => ConfirmDeleteAsync([card]));
    }
    private void MenuOpened(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        if ((menu.PlacementTarget as FrameworkElement)?.DataContext is ClipboardCardViewModel cardVm)
            cardVm.IsMenuOpen = true;
        else if (_lastOpenedMenuCard is ClipboardCardViewModel lastCard)
            lastCard.IsMenuOpen = true;
        bool fresh = _openMenus.Add(menu);
        menu.ApplyTemplate();
        if (menu.Template.FindName("MenuSurface", menu) is FrameworkElement surface)
            QueueMenuEntrance(surface, _menuAnimationTargets.GetValueOrDefault(menu) ?? menu.PlacementTarget as FrameworkElement,
                () => menu.IsOpen, fresh);
        if (menu.Parent is System.Windows.Controls.Primitives.Popup popup)
            ConfigureMenuDismissal(popup);
        EnsureMenuTopmost(menu);
    }
    private void EnsureMenuTopmost(FrameworkElement element)
    {
        void SetTopmost()
        {
            if (_disposed || !element.IsVisible) return;
            if (PresentationSource.FromVisual(element) is HwndSource source && source.Handle != IntPtr.Zero)
            {
                Win32Interop.SetWindowPos(source.Handle, Win32Interop.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32Interop.SWP_NOMOVE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOACTIVATE |
                    Win32Interop.SWP_NOOWNERZORDER | Win32Interop.SWP_SHOWWINDOW);
            }
        }
        SetTopmost();
        Dispatcher.BeginInvoke(new Action(SetTopmost), DispatcherPriority.Loaded);
    }
    private void MenuClosed(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.StaysOpen = false;
        var target = menu.PlacementTarget as FrameworkElement;
        var card = target?.DataContext as ClipboardCardViewModel;
        // A previous popup can finish its native fade after hover opens another
        // card's menu. Only clear state belonging to the popup that actually closed.
        if (ReferenceEquals(_lastOpenedMenu, menu))
        {
            target ??= _lastOpenedMenuTarget;
            card ??= _lastOpenedMenuCard as ClipboardCardViewModel;
        }
        if (ReferenceEquals(_lastClosedMenu, menu))
        {
            target ??= _lastClosedMenuTarget as FrameworkElement;
            card ??= _lastClosedMenuCard as ClipboardCardViewModel;
        }
        if (card != null) card.IsMenuOpen = false;
        if (menu.Template?.FindName("MenuSurface", menu) is FrameworkElement surface) CancelMenuEntrance(surface);
        _menuAnimationTargets.Remove(menu);
        if (menu.Parent is System.Windows.Controls.Primitives.Popup popup)
            popup.PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None;
        _openMenus.Remove(menu);
        menu.Items.Clear();
        _menuInteractionUntil = Environment.TickCount64 + 250;
        _lastClosedMenuTarget = target;
        _lastClosedMenuCard = card;
        _lastClosedMenu = menu;
        _lastClosedMenuTime = Environment.TickCount64;
    }

    private ClipboardCardViewModel? _detailCard;
    private string? _detailLocalizationKey;
    private async Task ShowDetailsAsync(ClipboardCardViewModel card)
    {
        if (_controller == null) return;
        int version = _refreshVersion;
        int detailVersion = ++_detailDismissVersion;
        var content = card.Entry.Kind == ClipboardKind.Image ? null : await _controller.Store.GetContentAsync(card.Entry.Id);
        if (_disposed || !IsVisible || version != _refreshVersion || detailVersion != _detailDismissVersion || card.Entry.IsPersonal && !_controller.Store.IsPersonalUnlocked) return;
        CloseDetails();
        detailVersion = _detailDismissVersion;
        DetailTextScroll.Visibility = Visibility.Visible;
        string text = content == null ? "" : content.Text.Length != 0 ? content.Text : string.Join(Environment.NewLine, content.Files.Select(file => file.Name));
        const int detailPreviewLimit = 16_000;
        DetailText.Text = text.Length > detailPreviewLimit ? text[..detailPreviewLimit] + "\n…" : text;
        if (card.Entry.Kind == ClipboardKind.Image)
        {
            byte[]? bytes = await _controller.Store.GetImageAsync(card.Entry.Id);
            try
            {
                if (bytes == null) return;
                var image = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.DecodePixelWidth = 1200; bitmap.StreamSource = stream;
                    bitmap.EndInit(); bitmap.Freeze(); return bitmap;
                });
                if (_disposed || !IsVisible || version != _refreshVersion || detailVersion != _detailDismissVersion || card.Entry.IsPersonal && !_controller.Store.IsPersonalUnlocked) return;
                DetailImage.Source = image;
                DetailImage.Visibility = Visibility.Visible;
                DetailTextScroll.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (IsContentError(ex))
            {
                if (_disposed || !IsVisible || version != _refreshVersion || detailVersion != _detailDismissVersion || card.Entry.IsPersonal && !_controller.Store.IsPersonalUnlocked) return;
                DetailImage.Source = null;
                DetailImage.Visibility = Visibility.Collapsed;
                DetailTextScroll.Visibility = Visibility.Visible;
                DetailText.Text = Loc.Get("clipboard.readError");
            }
            finally { if (bytes != null && card.Entry.IsPersonal) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        _detailCard = card;
        CopyDetailButton.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Visible;
    }
    private void AddMenu(ItemsControl menu, string key, Func<Task> action)
    {
        TrayIconKind icon = key.Split('.').Last() switch
        {
            "copy" => TrayIconKind.Copy,
            "pin" or "unpin" => TrayIconKind.Pin,
            "archive" or "unarchive" => TrayIconKind.Archive,
            "movePersonal" or "moveOut" => TrayIconKind.Lock,
            "details" => TrayIconKind.Info,
            "delete" => TrayIconKind.Delete,
            _ => TrayIconKind.Files
        };
        var item = new MenuItem
        {
            Header = Loc.Get(key),
            InputGestureText = key switch { "clipboard.copy" => "Ctrl+C", "clipboard.delete" => "Del", _ => "" },
            Icon = new TrayIcon { Kind = icon, Width = 14, Height = 14 }
        };
        if (key == "clipboard.delete")
        {
            item.Tag = "danger";
        }
        item.Click += async (_, _) => { _menuInteractionUntil = Environment.TickCount64 + 350; await SafeAsync(action); };
        menu.Items.Add(item);
    }
    private async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RuntimeLog.Error("CLIPBOARD", ex.GetType().Name);
            StatusChanged(Loc.Get("clipboard.error"));
        }
    }

    private static bool IsContentError(Exception exception) => exception is IOException or InvalidOperationException
        or KeyNotFoundException or UnauthorizedAccessException or NotSupportedException
        or FileFormatException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException;

    private void RequestApproval(ClipboardCapture capture, long size)
    {
        _approvals.Enqueue((capture, size)); ShowApproval();
    }
    private void ShowApproval()
    {
        ApprovalPanel.Visibility = _approvals.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (_approvals.TryPeek(out var pending))
        {
            var files = pending.Capture.FilePaths;
            string names = files.Length switch
            {
                0 => "Item",
                1 => Path.GetFileName(files[0]),
                <= 3 => string.Join(", ", files.Select(Path.GetFileName)),
                _ => $"{string.Join(", ", files.Take(2).Select(Path.GetFileName))} +{files.Length - 2}"
            };
            ApprovalText.Text = string.Format(Loc.GetCulture(), Loc.Get("clipboard.largeFile"), pending.Size / (1024d * 1024), names);
        }
    }
    private void OnImportProgress(double progress)
    {
        if (_disposed) return;
        Dispatcher.InvokeAsync(() =>
        {
            if (_disposed) return;
            if (progress > 0 && progress < 1.0)
            {
                ImportProgressBar.Value = progress * 100;
                if (ImportProgressBar.Visibility != Visibility.Visible)
                {
                    ImportProgressBar.Visibility = Visibility.Visible;
                    AnimateLayer(ImportProgressBar, true, 0);
                }
            }
            else
            {
                ImportProgressBar.Value = 100;
                AnimateLayer(ImportProgressBar, false, 0);
                _ = Task.Delay(250).ContinueWith(_ => Dispatcher.InvokeAsync(() =>
                {
                    if (!_disposed) ImportProgressBar.Visibility = Visibility.Collapsed;
                }));
            }
        });
    }
    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (_controller == null || !_approvals.TryDequeue(out var pending)) return;
        ApproveButton.IsEnabled = false;
        try { await SafeAsync(() => _controller.ImportAsync(pending.Capture, true)); }
        finally { ApproveButton.IsEnabled = true; ShowApproval(); }
    }
    private void Skip_Click(object sender, RoutedEventArgs e) => SkipApproval();
    private void SkipApproval() { _approvals.TryDequeue(out _); ShowApproval(); }
    private void CloseDetail_Click(object sender, RoutedEventArgs e) => DismissDetails();
    private async void CopyDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_detailCard is not { } card) return;
        CopyDetailButton.IsEnabled = false;
        try { await SafeAsync(() => CopyAsync(card)); }
        finally { if (!_disposed) CopyDetailButton.IsEnabled = true; }
    }

    private int _detailDismissVersion;
    private async void DismissDetails()
    {
        int version = ++_detailDismissVersion;
        AnimateLayer(DetailPanel, false, 0);
        if (!AnimationConfig.ReduceMotion) await Task.Delay(MotionStandard);
        if (version == _detailDismissVersion && !_disposed) CloseDetails();
    }

    private void CloseDetails()
    {
        ++_detailDismissVersion;
        _detailCard = null;
        _detailLocalizationKey = null;
        CopyDetailButton.Visibility = Visibility.Collapsed;
        bool refreshGroups = _groupEntryId != null;
        GroupChoices.Visibility = Visibility.Collapsed;
        ClearGroupChoices();
        DetailPanel.Visibility = Visibility.Collapsed;
        ResetPersonalButton.Visibility = Visibility.Collapsed;
        ResetConfirmationPanel.Visibility = Visibility.Collapsed;
        ResetConfirmationBox.Clear();
        DetailImage.Source = null;
        DetailImage.Visibility = Visibility.Collapsed;
        DetailTextScroll.Visibility = Visibility.Visible;
        DetailText.Clear();
        if (refreshGroups && _queryDirty && !_disposed) QueueRefresh();
    }
    private void ForgotPin_Click(object sender, RoutedEventArgs e)
    {
        CloseDetails();
        _detailLocalizationKey = "clipboard.resetConfirm";
        DetailText.Text = Loc.Get(_detailLocalizationKey);
        ResetPersonalButton.Visibility = Visibility.Visible;
        ResetConfirmationPanel.Visibility = Visibility.Visible;
        DetailPanel.Visibility = Visibility.Visible;
        ResetConfirmationBox.Focus();
    }
    private void ResetConfirmation_Changed(object sender, TextChangedEventArgs e)
    { if (ResetPersonalButton != null) ResetPersonalButton.IsEnabled = IsResetConfirmed(); }
    private bool IsResetConfirmed() => ResetConfirmationBox.Text.Trim() == "DELETE";

    private async void ResetPersonal_Click(object sender, RoutedEventArgs e) => await SafeAsync(ResetPersonalAsync);
    private async Task ResetPersonalAsync()
    {
        if (_controller == null || ResetConfirmationPanel.Visibility != Visibility.Visible || !IsResetConfirmed()) return;
        ResetPersonalButton.IsEnabled = false;
        try
        {
            HidePrivateContent();
            await _controller.Store.ResetPersonalAsync();
            HidePrivateContent();
            ResetPersonalButton.Visibility = Visibility.Collapsed;
            ResetConfirmationPanel.Visibility = Visibility.Collapsed;
            ResetConfirmationBox.Clear();
            RebuildCategories();
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusChanged(Loc.Get("clipboard.error")); }
        finally { ResetPersonalButton.IsEnabled = IsResetConfirmed(); }
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_controller == null) return;
        _controller.IsPaused = !_controller.IsPaused;
        PauseGlyph.Kind = _controller.IsPaused ? TrayIconKind.Resume : TrayIconKind.Pause;
        AnimateLayer(PauseGlyph, true, 0, true);
        PauseGlyph.Foreground = _controller.IsPaused ? (Brush)FindResource("TrayAccent") : Brush("#BFFFFFFF");
        UpdatePauseLabel();
    }
    private void UpdatePauseLabel()
    {
        string label = Loc.Get(_controller?.IsPaused == true ? "clipboard.resume" : "clipboard.pause");
        PauseButton.ToolTip = label;
        System.Windows.Automation.AutomationProperties.SetName(PauseButton, label);
    }
    private void Keyboard_Click(object sender, RoutedEventArgs e) => ShowInfo("clipboard.shortcuts");
    private void Info_Click(object sender, RoutedEventArgs e) => ShowInfo("clipboard.info");
    private void ShowInfo(string key)
    {
        CloseDetails();
        _detailLocalizationKey = key;
        DetailText.Text = Loc.Get(key);
        DetailPanel.Visibility = Visibility.Visible;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is ScrollViewer scroll) return scroll; if (FindScrollViewer(child) is { } nested) return nested; }
        return null;
    }
    private async void Cards_KeyDown(object sender, KeyEventArgs e)
    {
        if (_controller == null) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = SelectAllTrayItems(e.OriginalSource as DependencyObject); }
        else if (e.Key == Key.Enter || e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        { e.Handled = true; await SafeAsync(CopySelectionAsync); }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            var selected = SelectedCards();
            await SafeAsync(() => ConfirmDeleteAsync(selected));
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        StopEntranceWork();
        _contentLifetime.Cancel();
        _statusTimer.Stop(); _statusTimer.Tick -= StatusTimer_Tick;
        _ageTimer.Stop();
        _ageTimer.Tick -= AgeTimer_Tick;
        ClearSelectionContainers();
        StopCategoryScrolling();
        CancelThumbnailLoads(); CancelCardPositionAnimation();
        _realizedCardElements.Clear(); _arrivingCards.Clear();
        _searchTimer.Stop(); _searchCancellation?.Cancel();
        if (_controller != null)
        {
            _controller.Store.Changed -= StoreChanged;
            _controller.ApprovalRequired -= RequestApproval;
            _controller.StatusChanged -= StatusChanged;
            _controller.PersonalLocked -= HidePrivateContent;
            _controller.ImportProgress -= OnImportProgress;
        }
        HidePrivateContent(); _cards.Clear(); _approvals.Clear();
        foreach (var menu in _openMenus.ToArray()) menu.IsOpen = false;
        _openMenus.Clear();
        TrayDropBorder?.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
        TrayDropBorder?.BeginAnimation(OpacityProperty, null);
        _searchCancellation?.Dispose();
        _searchCancellation = null;
        _contentLifetime.Dispose();
    }

    private void Tray_Unloaded(object sender, RoutedEventArgs e)
    {
        StopEntranceWork();
        CancelPendingPersonalDrop();
        TrayDropBorder?.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
        TrayDropBorder?.BeginAnimation(OpacityProperty, null);
        StopCategoryScrolling(); CancelCardPositionAnimation(); CancelThumbnailLoads();
        _realizedCardElements.Clear(); _arrivingCards.Clear();
        _searchTimer.Stop(); _queryDirty = true;
        HidePrivateContent();
        if (_controller?.Store.IsPersonalUnlocked == true) _ = SafeAsync(_controller.LockPersonalAsync);
    }
}
