using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace VNotch.Services.Clipboard;

public sealed partial class ClipboardHistoryStore
{
    private sealed record ImagePreview(BitmapSource Image, long Bytes, long LastAccess);
    private const long ImagePreviewBudget = 8 * 1024 * 1024;
    private const int ImagePreviewLimit = 24;
    // Access is serialized by Run(), like the entry files. Personal/sensitive
    // previews stay viewport-owned and never enter this shared cache.
    private readonly Dictionary<Guid, ImagePreview> _imagePreviews = [];
    private long _imagePreviewBytes, _imagePreviewAccess;

    internal Task<BitmapSource?> GetImagePreviewAsync(Guid id, CancellationToken cancellation = default) => Run(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var entry = Find(id);
        bool cacheable = !entry.IsPersonal && !entry.IsSensitive;
        if (cacheable && _imagePreviews.TryGetValue(id, out var cached))
        {
            _imagePreviews[id] = cached with { LastAccess = ++_imagePreviewAccess };
            return cached.Image;
        }
        string path = Path.Combine(EntryDirectory(entry), "image.png" + (entry.IsPersonal ? ".enc" : ""));
        if (!File.Exists(path)) return null;
        byte[]? personalBytes = null;
        BitmapSource image;
        try
        {
            if (entry.IsPersonal)
            {
                personalBytes = _vault.Open(File.ReadAllBytes(path));
                image = ClipboardImagePreview.Decode(personalBytes, true, cancellation);
                EnsurePersonalUnlocked();
            }
            else
            {
                // Public originals are streamed/decrypted directly without materializing
                // a full-size uncompressed bitmap on the managed heap.
                using var input = OpenPublicBlobRead(path);
                image = ClipboardImagePreview.Decode(input, entry.IsSensitive, cancellation);
            }
        }
        finally { if (personalBytes != null) CryptographicOperations.ZeroMemory(personalBytes); }
        cancellation.ThrowIfCancellationRequested();
        if (cacheable)
        {
            long bytes = (long)((image.PixelWidth * image.Format.BitsPerPixel + 7) / 8) * image.PixelHeight;
            while (_imagePreviews.Count > 0 && (_imagePreviews.Count >= ImagePreviewLimit || _imagePreviewBytes + bytes > ImagePreviewBudget))
                RemoveImagePreview(_imagePreviews.MinBy(pair => pair.Value.LastAccess).Key);
            if (bytes <= ImagePreviewBudget)
            {
                _imagePreviews[id] = new ImagePreview(image, bytes, ++_imagePreviewAccess);
                _imagePreviewBytes += bytes;
            }
        }
        return image;
    });

    private void RemoveImagePreview(Guid id)
    {
        if (_imagePreviews.Remove(id, out var preview)) _imagePreviewBytes -= preview.Bytes;
    }
}
