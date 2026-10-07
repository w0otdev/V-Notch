using System.IO;
using System.Security.Cryptography;

namespace VNotch.Services.Clipboard;

/// <summary>Hashes the exact archive/file bytes as they are written, without rereading the copy.</summary>
internal sealed class ClipboardHashingStream(Stream output, IncrementalHash hash) : Stream
{
    internal long BytesWritten { get; private set; }
    public override bool CanRead => false;
    // ZipArchive must use its forward-only layout so it never rewrites bytes already hashed.
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => BytesWritten;
    public override long Position { get => BytesWritten; set => throw new NotSupportedException(); }
    public override void Flush() => output.Flush();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    { output.Write(buffer); hash.AppendData(buffer); BytesWritten = checked(BytesWritten + buffer.Length); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
