using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using VNotch.Models;
using VNotch.Services.Spotlight;
using Xunit;

namespace VNotch.Tests;

public sealed class SpotlightAiStreamAndPrivacyTests
{
    [Fact]
    public async Task StreamAsync_NewlineLessTwoMegabyteStream_AbortsBeforeEof()
    {
        const int totalStreamBytes = 2 * 1024 * 1024;
        using var responseStream = new NewlineLessChunkStream(totalStreamBytes);
        using var client = new HttpClient(new SpyHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(responseStream)
            })));

        var service = new SpotlightAiService(client);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in service.StreamAsync(CreateSettings("OpenAI"), [new("user", "hi")], CancellationToken.None))
            {
            }
        });

        Assert.Contains("maximum allowed size", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(responseStream.BytesRead > SpotlightAiService.MaxStreamResponseChars,
            $"Expected stream to read past 1 MB limit before throwing, but read {responseStream.BytesRead} bytes.");
        Assert.True(responseStream.BytesRead < totalStreamBytes,
            $"Expected stream to abort before 2 MB EOF, but read {responseStream.BytesRead} bytes.");
    }

    [Fact]
    public async Task StreamAsync_StalledStream_ThrowsTimeoutPromptly()
    {
        using var stalledStream = new StallingStream();
        using var client = new HttpClient(new SpyHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stalledStream)
            })));

        var service = new SpotlightAiService(client)
        {
            StreamReadIdleTimeout = TimeSpan.FromMilliseconds(50)
        };

        var ex = await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in service.StreamAsync(CreateSettings("OpenAI"), [new("user", "hi")], CancellationToken.None))
            {
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("stalled", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task GetBalanceAsync_WhenPrivacyBlocksAi_ThrowsWithoutCallingTransport(bool localOnly, bool allowOnlineAi)
    {
        int calls = 0;
        using var client = new HttpClient(new SpyHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));

        var settings = new NotchSettings
        {
            EnableLocalOnlyMode = localOnly,
            AllowOnlineAi = allowOnlineAi,
            SpotlightDeepSeekApiKey = "sk-test"
        };

        var service = new SpotlightAiService(client);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => service.GetBalanceAsync(settings, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StreamAsync_OpenAiTextDeltas_InvokesOnUsageOnlyWhenHeadersOrUsageArrive()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++)
            sb.Append("data: {\"choices\":[{\"delta\":{\"content\":\"tok\"}}]}\n\n");
        sb.Append("data: {\"choices\":[],\"usage\":{\"total_tokens\":42}}\n\n");
        sb.Append("data: [DONE]\n\n");

        using var client = new HttpClient(new SpyHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sb.ToString())
            })));

        var service = new SpotlightAiService(client);
        int usageCallbacks = 0;
        long? lastTokens = null;
        await foreach (var _ in service.StreamAsync(
            CreateSettings("OpenAI"),
            [new("user", "hi")],
            CancellationToken.None,
            onUsage: usage =>
            {
                usageCallbacks++;
                lastTokens = usage.Tokens;
            }))
        {
        }

        Assert.Equal(42, lastTokens);
        Assert.Equal(2, usageCallbacks);
    }

    [Fact]
    public async Task StreamAsync_TimedOutUncancellableStream_DoesNotCorruptArrayPool()
    {
        var secondReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLateWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stream = new LateCompletingUncancellableStream(secondReadEntered, releaseLateWrite.Task);
        using var client = new HttpClient(new SpyHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            })));

        var service = new SpotlightAiService(client)
        {
            StreamReadIdleTimeout = TimeSpan.FromMilliseconds(40)
        };

        await Assert.ThrowsAsync<TimeoutException>(async () =>
        {
            await foreach (var _ in service.StreamAsync(CreateSettings("OpenAI"), [new("user", "hi")], CancellationToken.None))
            {
            }
        }).WaitAsync(TimeSpan.FromSeconds(5));

        char[] probe = ArrayPool<char>.Shared.Rent(4096);
        try
        {
            Array.Clear(probe);
            await secondReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            releaseLateWrite.TrySetResult();
            await stream.SecondReadCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(30);
            Assert.All(probe, ch => Assert.Equal('\0', ch));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(probe);
        }
    }

    private static NotchSettings CreateSettings(string provider)
    {
        var settings = new NotchSettings
        {
            SpotlightAiProvider = provider,
            EnableLocalOnlyMode = false,
            AllowOnlineAi = true
        };
        SpotlightAiService.Configure(settings, provider, "sk-test-secret", "gpt-4o-mini");
        return settings;
    }

    private sealed class SpyHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class NewlineLessChunkStream(int totalBytes) : Stream
    {
        private static readonly byte[] Prefix = Encoding.ASCII.GetBytes("data: ");
        public int BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        private int ReadCore(Span<byte> destination)
        {
            int remaining = totalBytes - BytesRead;
            if (remaining <= 0) return 0;
            int toWrite = Math.Min(Math.Min(destination.Length, 4096), remaining);
            for (int i = 0; i < toWrite; i++)
            {
                int pos = BytesRead + i;
                destination[i] = pos < Prefix.Length ? Prefix[pos] : (byte)'a';
            }
            BytesRead += toWrite;
            return toWrite;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LateCompletingUncancellableStream(
        TaskCompletionSource secondReadEntered,
        Task releaseLateWrite) : Stream
    {
        private int _reads;
        public TaskCompletionSource SecondReadCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = Interlocked.Increment(ref _reads);
            if (read == 1)
            {
                byte[] first = Encoding.ASCII.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"ok\"}}]}\n\n");
                first.CopyTo(buffer.Span);
                return first.Length;
            }
            if (read == 2)
            {
                secondReadEntered.TrySetResult();
                await releaseLateWrite.ConfigureAwait(false);
                int count = Math.Min(buffer.Length, 256);
                buffer.Span[..count].Fill((byte)'Z');
                SecondReadCompleted.TrySetResult();
                return count;
            }
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
