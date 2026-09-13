using System;

namespace VNotch.Controllers;

// Worker-owned snapshot of the last submitted pixels. Span equality uses the
// runtime's vectorized comparison and checks every byte, including tiny motion
// between the fingerprint's sample positions.
internal sealed class GlassFrameHistory
{
    private byte[] _pixels = Array.Empty<byte>();
    private int _width, _height;

    internal void Clear()
    {
        _pixels = Array.Empty<byte>();
        _width = _height = 0;
    }

    internal unsafe bool IsUnchanged(IntPtr pixels, int width, int height)
    {
        if (pixels == IntPtr.Zero || width <= 0 || height <= 0 ||
            width != _width || height != _height) return false;
        int length = checked(width * height * 4);
        return new ReadOnlySpan<byte>((void*)pixels, length).SequenceEqual(_pixels.AsSpan(0, length));
    }

    internal unsafe void Commit(IntPtr pixels, int width, int height)
    {
        int length = checked(width * height * 4);
        if (_pixels.Length < length) _pixels = new byte[length];
        new ReadOnlySpan<byte>((void*)pixels, length).CopyTo(_pixels);
        _width = width;
        _height = height;
    }
}
