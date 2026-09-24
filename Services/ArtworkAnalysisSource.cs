using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static class ArtworkAnalysisSource
{
    private static readonly ConditionalWeakTable<BitmapSource, BitmapSource> FormattedSources = new();

    internal static BitmapSource GetBgra32(BitmapSource source)
    {
        if (source.Format == PixelFormats.Bgra32) return source;
        // Only immutable inputs may share a converted source across the UI and blur worker.
        if (!source.IsFrozen) return new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        return FormattedSources.GetValue(source, static bitmap =>
        {
            var formatted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
            formatted.Freeze();
            return formatted;
        });
    }
}
