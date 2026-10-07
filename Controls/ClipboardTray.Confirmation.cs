using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private TaskCompletionSource<bool>? _deleteDecision;
    private int _deleteConfirmationCount;

    private async Task ConfirmDeleteAsync(ClipboardCardViewModel[] cards)
    {
        if (_controller == null || cards.Length == 0 || _deleteConfirmationOpen) return;
        _deleteConfirmationOpen = true;
        bool confirmed;
        try
        {
            _deleteDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _deleteConfirmationCount = cards.Length;
            DeleteConfirmationText.Text = Loc.Get("clipboard.deleteConfirm", _deleteConfirmationCount);
            TrayContentGrid.IsHitTestVisible = false;
            TrayMotion.SetBlurRadius(TrayContentGrid, 12.0);
            ShowTransientLayer(DeleteConfirmationPanel);
            AnimateDeleteConfirmationBorder();
            CancelDeleteButton.Focus();
            confirmed = await _deleteDecision.Task;
        }
        finally
        {
            // Only dismiss if nothing resolved the dialog yet (e.g. an exception).
            if (_deleteDecision != null) ResolveDeleteConfirmation(false);
            _deleteConfirmationOpen = false;
            TrayContentGrid.IsHitTestVisible = true;
            TrayMotion.SetBlurRadius(TrayContentGrid, 0.0);
        }

        // The dialog is closed; deletion I/O must not hold the confirmation gate.
        if (!confirmed || _disposed) return;
        // Block interaction with cards whose files are being removed.
        TrayContentGrid.IsHitTestVisible = false;
        try
        {
            foreach (var card in cards)
            {
                if (_disposed) return;
                if (_controller.Store.GetEntry(card.Entry.Id) != null) await _controller.Store.DeleteAsync(card.Entry.Id);
            }
        }
        finally
        {
            if (!_disposed && !_deleteConfirmationOpen) TrayContentGrid.IsHitTestVisible = true;
        }
    }

    private void ResolveDeleteConfirmation(bool confirmed, bool immediately = false)
    {
        bool restoreCardsFocus = _deleteDecision != null && !immediately &&
            !_disposed && !_trayLeaving && IsLoaded && IsVisible;
        DismissTransientLayer(DeleteConfirmationPanel, immediately: immediately);
        TrayContentGrid.IsHitTestVisible = true;
        TrayMotion.SetBlurRadius(TrayContentGrid, 0.0);
        _deleteDecision?.TrySetResult(confirmed);
        _deleteDecision = null;
        // Cancel takes focus into the dialog. Return it without touching the
        // selection so the next Delete/Ctrl+C routes through Cards_KeyDown.
        if (restoreCardsFocus)
        {
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(Cards), Cards);
            Cards.Focus();
        }
    }
    private void ConfirmDelete_Click(object sender, RoutedEventArgs e) => ResolveDeleteConfirmation(true);
    private void CancelDelete_Click(object sender, RoutedEventArgs e) => ResolveDeleteConfirmation(false);
    private void DeleteConfirmationBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DeleteConfirmationPanel))
        {
            ResolveDeleteConfirmation(false);
            e.Handled = true;
        }
    }
    private void Tray_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && SelectAllTrayItems(e.OriginalSource as DependencyObject))
        { e.Handled = true; return; }
        if (e.Key is Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Space)
            _pendingSelectAll = null;
        if (e.Key != Key.Escape || (!_deleteConfirmationOpen && DeleteConfirmationPanel.Visibility != Visibility.Visible)) return;
        ResolveDeleteConfirmation(false); e.Handled = true;
    }

    private void AnimateDeleteConfirmationBorder()
    {
        if (AnimationConfig.ReduceMotion) return;
        var brush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0x45, 0x3A));
        DeleteConfirmationBorder.BorderBrush = brush;
        var anim = new ColorAnimation
        {
            From = Color.FromArgb(0x35, 0xFF, 0x45, 0x3A),
            To = Color.FromArgb(0x95, 0xFF, 0x45, 0x3A),
            Duration = TimeSpan.FromMilliseconds(260),
            AutoReverse = true,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
    }
}
