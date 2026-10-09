using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using VNotch.Services;

namespace VNotch;

public partial class TranslationWindow
{
    private static readonly DependencyProperty PopupPlacementProgressProperty = DependencyProperty.Register(
        "PopupPlacementProgress", typeof(double), typeof(TranslationWindow),
        new PropertyMetadata(0d, (owner, e) => ((TranslationWindow)owner).ApplyPopupPlacement((double)e.NewValue)));
    private bool _placementAnimating;
    private int _placementMotionVersion;
    private Point _placementFrom, _placementTarget;

    private void MovePopupTo(Point target, bool animate)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Win32Interop.GetWindowRect(hwnd, out var rect)) return;
        target = new Point(Math.Round(target.X), Math.Round(target.Y));
        if (_placementAnimating && target == _placementTarget) return;
        CancelPopupPlacement();
        _placementFrom = new Point(rect.Left, rect.Top);
        _placementTarget = target;
        if (target == _placementFrom) return;
        if (!animate || AnimationConfig.ReduceMotion || !SystemParameters.ClientAreaAnimation)
        {
            SetPopupPosition(target);
            return;
        }

        // Re-target from the live HWND position, including when a result or drag interrupts a move.
        SetValue(PopupPlacementProgressProperty, 1d);
        int version = _placementMotionVersion;
        _placementAnimating = true;
        var spring = Motion(0, 1, 400, AnimationPrimitives._easeMoveSpring);
        spring.FillBehavior = FillBehavior.HoldEnd;
        spring.Completed += (_, _) => FinishPopupPlacement(version);
        BeginAnimation(PopupPlacementProgressProperty, spring);
    }

    private void ApplyPopupPlacement(double progress)
    {
        if (!_placementAnimating || !IsVisible || _dismissing || _draggingPopup) return;
        SetPopupPosition(_placementFrom + (_placementTarget - _placementFrom) * progress);
    }

    private void SetPopupPosition(Point position)
    {
        // Physical coordinates keep the HWND and its hit targets together on mixed-DPI displays.
        Win32Interop.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero,
            (int)Math.Round(position.X), (int)Math.Round(position.Y), 0, 0,
            Win32Interop.SWP_NOACTIVATE | Win32Interop.SWP_NOSIZE | Win32Interop.SWP_NOZORDER);
    }

    private void FinishPopupPlacement(int version)
    {
        if (!_placementAnimating || version != _placementMotionVersion) return;
        var target = _placementTarget;
        CancelPopupPlacement();
        if (IsVisible && !_dismissing && !_draggingPopup) SetPopupPosition(target);
    }

    private void CancelPopupPlacement()
    {
        // Disable the callback before removing the clock, so its base value cannot move the window back.
        _placementAnimating = false;
        ++_placementMotionVersion;
        BeginAnimation(PopupPlacementProgressProperty, null);
    }
}
