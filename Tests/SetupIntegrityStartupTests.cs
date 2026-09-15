using System.IO;
using System.Net;
using System.Net.Http;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class SetupIntegrityStartupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetupTimeout_CancelsPendingNetworkRequest_AndLeavesDispatcherResponsive(bool downloadingChecksum)
    {
        string file = Path.Combine(Path.GetTempPath(), $"setup-integrity-{Guid.NewGuid():N}.exe");
        await File.WriteAllTextAsync(file, "Test installer payload");
        using var timeout = new CancellationTokenSource();
        using var handler = new PendingIntegrityResponse(downloadingChecksum);
        using var client = new HttpClient(handler);
        Task<IntegrityCheckStatus>? check = null;

        try
        {
            SharedStaTestRunner.Run(() =>
            {
                check = AppIntegrityService.VerifyChecksumAsync(
                    file, "1.9.2", client, timeout.Token);
            });
            await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(check!.IsCompleted);

            // A dispatcher callback must run even while the server has not replied.
            bool dispatcherResponded = false;
            SharedStaTestRunner.Run(() => dispatcherResponded = true);
            Assert.True(dispatcherResponded);

            timeout.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => check.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(handler.RequestWasCancelled);
        }
        finally
        {
            timeout.Cancel();
            File.Delete(file);
        }
    }

    private sealed class PendingIntegrityResponse(bool downloadingChecksum) : HttpMessageHandler
    {
        public TaskCompletionSource RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool RequestWasCancelled { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (downloadingChecksum && request.RequestUri!.Host == "api.github.com")
            {
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"assets":[{"name":"checksums.txt","browser_download_url":"https://test.invalid/checksums.txt"}]}
                        """)
                };
            }

            RequestStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Pending request completed without cancellation.");
            }
            catch (OperationCanceledException)
            {
                RequestWasCancelled = true;
                throw;
            }
        }
    }
}
