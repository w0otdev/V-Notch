using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal static class ArtworkAnalysisSource
{
    private static readonly ConditionalWeakTable<BitmapSource, BitmapSource> FormattedSources = new();
    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<(int, int), BitmapSource>> Samples = new();

    internal static void ClearCache()
    {
        FormattedSources.Clear();
        Samples.Clear();
    }

    internal static BitmapSource GetSample(BitmapSource source, int width, int height)
    {
        width = Math.Clamp(width, 1, 512); height = Math.Clamp(height, 1, 512);
        if (!source.IsFrozen) return Build();
        var samples = Samples.GetValue(source, _ => new());
        lock (samples)
        {
            if (samples.TryGetValue((width, height), out var cached)) return cached;
            var image = Build();
            if (samples.Count < 8) samples[(width, height)] = image;
            return image;
        }
        BitmapSource Build()
        {
            BitmapSource scaled = source.PixelWidth == width && source.PixelHeight == height ? source :
                new TransformedBitmap(source, new ScaleTransform((double)width / source.PixelWidth, (double)height / source.PixelHeight));
            var copy = new WriteableBitmap(GetBgra32(scaled));
            copy.Freeze();
            return copy;
        }
    }

    internal static async Task<BitmapSource> GetFrozenSnapshotAsync(BitmapSource source)
    {
        // IsFrozen itself verifies access for mutable Freezables. Read it only
        // on the owner; frozen sources have no dispatcher and are safe to share.
        if (source.Dispatcher is not { } dispatcher || dispatcher.CheckAccess()) return Snapshot();
        return await dispatcher.InvokeAsync(Snapshot).Task.ConfigureAwait(false);

        BitmapSource Snapshot()
        {
            if (source.IsFrozen) return source;
            // Keep the caller's bitmap editable; only the worker's copy is frozen.
            var snapshot = source.CloneCurrentValue();
            snapshot.Freeze();
            return snapshot;
        }
    }

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
