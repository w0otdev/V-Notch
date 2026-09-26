using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>Accepts bitmap/PNG clipboard output, including ownerless Print Screen captures.</summary>
internal sealed class ScreenshotCaptureService : IDisposable
{
    private readonly DispatcherTimer _retry = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private uint _sequence;
    private int _attempts;
    private bool _disposed;
    private bool _pending;
    private uint? _lastCapturedSequence;
    private readonly Func<string?> _getOwnerName;
    private readonly Func<uint> _getSequence;
    private readonly Func<BitmapSource?> _readImage;
    public event Action<BitmapSource>? Captured;

    public ScreenshotCaptureService(Func<string?>? getOwnerName = null,
        Func<uint>? getSequence = null, Func<BitmapSource?>? readImage = null)
    {
        _getOwnerName = getOwnerName ?? GetOwnerName;
        _getSequence = getSequence ?? GetClipboardSequenceNumber;
        _readImage = readImage ?? ReadClipboardImage;
        _retry.Tick += ReadImage;
    }

    internal static bool IsCaptureProcess(string? name) => name?.ToLowerInvariant() is
        "snippingtool" or "screenclippinghost" or "screensketch" or "sharex" or
        "greenshot" or "lightshot" or "snagit32" or "snagiteditor" or
        "flameshot" or "picpick" or "screenpresso" or "ksnip";

    public bool NotifyClipboardUpdated()
    {
        if (_disposed) return false;
        _retry.Stop();
        _pending = false;
        try
        {
            // The owner is not a reliable discriminator: Windows and third-party capture
            // tools may publish through a broker or without an owner window at all.
            _sequence = _getSequence();
            if (_sequence == _lastCapturedSequence) return true;
            _attempts = 0;
            _pending = true;
            _retry.Start();
            return false; // Preserve ordinary clipboard feedback while delayed image formats settle.
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? GetOwnerName()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero) return null;
        Win32Interop.GetWindowThreadProcessId(owner, out uint processId);
        using var process = Process.GetProcessById((int)processId);
        return process.ProcessName;
    }

    private static BitmapSource? ReadClipboardImage()
    {
        if (Clipboard.ContainsImage()) return Clipboard.GetImage();
        foreach (string format in new[] { "PNG", "image/png" })
        {
            if (!Clipboard.ContainsData(format)) continue;
            object data = Clipboard.GetData(format);
            if (data is MemoryStream stream)
            {
                using var copy = new MemoryStream(stream.ToArray());
                return BitmapDecoder.Create(copy, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
            }
            if (data is byte[] bytes)
            {
                using var copy = new MemoryStream(bytes);
                return BitmapDecoder.Create(copy, BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad).Frames[0];
            }
        }
        return null;
    }

    private void ReadImage(object? sender, EventArgs e) => ReadPendingClipboard();

    internal void ReadPendingClipboard()
    {
        if (!_pending) return;
        if (_disposed || _getSequence() != _sequence || ++_attempts > 5)
        {
            _pending = false;
            _retry.Stop();
            return;
        }
        try
        {
            // Clipboard producers can publish formats lazily. Retry without blocking the UI.
            var image = _readImage();
            if (image == null) return;
            if (_getSequence() != _sequence) return;
            image.Freeze();
            _lastCapturedSequence = _sequence;
            _pending = false;
            _retry.Stop();
            Captured?.Invoke(image);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException or NotSupportedException or IOException or ArgumentException)
        {
            // A busy clipboard is normal while the capture tool is publishing its image.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pending = false;
        _retry.Stop();
        _retry.Tick -= ReadImage;
        Captured = null;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
