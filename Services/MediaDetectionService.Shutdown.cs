using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VNotch.Models;
using Windows.Media.Control;

namespace VNotch.Services;

public sealed partial class MediaDetectionService : IMediaDetectionService, IAsyncDisposable
{
    public void Dispose()
    {
        // Mark the service disposed immediately; let the dispatcher keep running
        // while workers finish cancellation and release their resources.
        _ = ObserveShutdownAsync(GetDisposalTask());
    }

    public ValueTask DisposeAsync() => new(GetDisposalTask());
    private Task _workerResourceReleaseTask = Task.CompletedTask;

    internal async Task DrainShutdownAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        await _workerResourceReleaseTask.ConfigureAwait(false);
    }

    private Task GetDisposalTask()
    {
        lock (_lifecycleLock)
            return _disposeTask ??= StopCoreAsync(isDisposing: true);
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { RuntimeLog.Warn(MediaStopLogTag, $"Shutdown failed: {ex}"); }
    }

    private async Task StopCoreAsync(bool isDisposing)
    {
        CancellationTokenSource? ctsToCancel;
        Task allTasks;
        GlobalSystemMediaTransportControlsSessionManager? sessionManagerToUnsub;

        lock (_lifecycleLock)
        {
            if (_state == ServiceLifecycleState.Disposed || (!isDisposing && _state == ServiceLifecycleState.Stopped))
                return;

            _state = isDisposing ? ServiceLifecycleState.Disposed : ServiceLifecycleState.Stopping;
            if (isDisposing)
                _disposed = true;

            unchecked { _lifecycleGeneration++; }

            ctsToCancel = _bgCts;
            _bgCts = null;
            if (isDisposing) _pinReconnect.Dispose();
            else _pinReconnect.Reset();

            var tasksToWait = new List<Task> { CancelSpotifyArtistLookup() };
            if (!_workerDrainTask.IsCompleted) tasksToWait.Add(_workerDrainTask);
            if (_initTask != null) tasksToWait.Add(_initTask);
            if (_processingTask != null) tasksToWait.Add(_processingTask);
            if (_heartbeatTask != null) tasksToWait.Add(_heartbeatTask);
            if (_stagedRefreshTask != null) tasksToWait.Add(_stagedRefreshTask);
            allTasks = _workerDrainTask = Task.WhenAll(tasksToWait);

            _initTask = null;
            _processingTask = null;
            _heartbeatTask = null;
            _stagedRefreshTask = null;

            sessionManagerToUnsub = _sessionManager;
            _sessionManager = null;
        }

        // 1. Cancel background token
        try
        {
            if (ctsToCancel != null) await ctsToCancel.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(MediaStopLogTag, $"CTS cancel failed: {ex.Message}");
        }

        // 2. Wait for running tasks to complete
        if (!allTasks.IsCompletedSuccessfully)
        {
            try
            {
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                var completed = await Task.WhenAny(allTasks, timeoutTask).ConfigureAwait(false);
                if (completed != allTasks)
                {
                    RuntimeLog.Warn(MediaStopLogTag, "Timed out waiting for background tasks to exit.");
                }
                else
                {
                    await allTasks.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(MediaStopLogTag, $"Error waiting for tasks: {ex.Message}");
            }
        }

        // 3. Unsubscribe from session & session manager
        Interlocked.Exchange(ref _pinnedSession, null);
        _pinReconnect.Reset();
        _volumeService.InvalidateVolumeSessionCache();
        UnsubscribeFromSession();
        if (sessionManagerToUnsub != null)
        {
            try
            {
                sessionManagerToUnsub.CurrentSessionChanged -= OnSessionChanged;
                sessionManagerToUnsub.SessionsChanged -= OnSessionsChanged;
            }
            catch (Exception ex)
            {
                RuntimeLog.Log(MediaStopLogTag, $"Failed unsubscribing session manager: {ex.Message}");
            }
        }

        // 4. Dispose CTS
        // A timed-out worker may still use its token and release the update lock.
        // Release those objects only after the captured workers actually exit.
        _workerResourceReleaseTask = ReleaseWorkerResourcesAsync(allTasks, ctsToCancel, isDisposing);

        // 5. Complete the channel or drain it for a later restart.
        _spotifyArtistCache.Clear();
        _sessionState.Clear();
        CancelAndClearThumbnailCts();
        if (isDisposing)
        {
            _changes.Complete();
        }
        else
        {
            // Drain stale items from channel so restart is clean
            _changes.Clear();

            lock (_lifecycleLock)
            {
                if (_state == ServiceLifecycleState.Stopping)
                {
                    _state = ServiceLifecycleState.Stopped;
                }
            }
        }
    }

    private async Task ReleaseWorkerResourcesAsync(Task workers, CancellationTokenSource? cts, bool isDisposing)
    {
        try { await workers.ConfigureAwait(false); }
        catch (Exception ex) { RuntimeLog.Debug(MediaStopLogTag, $"Worker exited: {ex.Message}"); }
        finally
        {
            cts?.Dispose();
            if (isDisposing)
            {
                _sourceCache.Save();
                _volumeService.Dispose();
                _updateLock.Dispose();
            }
        }
    }

}
