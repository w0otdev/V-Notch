using VNotch.Controls;

namespace VNotch;

public partial class MainWindow
{
    private MarqueeController? _marqueeController;

    private MarqueeController Marquee => _marqueeController ??= new MarqueeController(
        TrackTitleLayer, TrackTitle, TitleMarqueeTranslate, TitleMorphTranslate,
        TrackTitleNextLayer, TrackTitleNext, TitleMarqueeTranslateNext, TitleMorphTranslateNext,
        TrackArtistLayer, TrackArtist, ArtistMarqueeTranslate, ArtistMorphTranslate,
        TrackArtistNextLayer, TrackArtistNext, ArtistMarqueeTranslateNext, ArtistMorphTranslateNext,
        CompactTitleMarquee, CompactTitleMarqueeTranslate,
        GetVisibleMediaTextWidth);

    private void RefreshMediaMarquee() => Marquee.RefreshMediaMarquee();
    private void TransitionTrackText(string title, string artist) => Marquee.TransitionTrackText(title, artist);
    private void UpdateTitleText(string newText) => Marquee.UpdateTitleText(newText);
    private void UpdateArtistText(string newText) => Marquee.UpdateArtistText(newText);
}
