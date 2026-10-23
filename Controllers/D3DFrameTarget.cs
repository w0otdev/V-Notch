using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VNotch.Controllers;

/// <summary>The WPF back-buffer contract, without exposing compositor implementation details.</summary>
internal interface ID3DFrameTarget
{
    ImageSource ImageSource { get; }
    bool IsFrontBufferAvailable { get; }
    event DependencyPropertyChangedEventHandler IsFrontBufferAvailableChanged;
    // WPF requires Unlock after every non-throwing TryLock, including a timeout.
    bool TryLock(Duration timeout);
    void Lock();
    void Unlock();
    void SetBackBuffer(D3DResourceType type, IntPtr surface, bool enableSoftwareFallback = false);
    void AddDirtyRect(Int32Rect rectangle);
}

internal sealed class D3DFrameTarget : ID3DFrameTarget
{
    private readonly D3DImage _image = new();
    public ImageSource ImageSource => _image;
    public bool IsFrontBufferAvailable => _image.IsFrontBufferAvailable;
    public event DependencyPropertyChangedEventHandler IsFrontBufferAvailableChanged
    {
        add => _image.IsFrontBufferAvailableChanged += value;
        remove => _image.IsFrontBufferAvailableChanged -= value;
    }
    public bool TryLock(Duration timeout) => _image.TryLock(timeout);
    public void Lock() => _image.Lock();
    public void Unlock() => _image.Unlock();
    public void SetBackBuffer(D3DResourceType type, IntPtr surface, bool enableSoftwareFallback = false) =>
        _image.SetBackBuffer(type, surface, enableSoftwareFallback);
    public void AddDirtyRect(Int32Rect rectangle) => _image.AddDirtyRect(rectangle);
}
