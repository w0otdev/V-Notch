using System.IO;
using System.Buffers;
using System.Net.Http;

namespace VNotch.Services;

internal sealed class PooledUtf8Response : IDisposable
{
    private byte[]? _buffer;
    internal int Length { get; private set; }
    internal ReadOnlySpan<byte> Span => _buffer.AsSpan(0, Length);
    private PooledUtf8Response(int capacity) => _buffer = ArrayPool<byte>.Shared.Rent(capacity);

    internal static async Task<PooledUtf8Response> ReadAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellation)
    {
        long? declared = response.Content.Headers.ContentLength;
        if (maxBytes <= 0 || maxBytes == int.MaxValue || declared > maxBytes) throw new InvalidDataException("Response exceeds the size limit.");
        var result = new PooledUtf8Response(declared is >= 0 ? Math.Max(1, (int)declared.Value + 1) : Math.Min(8192, maxBytes + 1));
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] buffer = result._buffer!;
                if (result.Length == buffer.Length)
                {
                    int capacity = (int)Math.Min((long)maxBytes + 1, (long)buffer.Length * 2);
                    byte[] larger = ArrayPool<byte>.Shared.Rent(capacity);
                    buffer.AsSpan(0, result.Length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    result._buffer = buffer = larger;
                }
                int read = await stream.ReadAsync(buffer.AsMemory(result.Length, Math.Min(buffer.Length - result.Length, maxBytes - result.Length + 1)), cancellation).ConfigureAwait(false);
                if (read == 0) return result;
                result.Length += read;
                if (result.Length > maxBytes) throw new InvalidDataException("Response exceeds the size limit.");
            }
        }
        catch { result.Dispose(); throw; }
    }

    public void Dispose()
    {
        byte[]? buffer = Interlocked.Exchange(ref _buffer, null);
        if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
    }
}
