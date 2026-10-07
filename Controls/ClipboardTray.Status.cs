using System.Windows;
using System.Windows.Threading;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    private void StatusChanged(string message)
    {
        if (_disposed) return;
        // Repeated clipboard-busy reports must not keep extending the same toast.
        if (_statusTimer.IsEnabled && StatusText.Text == message) return;
        StatusText.Text = message;
        ShowTransientLayer(StatusPanel);
        StatusPanel.IsHitTestVisible = false;
        _statusTimer.Stop(); _statusTimer.Start();
    }

    private void StatusTimer_Tick(object? sender, EventArgs e) => ClearStatus();
    private void ClearStatus(bool immediately = false)
    {
        _statusTimer.Stop();
        DismissTransientLayer(StatusPanel, () => StatusText.Text = "", immediately);
    }
}
