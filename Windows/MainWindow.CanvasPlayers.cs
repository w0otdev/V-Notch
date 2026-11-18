using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VNotch;

public partial class MainWindow
{
    // Called only when the Canvas controller is first needed. Creating these in
    // XAML loads the native media stack for every compact/background session.
    private (MediaElement Video, MediaElement VideoAlt) CreateSpotifyCanvasPlayers()
    {
        var video = CreateCanvasPlayer(1);
        var videoAlt = CreateCanvasPlayer(0);
        LyricsCanvasViewport.Children.Insert(0, video);
        LyricsCanvasViewport.Children.Insert(1, videoAlt);
        return (video, videoAlt);
    }

    private static MediaElement CreateCanvasPlayer(double opacity) => new()
    {
        LoadedBehavior = MediaState.Manual,
        UnloadedBehavior = MediaState.Manual,
        Stretch = Stretch.UniformToFill,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsMuted = true,
        ScrubbingEnabled = true,
        Opacity = opacity
    };
}
