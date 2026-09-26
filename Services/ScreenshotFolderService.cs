using System.IO;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VNotch.Services;

/// <summary>Watches new files only; never imports existing screenshot history.</summary>
internal sealed class ScreenshotFolderService : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<bool> _enabled;
    private readonly Func<string> _customFolders;
    private readonly DispatcherTimer _refresh;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    public event Action<BitmapSource>? Captured;

    public ScreenshotFolderService(Func<bool> enabled, Func<string> customFolders)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _enabled = enabled;
        _customFolders = customFolders;
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refresh.Tick += Refresh;
        Refresh(null, EventArgs.Empty);
        _refresh.Start();
    }

    internal static bool IsImagePath(string path) => Path.GetExtension(path).ToLowerInvariant()
        is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff";

    private IEnumerable<string> Folders()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Captures");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX", "Screenshots");
        // Shell folder values honor redirected/OneDrive screenshot and Game Bar folders.
        string[] shellFolders = { "{B7BEDE81-DF94-4682-A7D8-57A52620B86F}", "{EDC0FE71-98D8-4F4A-B920-C8DC133CB165}" };
        foreach (string name in shellFolders)
        {
            string? path = null;
            try { path = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", name, null) as string; }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            if (!string.IsNullOrWhiteSpace(path)) yield return Environment.ExpandEnvironmentVariables(path);
        }
        foreach (string path in _customFolders().Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            yield return Environment.ExpandEnvironmentVariables(path);
    }

    private void Refresh(object? sender, EventArgs e)
    {
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_enabled())
            foreach (string folder in Folders())
                try { if (Path.IsPathFullyQualified(folder) && Directory.Exists(folder)) wanted.Add(Path.GetFullPath(folder)); }
                catch (ArgumentException) { }
                catch (IOException) { }
        foreach (string old in _watchers.Keys.Except(wanted).ToArray())
        {
            _watchers[old].Dispose();
            _watchers.Remove(old);
        }
        foreach (string folder in wanted.Except(_watchers.Keys).ToArray())
        {
            try
            {
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
                watcher.Created += (_, args) => Schedule(args.FullPath);
                watcher.Renamed += (_, args) => Schedule(args.FullPath);
                watcher.Error += (_, _) => _dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_watchers.Remove(folder, out var failed)) failed.Dispose();
                }));
                _watchers.Add(folder, watcher);
                watcher.EnableRaisingEvents = true;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }
    }

    private void Schedule(string path)
    {
        if (!IsImagePath(path) || _dispatcher.HasShutdownStarted) return;
        string exports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-Notch", "Screenshots") + Path.DirectorySeparatorChar;
        if (path.StartsWith(exports, StringComparison.OrdinalIgnoreCase)) return;
        _dispatcher.BeginInvoke(new Action(() => ReadFileAsync(path)));
    }

    private async void ReadFileAsync(string path)
    {
        if (_disposed || !_enabled() || _pending.ContainsKey(path) || _pending.Count >= 32) return;
        using var cancellation = new CancellationTokenSource();
        _pending[path] = cancellation;
        try
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                await Task.Delay(250, cancellation.Token);
                if (!_enabled()) return;
                try
                {
                    var image = await Task.Run(() =>
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (stream.Length == 0 || stream.Length > 100 * 1024 * 1024) return null;
                        var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
                        bitmap.Freeze();
                        return bitmap;
                    }, cancellation.Token);
                    if (image == null) continue;
                    if (!_disposed && _enabled()) Captured?.Invoke(image);
                    return;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (NotSupportedException) { }
                catch (System.Runtime.InteropServices.ExternalException) { }
                catch (ArgumentException) { }
            }
        }
        catch (OperationCanceledException) { }
        finally { _pending.Remove(path); }
    }

    public void Dispose()
    {
        _disposed = true;
        _refresh.Stop();
        _refresh.Tick -= Refresh;
        foreach (var watcher in _watchers.Values) watcher.Dispose();
        _watchers.Clear();
        foreach (var pending in _pending.Values) pending.Cancel();
        Captured = null;
    }
}
