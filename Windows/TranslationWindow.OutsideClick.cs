using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

public partial class TranslationWindow
{
    private IDisposable? _outsideClickSubscription;
    private uint _outsideDismissOpenedAt, _outsidePopupOpenedAt, _outsidePopupClosedAt;
    private FrameworkElement? _outsidePopup;
    private Rect _outsidePopupBounds = Rect.Empty;
    private bool _outsidePopupOpen;

    private void InitializeOutsideDismissal()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { StopOutsideDismissal(); return; }
            ResetOutsideDismissal();
            _outsideClickSubscription ??= InputMonitorService.Subscribe(new WindowInteropHelper(this).EnsureHandle(),
                (point, timestamp) => HandleOutsidePointerPress(new Point(point.x, point.y), timestamp));
        };
        Closed += (_, _) => StopOutsideDismissal();
        foreach (var combo in new[] { SourceLanguageCombo, TargetLanguageCombo, SourceCurrencyCombo, TargetCurrencyCombo })
        {
            combo.DropDownOpened += (_, _) =>
            {
                if (combo.Template.FindName("PART_Popup", combo) is Popup { Child: FrameworkElement child })
                    TrackOutsidePopup(child);
            };
            combo.DropDownClosed += (_, _) => FreezeOutsidePopup();
        }
        ContextMenuOpening += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible || _dismissing) return;
            foreach (HwndSource source in PresentationSource.CurrentSources.OfType<HwndSource>())
            {
                if (source.Dispatcher != Dispatcher || source.RootVisual == null) continue;
                if (FindOwnedContextMenu(source.RootVisual) is not { } menu) continue;
                TrackOutsidePopup(menu);
                break;
            }
        }));
        ContextMenuClosing += (_, _) => FreezeOutsidePopup();
    }

    private ContextMenu? FindOwnedContextMenu(DependencyObject element)
    {
        if (element is ContextMenu { IsOpen: true, PlacementTarget: { } target } menu && Window.GetWindow(target) == this) return menu;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
            if (FindOwnedContextMenu(VisualTreeHelper.GetChild(element, i)) is { } child) return child;
        return null;
    }

    private void ResetOutsideDismissal()
    {
        _outsideDismissOpenedAt = unchecked((uint)Environment.TickCount);
    }

    private void StopOutsideDismissal()
    {
        _outsideClickSubscription?.Dispose();
        _outsideClickSubscription = null;
        FreezeOutsidePopup();
        _outsidePopupBounds = Rect.Empty;
    }

    private void TrackOutsidePopup(FrameworkElement popup)
    {
        FreezeOutsidePopup();
        _outsidePopup = popup;
        _outsidePopupOpen = true;
        _outsidePopupOpenedAt = unchecked((uint)Environment.TickCount);
        _outsidePopupBounds = Rect.Empty;
        popup.LayoutUpdated += UpdateOutsidePopupBounds;
        UpdateOutsidePopupBounds(null, EventArgs.Empty);
    }

    private void UpdateOutsidePopupBounds(object? sender, EventArgs e)
    {
        if (_outsidePopup is { IsVisible: true } popup && ScreenBounds(popup) is { IsEmpty: false } bounds)
            _outsidePopupBounds = bounds;
    }

    private void FreezeOutsidePopup()
    {
        if (!_outsidePopupOpen) return;
        UpdateOutsidePopupBounds(null, EventArgs.Empty);
        _outsidePopupClosedAt = unchecked((uint)Environment.TickCount);
        _outsidePopupOpen = false;
        if (_outsidePopup != null) _outsidePopup.LayoutUpdated -= UpdateOutsidePopupBounds;
        _outsidePopup = null;
    }

    private static Rect ScreenBounds(FrameworkElement element)
    {
        if (PresentationSource.FromVisual(element) == null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            return Rect.Empty;
        return new Rect(element.PointToScreen(new Point()),
            element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
    }

    internal void HandleOutsidePointerPress(Point screenPoint, uint timestamp)
    {
        if (!IsVisible || _dismissing || _draggingPopup || unchecked((int)(timestamp - _outsideDismissOpenedAt)) <= 0) return;
        if (ScreenBounds(PopupSurface).Contains(screenPoint)) return;
        UpdateOutsidePopupBounds(null, EventArgs.Empty);
        // The menu item can close its HWND before queued raw input reaches WPF.
        // Keep its last bounds only for clicks that happened while it was open.
        bool wasInPopup = _outsidePopupOpen ||
            (unchecked((int)(timestamp - _outsidePopupOpenedAt)) >= 0 && unchecked((int)(_outsidePopupClosedAt - timestamp)) >= 0);
        if (wasInPopup && _outsidePopupBounds.Contains(screenPoint)) return;
        Dismiss();
    }
}
