using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class TranslationWindow
{
    private bool _anchorPlacementPending, _expansionPending, _draggingPopup;
    private Size _expansionStartSize;
    private ScaleTransform? _expansionScale;
    private Point _dragStart, _dragOrigin;

    [LibraryImport("VNotch.Native")]
    private static partial int vn_translation_compute_popup_limits(
        double workHeight, double dpiScaleY, out double outMaxHeight, out double outPanelMaxHeight);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_translation_compute_viewport_height(
        double panelMaxHeight, double chromeHeight, double currentMaxHeight, out double outHeight, out byte outChanged);

    [LibraryImport("VNotch.Native")]
    private static partial int vn_translation_compute_drag_position(
        double originX, double originY, double startX, double startY, double screenX, double screenY,
        out int outX, out int outY);

    private void PreparePopupExpansion()
    {
        _expansionStartSize = new Size(PopupSurface.ActualWidth, PopupSurface.ActualHeight);
        ResetPopupExpansion();
        double x = PopupOffset.X, opacity = PopupSurface.Opacity;
        PopupOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PopupSurface.BeginAnimation(OpacityProperty, null);
        PopupOffset.X = x;
        PopupSurface.Opacity = opacity;
        _expansionPending = true;
        PopupSurface.RenderTransformOrigin = new Point(0, 0);
        _expansionScale = new ScaleTransform(1, 1);
        PopupSurface.RenderTransform = new TransformGroup { Children = { _expansionScale, PopupOffset } };
        // Prepare before changing the layout; the loaded callback measures the final size before rendering.
        _expansionScale.ScaleX = _expansionStartSize.Width / Math.Max(1, 536 - 52);
    }

    private void AnimatePopupExpansion()
    {
        _expansionPending = false;
        if (_expansionScale == null) return;
        double x = _expansionStartSize.Width / Math.Max(1, PopupSurface.ActualWidth);
        double y = _expansionStartSize.Height / Math.Max(1, PopupSurface.ActualHeight);
        _expansionScale.ScaleX = _expansionScale.ScaleY = 1;
        if (AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            PopupOffset.X = 0;
            PopupSurface.Opacity = 1;
            CompletePopupMotion(_motionVersion);
            return;
        }
        int version = _motionVersion;
        // The shared physical step response starts at rest, accelerates, then settles with restrained overshoot.
        var width = Motion(x, 1, 400, AnimationPrimitives._easeSpring);
        var height = Motion(y, 1, 400, AnimationPrimitives._easeSpring);
        width.FillBehavior = height.FillBehavior = FillBehavior.Stop;
        height.Completed += (_, _) => CompletePopupMotion(version);
        _expansionScale.BeginAnimation(ScaleTransform.ScaleXProperty, width);
        _expansionScale.BeginAnimation(ScaleTransform.ScaleYProperty, height);
        var slide = Motion(PopupOffset.X, 0, 400, AnimationPrimitives._easeSpring);
        slide.FillBehavior = FillBehavior.Stop;
        PopupOffset.X = 0;
        PopupOffset.BeginAnimation(TranslateTransform.XProperty, slide);
        var fade = Motion(PopupSurface.Opacity, 1, 180);
        fade.FillBehavior = FillBehavior.Stop;
        PopupSurface.Opacity = 1;
        PopupSurface.BeginAnimation(OpacityProperty, fade);
    }

    private void ResetPopupExpansion()
    {
        _expansionPending = false;
        _expansionScale?.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _expansionScale?.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _expansionScale = null;
        PopupSurface.RenderTransform = PopupOffset;
        PopupSurface.RenderTransformOrigin = new Point(0, 0);
    }

    private void PopupMotionPreferenceChanged()
    {
        if (AnimationConfig.ReduceMotion && _placementAnimating) FinishPopupPlacement(_placementMotionVersion);
        if (!AnimationConfig.ReduceMotion || !_popupAnimating || _dismissing || _expansionScale == null) return;
        _expansionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _expansionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _expansionScale.ScaleX = _expansionScale.ScaleY = 1;
        PopupOffset.BeginAnimation(TranslateTransform.XProperty, null);
        PopupSurface.BeginAnimation(OpacityProperty, null);
        PopupOffset.X = 0;
        PopupSurface.Opacity = 1;
        _expansionPending = _entrancePending = false;
        CompletePopupMotion(_motionVersion);
    }

    private void UpdatePopupLimits()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !Win32Interop.GetWindowRect(hwnd, out _)) return;
        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var work = screen.WorkingArea;
        _workArea = new Rect(work.X, work.Y, work.Width, work.Height);
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        // Use the monitor's available height. Reposition the menu to fit instead of nesting scroll areas.
        if (vn_translation_compute_popup_limits(work.Height, scale, out double maxHeight, out double panelMaxHeight) != 0)
            return;
        MaxHeight = maxHeight;
        TranslationPanel.MaxHeight = panelMaxHeight;
    }

    private bool UpdateResultViewportHeight()
    {
        double chrome = 0;
        var rows = TranslationPanel.RowDefinitions;
        for (int i = 0; i < rows.Count; i++)
            if (i != 3) chrome += rows[i].ActualHeight;
        if (vn_translation_compute_viewport_height(TranslationPanel.MaxHeight, chrome, ResultScroll.MaxHeight, out double height, out byte changed) != 0 || changed == 0)
            return false;
        ResultScroll.MaxHeight = height;
        return true;
    }

    private static Point PointerScreenPosition()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        return new Point(cursor.X, cursor.Y);
    }

    private void PopupDragStarted(object sender, MouseButtonEventArgs e)
    {
        // The close button retains its normal click; only the title and its empty space drag.
        for (var node = e.OriginalSource as DependencyObject; node != null && node != TranslationDragHandle; node = VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return;
        if (!BeginPopupDrag(PointerScreenPosition())) return;
        TranslationDragHandle.CaptureMouse();
        e.Handled = true;
    }

    private void PopupDragMoved(object sender, MouseEventArgs e)
    {
        if (!_draggingPopup) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPopupDrag(); return; }
        MovePopupDrag(PointerScreenPosition());
        e.Handled = true;
    }

    private void PopupDragEnded(object sender, MouseButtonEventArgs e)
    {
        if (!_draggingPopup) return;
        MovePopupDrag(PointerScreenPosition());
        EndPopupDrag();
        e.Handled = true;
    }

    private void PopupDragCaptureLost(object sender, MouseEventArgs e) => EndPopupDrag();

    internal bool BeginPopupDrag(Point screenPosition)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (_draggingPopup || !IsVisible || _dismissing || !Win32Interop.GetWindowRect(hwnd, out var rect)) return false;
        CancelPopupPlacement();
        _draggingPopup = true;
        _anchorPlacementPending = false;
        _dragStart = screenPosition;
        _dragOrigin = new Point(rect.Left, rect.Top);
        SourceLanguageCombo.IsDropDownOpen = TargetLanguageCombo.IsDropDownOpen = false;
        return true;
    }

    internal void MovePopupDrag(Point screenPosition)
    {
        if (!_draggingPopup) return;
        if (vn_translation_compute_drag_position(
            _dragOrigin.X, _dragOrigin.Y, _dragStart.X, _dragStart.Y, screenPosition.X, screenPosition.Y,
            out int x, out int y) != 0)
            return;
        Win32Interop.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            x, y, 0, 0, 0x0010 | 0x0001 | 0x0004);
    }

    internal void EndPopupDrag()
    {
        if (!_draggingPopup) return;
        _draggingPopup = false;
        if (TranslationDragHandle.IsMouseCaptured) TranslationDragHandle.ReleaseMouseCapture();
        UpdatePopupLimits();
        if (IsVisible && !_dismissing) SchedulePresentation();
    }
}
