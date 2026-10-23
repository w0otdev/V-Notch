using System.IO;
using System.Net;
using System.Net.Http;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.Clipboard;
using Xunit;

namespace VNotch.Tests;

public sealed class ClipboardDropPrivacyTests
{
    private static readonly ClipboardDropReader.Payload RemoteImage = new(new ClipboardCapture(), new Uri("https://example.com/image.png"));

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task PrivacyDenialStopsTheRequestBeforeTransport(bool localOnly, bool artwork)
    {
        var privacy = new NetworkPrivacy();
        privacy.Apply(new NotchSettings { EnableLocalOnlyMode = localOnly, EnableOnlineArtworkLookup = artwork });
        var transport = new Transport(() => new ByteArrayContent([]));
        using var client = ClipboardDropReader.CreateClient(transport, privacy);

        await Assert.ThrowsAsync<HttpRequestException>(() => ClipboardDropReader.ResolveAsync(RemoteImage, client: client));
        Assert.Equal(0, transport.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OversizedImagesAreRejectedWithOrWithoutContentLength(bool knownLength)
    {
        var privacy = OnlinePrivacy();
        var transport = new Transport(() =>
        {
            var content = new StreamContent(new SizedStream(20 * 1024 * 1024 + 1));
            if (knownLength) content.Headers.ContentLength = 20 * 1024 * 1024 + 1;
            else content.Headers.ContentLength = null;
            return content;
        });
        using var client = ClipboardDropReader.CreateClient(transport, privacy);
        await Assert.ThrowsAsync<InvalidDataException>(() => ClipboardDropReader.ResolveAsync(RemoteImage, client: client));
        Assert.Equal(1, transport.Requests);
    }

    [Fact]
    public async Task RevokingPrivacyCancelsResponseBodyReadsAfterHeaders()
    {
        var privacy = OnlinePrivacy();
        using var body = new WaitingStream();
        using var client = ClipboardDropReader.CreateClient(new Transport(() => new StreamContent(body)), privacy);
        var download = ClipboardDropReader.ResolveAsync(RemoteImage, client: client);
        await body.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        privacy.Apply(new NotchSettings { EnableLocalOnlyMode = true });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static NetworkPrivacy OnlinePrivacy()
    {
        var privacy = new NetworkPrivacy();
        privacy.Apply(new NotchSettings { EnableLocalOnlyMode = false, EnableOnlineArtworkLookup = true });
        return privacy;
    }

    private sealed class Transport(Func<HttpContent> content) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content() });
        }
    }

    private sealed class WaitingStream : Stream
    {
        internal readonly TaskCompletionSource ReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SizedStream(int remaining) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = Math.Min(buffer.Length, remaining);
            buffer.Span[..read].Clear();
            remaining -= read;
            return ValueTask.FromResult(read);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).Result;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
