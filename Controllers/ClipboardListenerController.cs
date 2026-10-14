using VNotch.Services;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

public sealed class ClipboardListenerController : IDisposable
{
    private readonly Func<IntPtr> _getHwnd;
    private readonly Action _onClipboardChanged;
    private bool _registered;

    public ClipboardListenerController(Func<IntPtr> getHwnd, Action onClipboardChanged)
    {
        _getHwnd = getHwnd;
        _onClipboardChanged = onClipboardChanged;
    }

    public void Start()
    {
        var hwnd = _getHwnd();
        if (!_registered && hwnd != IntPtr.Zero)
            _registered = AddClipboardFormatListener(hwnd);
    }

    public void NotifyClipboardUpdated()
    {
        _onClipboardChanged();
    }

    public void Dispose()
    {
        var hwnd = _getHwnd();
        if (_registered && hwnd != IntPtr.Zero)
            RemoveClipboardFormatListener(hwnd);
        _registered = false;
    }
}
