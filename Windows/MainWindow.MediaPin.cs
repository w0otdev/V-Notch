using System.Windows.Input;

namespace VNotch;

public partial class MainWindow
{
    private void Thumbnail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_currentMediaInfo != null && _mediaService.ToggleSessionPin(_currentMediaInfo))
            RefreshMediaPinBadges(animate: true);
    }

    private void SynchronizeMediaPinForTransition()
    {
        RefreshMediaPinBadges();
        CompactMediaPin.FinishAnimation();
        ExpandedMediaPin.FinishAnimation();
        AnimationMediaPin.FinishAnimation();
    }

    private void RefreshMediaPinBadges(bool animate = false)
    {
        bool pinned = _mediaService.IsSessionPinned(_currentMediaInfo?.SessionInstanceKey);
        CompactMediaPin.SetPinned(pinned, animate);
        ExpandedMediaPin.SetPinned(pinned, animate);
        AnimationMediaPin.SetPinned(pinned, false);
    }
}
