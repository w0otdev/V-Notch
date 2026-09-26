using System.IO;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

internal sealed class ScreenshotFileStore
{
    private readonly string _root;
    public ScreenshotFileStore(string? root = null) => _root = root ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-Notch", "Screenshots");

    // Unused previews stay in memory. Only an explicit drag or Keep action writes a PNG.
    public string Save(BitmapSource image, bool keep)
    {
        string directory = Path.Combine(_root, keep ? "Saved" : "Temporary");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"Screenshot-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
            return path;
        }
        catch
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    public void CleanExpiredExports()
    {
        string directory = Path.Combine(_root, "Temporary");
        if (!Directory.Exists(directory)) return;
        try
        {
            // Exported files survive dismissal and app exit so drop targets can read them later.
            foreach (string path in Directory.EnumerateFiles(directory, "Screenshot-*.png"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1)) File.Delete(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
