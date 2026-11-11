using System.IO;
using System.Windows.Media.Imaging;

namespace VNotch.Services.Clipboard;

internal static class ClipboardOriginalImage
{
    internal static async Task<BitmapSource> DecodeAsync(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, writable: false);
        return await DecodeAsync(input).ConfigureAwait(false);
    }

    // The caller owns the stream until this task completes. OnLoad detaches the
    // frozen, full-resolution bitmap before the original bytes can be wiped.
    internal static Task<BitmapSource> DecodeAsync(Stream input, CancellationToken cancellation = default) => Task.Run<BitmapSource>(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = input;
        bitmap.EndInit();
        bitmap.Freeze();
        cancellation.ThrowIfCancellationRequested();
        return bitmap;
    }, cancellation);
}
