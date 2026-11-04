using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace VNotch.Services.Clipboard;

// Seekable, zero-filled plaintext scratch storage outside the managed heap.
// SafeBuffer owns and wipes the allocation even if a serializer throws.
internal sealed unsafe class NativeBufferStream : Stream
{
    private sealed class Buffer : SafeBuffer
    {
        internal int Capacity { get; }
        internal Buffer(int capacity) : base(true)
        {
            Capacity = capacity;
            SetHandle((nint)NativeMemory.AllocZeroed((nuint)capacity));
            if (IsInvalid) throw new OutOfMemoryException();
            Initialize((ulong)capacity);
        }
        protected override bool ReleaseHandle()
        {
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)handle, Capacity));
            NativeMemory.Free((void*)handle); return true;
        }
    }
    private Buffer? _buffer;
    private long _length, _position;
    private bool _disposed;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => !_disposed;
    public override long Length { get { ThrowIfDisposed(); return _length; } }
    public override long Position { get { ThrowIfDisposed(); return _position; } set { ThrowIfDisposed(); ArgumentOutOfRangeException.ThrowIfNegative(value); _position = value; } }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private void EnsureCapacity(long length)
    {
        ThrowIfDisposed();
        if (length > int.MaxValue) throw new IOException("Clipboard scratch buffer exceeds its size limit.");
        if (length <= (_buffer?.Capacity ?? 0)) return;
        int capacity = (int)Math.Max(length, Math.Min(int.MaxValue, Math.Max(65536L, (_buffer?.Capacity ?? 0) * 2L)));
        var next = new Buffer(capacity);
        if (_buffer != null)
        {
            byte* source = null; byte* destination = null;
            try { _buffer.AcquirePointer(ref source); next.AcquirePointer(ref destination); new ReadOnlySpan<byte>(source, (int)_length).CopyTo(new Span<byte>(destination, capacity)); }
            catch { next.Dispose(); throw; }
            finally { if (source != null) _buffer.ReleasePointer(); if (destination != null) next.ReleasePointer(); }
            _buffer.Dispose();
        }
        _buffer = next;
    }
    public override void Flush() => ThrowIfDisposed();
    internal delegate T BufferReader<T>(ReadOnlySpan<byte> bytes);
    internal T ReadBuffered<T>(BufferReader<T> read)
    {
        ThrowIfDisposed();
        if (_length == 0) return read(default);
        byte* data = null;
        try { _buffer!.AcquirePointer(ref data); return read(new ReadOnlySpan<byte>(data, (int)_length)); }
        finally { if (data != null) _buffer!.ReleasePointer(); }
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ThrowIfDisposed();
        int count = (int)Math.Min(buffer.Length, Math.Max(0, _length - _position)); if (count == 0) return 0;
        byte* source = null;
        try { _buffer!.AcquirePointer(ref source); new ReadOnlySpan<byte>(source + _position, count).CopyTo(buffer); _position += count; return count; }
        finally { if (source != null) _buffer!.ReleasePointer(); }
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfDisposed(); if (buffer.IsEmpty) return;
        long end = checked(_position + buffer.Length); EnsureCapacity(end);
        byte* destination = null;
        try { _buffer!.AcquirePointer(ref destination); buffer.CopyTo(new Span<byte>(destination + _position, buffer.Length)); _position = end; _length = Math.Max(_length, end); }
        finally { if (destination != null) _buffer!.ReleasePointer(); }
    }
    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(_position + offset), SeekOrigin.End => checked(_length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        return _position;
    }
    public override void SetLength(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value); EnsureCapacity(value);
        if (value < _length)
        {
            byte* data = null;
            try { _buffer!.AcquirePointer(ref data); CryptographicOperations.ZeroMemory(new Span<byte>(data + value, (int)(_length - value))); }
            finally { if (data != null) _buffer!.ReleasePointer(); }
        }
        _length = value; _position = Math.Min(_position, value);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _buffer?.Dispose(); _buffer = null; }
        base.Dispose(disposing);
    }
}
