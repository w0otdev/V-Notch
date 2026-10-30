using System.Windows;
using System.Windows.Controls;
using VNotch.Models;

namespace VNotch;

public partial class MainWindow
{
    private void PlaceSharedSettingsButton(NotchView view)
    {
        // Let the tray's toolbar lay out this same button alongside its actions.
        // Other expanded views keep the button in the shared corner host.
        bool inTray = view == NotchView.Secondary;
        SettingsButton.Width = SettingsButton.Height = inTray ? 28 : 24;
        SettingsButton.CornerRadius = new CornerRadius(inTray ? 14 : 12);
        ContentControl host = inTray
            ? ClipboardTrayView.SettingsButtonHost : SharedSettingsHost;
        if (ReferenceEquals(host.Content, SettingsButton)) return;

        if (SettingsButton.Parent is ContentControl previous)
        {
            previous.Content = null;
            previous.Visibility = Visibility.Collapsed;
        }
        host.Visibility = Visibility.Visible;
        host.Content = SettingsButton;
    }

    private void HideSharedSettingsButton()
    {
        SettingsButton.IsHitTestVisible = false;
        SettingsButton.Focusable = false;
        SettingsButton.Visibility = Visibility.Collapsed;
        SettingsButton.BeginAnimation(OpacityProperty, null);
        SettingsButton.Opacity = 0;
    }
}
