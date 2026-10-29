using System;
using System.Runtime.InteropServices;

namespace VNotch.Controllers;

// Worker-owned full-frame fingerprint. Every byte participates, including motion
// between the old sampled rows. Retain the digest instead of a second bitmap.
internal sealed partial class GlassFrameHistory
{
    private UInt128 _committedHash, _checkedHash;
    private int _width, _height, _checkedWidth, _checkedHeight;
    private bool _hasFrame, _hasCheckedFrame;
#pragma warning disable S2325
    internal int RetainedPixelBytes => 0;
#pragma warning restore S2325

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

    internal static unsafe UInt128 ComputeHash(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return default;
        fixed (byte* ptr = data)
        {
            vn_glass_frame_hash_128(ptr, data.Length, out ulong lo, out ulong hi);
            return new UInt128(hi, lo);
        }
    }

    [LibraryImport("VNotch.Native.dll")]
    private static unsafe partial int vn_glass_frame_hash_128(
        byte* data, int length, out ulong out_lo, out ulong out_hi);
}
