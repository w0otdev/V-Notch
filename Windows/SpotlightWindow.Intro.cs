using System.Windows;
using VNotch.Services;

namespace VNotch;

public partial class SpotlightWindow
{
    internal event EventHandler? IntroDismissed;

    private void RefreshSpotlightIntro()
    {
        SpotlightIntroTitle.Text = Loc.Get("intro.title");
        SpotlightIntroHeadline.Text = Loc.Get("intro.headline");
        SpotlightIntroBody.Text = Loc.Get("intro.body");
        SpotlightIntroClose.Content = Loc.Get("intro.close");
        SpotlightIntroPanel.Visibility = _settings.HasSeenSpotlightIntro ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SpotlightIntroClose_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.HasSeenSpotlightIntro) return;
        _settings.HasSeenSpotlightIntro = true;
        RefreshSpotlightIntro();
        IntroDismissed?.Invoke(this, EventArgs.Empty);
        SearchBox.Focus();
    }
}
