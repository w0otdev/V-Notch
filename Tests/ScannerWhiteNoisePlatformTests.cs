using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ScannerWhiteNoisePlatformTests
{
    [Fact]
    public async Task WhiteNoiseService_HandleNativePlaybackFailed_MarshalsStateChangedAndClearsPlayingKind()
    {
        int marshalCallCount = 0;
        int stateChangedCallCount = 0;
        int backgroundThreadId = -1;
        int stateChangedThreadId = -1;
        int uiThreadId = Environment.CurrentManagedThreadId;
        Action? capturedUiAction = null;

        void OnStateChanged()
        {
            stateChangedThreadId = Environment.CurrentManagedThreadId;
            if (stateChangedThreadId == backgroundThreadId)
            {
                throw new InvalidOperationException("StateChanged invoked directly on background native callback thread.");
            }
            Interlocked.Increment(ref stateChangedCallCount);
        }

        WhiteNoiseService.SetPlayingKindForTest(0);
        Assert.True(WhiteNoiseService.IsPlaying);
        Assert.NotNull(WhiteNoiseService.CurrentKind);
        Assert.Equal(0, WhiteNoiseService.PlayingKind);

        WhiteNoiseService.CallbackMarshaler = action =>
        {
            Interlocked.Increment(ref marshalCallCount);
            capturedUiAction = action;
        };
        WhiteNoiseService.StateChanged += OnStateChanged;

        try
        {
            await Task.Run(() =>
            {
                backgroundThreadId = Environment.CurrentManagedThreadId;
                WhiteNoiseService.HandleNativePlaybackFailed();
            });

            Assert.False(WhiteNoiseService.IsPlaying);
            Assert.Null(WhiteNoiseService.CurrentKind);
            Assert.Equal(-1, WhiteNoiseService.PlayingKind);
            Assert.Equal(1, Volatile.Read(ref marshalCallCount));
            Assert.NotNull(capturedUiAction);

            int callerThreadId = Environment.CurrentManagedThreadId;
            capturedUiAction!();

            Assert.Equal(1, Volatile.Read(ref stateChangedCallCount));
            Assert.Equal(callerThreadId, stateChangedThreadId);
            Assert.NotEqual(backgroundThreadId, stateChangedThreadId);
        }
        finally
        {
            WhiteNoiseService.StateChanged -= OnStateChanged;
            WhiteNoiseService.CallbackMarshaler = null;
            WhiteNoiseService.SetPlayingKindForTest(-1);
        }
    }

    [Fact]
    public void WindowTitleScanner_RunWithTimeout_BoundsStaThreadWhenHungAndRecoversAfterUnblock()
    {
        using var hangStarted = new ManualResetEventSlim(false);
        using var releaseHang = new ManualResetEventSlim(false);
        using var hangExited = new ManualResetEventSlim(false);

        int activeRuns = 0;
        int maxConcurrentRuns = 0;

        for (int i = 0; i < 10; i++)
        {
            string? timedOutResult = WindowTitleScanner.RunWithTimeout(() =>
            {
                int current = Interlocked.Increment(ref activeRuns);
                int observedMax;
                do
                {
                    observedMax = Volatile.Read(ref maxConcurrentRuns);
                    if (current <= observedMax) break;
                }
                while (Interlocked.CompareExchange(ref maxConcurrentRuns, current, observedMax) != observedMax);

                try
                {
                    hangStarted.Set();
                    releaseHang.Wait(TimeSpan.FromSeconds(5));
                    return "hung";
                }
                finally
                {
                    Interlocked.Decrement(ref activeRuns);
                    hangExited.Set();
                }
            }, timeoutMs: 10);

            Assert.Null(timedOutResult);
            Assert.True(WindowTitleScanner.ActiveUiaStaThreadCount <= 1);
        }

        Assert.True(hangStarted.IsSet);
        Assert.Equal(1, Volatile.Read(ref maxConcurrentRuns));
        Assert.Equal(1, WindowTitleScanner.ActiveUiaStaThreadCount);

        releaseHang.Set();
        Assert.True(hangExited.Wait(TimeSpan.FromSeconds(2)));

        (string Value, ApartmentState Apartment) recovered = WindowTitleScanner.RunWithTimeout(
            () => ("recovered", Thread.CurrentThread.GetApartmentState()),
            timeoutMs: 500);

        Assert.Equal("recovered", recovered.Value);
        Assert.Equal(ApartmentState.STA, recovered.Apartment);
        Assert.Equal(1, WindowTitleScanner.ActiveUiaStaThreadCount);
    }

    [Fact]
    public void PlatformDetector_HasReliableWindowMatch_MatchesInstagramUnderTurkishCulture()
    {
        CultureInfo originalCulture = CultureInfo.CurrentCulture;
        CultureInfo originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            var turkish = new CultureInfo("tr-TR");
            CultureInfo.CurrentCulture = turkish;
            CultureInfo.CurrentUICulture = turkish;

            Assert.True(PlatformDetector.HasReliableWindowMatch("Instagram - Google Chrome", MediaPlatform.Instagram));
            Assert.True(PlatformDetector.HasReliableWindowMatch(
                ["Reel by creator - Instagram - Google Chrome"],
                "Reel by creator",
                "Instagram"));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}
