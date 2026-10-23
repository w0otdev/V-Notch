using System;
using System.IO.Hashing;

namespace VNotch.Controllers;

// Worker-owned full-frame fingerprint. Every byte participates, including motion
// between the old sampled rows. Retain the digest instead of a second bitmap.
internal sealed class GlassFrameHistory
{
    private UInt128 _committedHash, _checkedHash;
    private int _width, _height, _checkedWidth, _checkedHeight;
    private bool _hasFrame, _hasCheckedFrame;
    internal int RetainedPixelBytes => 0;

    internal void Clear()
    {
        _hasFrame = _hasCheckedFrame = false;
    }

    internal unsafe bool IsUnchanged(IntPtr pixels, int width, int height)
    {
        _hasCheckedFrame = pixels != IntPtr.Zero && width > 0 && height > 0;
        if (!_hasCheckedFrame) return false;
        _checkedHash = ComputeHash(new ReadOnlySpan<byte>((void*)pixels, checked(width * height * 4)));
        _checkedWidth = width;
        _checkedHeight = height;
        return _hasFrame && width == _width && height == _height && _checkedHash == _committedHash;
    }

    internal void Commit(IntPtr pixels, int width, int height)
    {
        IsUnchanged(pixels, width, height);
        CommitCheckedFrame();
    }

    // Capture is stable between comparison and rendering on this worker.
    internal void CommitCheckedFrame()
    {
        if (!_hasCheckedFrame) return;
        _committedHash = _checkedHash;
        _width = _checkedWidth;
        _height = _checkedHeight;
        _hasFrame = true;
        _hasCheckedFrame = false;
    }

    // The runtime implementation uses SIMD where available. Hash the entire frame
    // once, with a 128-bit digest, and commit that checked digest after rendering.
    internal static UInt128 ComputeHash(ReadOnlySpan<byte> data) => XxHash128.HashToUInt128(data);
}
