using VNotch.Services;

namespace VNotch.Controls;

public partial class ClipboardTray
{
    private UiScheduledTimer? _cacheReleaseTimer;
    private CancellationTokenSource? _cacheReleaseCancellation;

    private void ScheduleCacheRelease()
    {
        if (_disposed || IsVisible || _controller == null) return;
        CancelCacheRelease();
        _cacheReleaseTimer ??= CreateCacheReleaseTimer();
        _cacheReleaseTimer.Start();
    }

    private UiScheduledTimer CreateCacheReleaseTimer()
    {
        var timer = new UiScheduledTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (_disposed || IsVisible || _controller == null) return;
            using var cancellation = new CancellationTokenSource();
            _cacheReleaseCancellation = cancellation;
            try { await _controller.Store.TrimMemoryCachesAsync(cancellation.Token); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (ReferenceEquals(_cacheReleaseCancellation, cancellation))
                    _cacheReleaseCancellation = null;
            }
        };
        return timer;
    }

    private void CancelCacheRelease()
    {
        _cacheReleaseTimer?.Stop();
        _cacheReleaseCancellation?.Cancel();
        _cacheReleaseCancellation = null;
    }
}
