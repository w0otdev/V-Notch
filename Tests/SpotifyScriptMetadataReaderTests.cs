using System.IO;
using System.Text;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotifyScriptMetadataReaderTests
{
    private const int Limit = 8 * 1024 * 1024;

    [Theory]
    [InlineData(8188, 8192)]
    [InlineData(0, 1)]
    [InlineData(8190, 7)]
    public async Task FindsMetadataAcrossBlockAndTransportBoundaries(int offset, int fragmentSize)
    {
        string hash = new('a', 64);
        using var stream = new ScriptStream(offset, "'canvas' , 'query' , '" + hash + "'", offset + 512, fragmentSize);
        Assert.Equal(hash, await SpotifyScriptMetadataReader.ReadHashAsync(
            stream, SpotifyScriptMetadataReader.CanvasPattern, Limit, CancellationToken.None));
    }

    [Fact]
    public async Task ScanningEightMegabytesAllocatesOnlySmallBuffers()
    {
        string metadata = "\"findTracks\",\"query\",\"" + new string('b', 64) + "\"";
        using var warmup = new ScriptStream(0, metadata, metadata.Length);
        await SpotifyScriptMetadataReader.ReadHashAsync(warmup,
            SpotifyScriptMetadataReader.FindTracksPattern, Limit, CancellationToken.None);

        using var stream = new ScriptStream(Limit - metadata.Length, metadata, Limit);
        long before = GC.GetAllocatedBytesForCurrentThread();
        string? hash = await SpotifyScriptMetadataReader.ReadHashAsync(stream,
            SpotifyScriptMetadataReader.FindTracksPattern, Limit, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new string('b', 64), hash);
        Assert.True(allocated < 512 * 1024, $"Allocated {allocated:N0} bytes for an 8 MB script.");
        Assert.InRange(stream.LargestReadRequest, 1, 8192);
    }

    [Fact]
    public async Task StopsReadingAfterFindingTheHash()
    {
        using var stream = new ScriptStream(10, "'canvas','query','" + new string('c', 64) + "'", Limit * 4L);
        Assert.NotNull(await SpotifyScriptMetadataReader.ReadHashAsync(
            stream, SpotifyScriptMetadataReader.CanvasPattern, Limit, CancellationToken.None));
        Assert.True(stream.BytesRead <= 8192);
    }

    [Fact]
    public async Task UnknownLengthBodyCannotExceedTheDownloadLimit()
    {
        using var stream = new ScriptStream(0, "", Limit * 4L);
        Assert.Null(await SpotifyScriptMetadataReader.ReadHashAsync(
            stream, SpotifyScriptMetadataReader.CanvasPattern, Limit, CancellationToken.None));
        Assert.Equal(Limit + 1L, stream.BytesRead);
    }

    [Fact]
    public async Task CancelledScanDoesNotReadTheBody()
    {
        using var stream = new ScriptStream(0, "", Limit);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpotifyScriptMetadataReader.ReadHashAsync(
            stream, SpotifyScriptMetadataReader.CanvasPattern, Limit, cts.Token));
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData(8188, 8192)]
    [InlineData(8188, 1)]
    [InlineData(16340, 7)]
    public async Task ReadsBothHashesAcrossBlockAndTransportBoundaries(int offset, int fragmentSize)
    {
        string catalog = new('d', 64);
        string canvas = new('e', 64);
        string body = $"'findTracks','query','{catalog}';'canvas','query','{canvas}'";
        using var stream = new ScriptStream(offset, body, offset + body.Length, fragmentSize);

        var hashes = await SpotifyScriptMetadataReader.ReadMetadataAsync(stream, Limit, CancellationToken.None);

        Assert.Equal(catalog, hashes?.FindTracksHash);
        Assert.Equal(canvas, hashes?.CanvasHash);
    }

    [Fact]
    public async Task ReadsBothHashesNearTheLimitWithBoundedAllocations()
    {
        string body = $"'findTracks','query','{new string('d', 64)}';'canvas','query','{new string('e', 64)}'";
        using var warmup = new ScriptStream(0, body, body.Length);
        await SpotifyScriptMetadataReader.ReadMetadataAsync(warmup, Limit, CancellationToken.None);
        using var stream = new ScriptStream(Limit - body.Length, body, Limit);
        long before = GC.GetAllocatedBytesForCurrentThread();

        var hashes = await SpotifyScriptMetadataReader.ReadMetadataAsync(stream, Limit, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(hashes?.FindTracksHash);
        Assert.NotNull(hashes?.CanvasHash);
        Assert.InRange(allocated, 1, 512 * 1024);
        Assert.InRange(stream.LargestReadRequest, 1, 8192);
    }

    [Fact]
    public async Task ReadsMetadataFromBundleContainingOnlyOneOperation()
    {
        string hash = new('f', 64);
        string body = $"'canvas','query','{hash}'";
        using var stream = new ScriptStream(0, body, body.Length);

        var hashes = await SpotifyScriptMetadataReader.ReadMetadataAsync(stream, Limit, CancellationToken.None);

        Assert.Null(hashes?.FindTracksHash);
        Assert.Equal(hash, hashes?.CanvasHash);
    }

    [Fact]
    public async Task MetadataScan_RejectsUnknownLengthBodyOverTheLimit()
    {
        using var stream = new ScriptStream(0, "", Limit * 4L);
        Assert.Null(await SpotifyScriptMetadataReader.ReadMetadataAsync(stream, Limit, CancellationToken.None));
        Assert.Equal(Limit + 1L, stream.BytesRead);
    }

    // Generate the large response in place; the test itself needs no large array.
    private sealed class ScriptStream(int offset, string metadata, long length, int fragmentSize = 8192) : Stream
    {
        private readonly byte[] _metadata = Encoding.ASCII.GetBytes(metadata);
        public long BytesRead { get; private set; }
        public int LargestReadRequest { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestReadRequest = Math.Max(LargestReadRequest, buffer.Length);
            int count = (int)Math.Min(Math.Min(buffer.Length, fragmentSize), length - BytesRead);
            var destination = buffer.Span[..count];
            destination.Fill((byte)'x');
            long start = Math.Max(BytesRead, offset);
            long end = Math.Min(BytesRead + count, offset + _metadata.Length);
            if (end > start)
                _metadata.AsSpan((int)(start - offset), (int)(end - start))
                    .CopyTo(destination[(int)(start - BytesRead)..]);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
