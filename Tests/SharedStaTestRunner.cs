using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using VNotch.TestSupport;

namespace VNotch.Tests;

internal static class SharedStaTestRunner
{
    private static readonly object _sync = new();
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;

    // The dispatcher keeps running its normal message loop while the test awaits
    // rendering or completion events. No nested DispatcherFrame is necessary.
    public static void RunAsync(Func<Task> action, int timeoutSeconds = 45)
        => RunAsync(_ => action(), timeoutSeconds);

    public static void RunAsync(Func<CancellationToken, Task> action, int timeoutSeconds = 45)
    {
        lock (_sync)
        {
            EnsureDispatcher();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher!.BeginInvoke(async () =>
            {
                try
                {
                    BackgroundTestWindows.Initialize();
                    await action(deadline.Token);
                    completion.TrySetResult();
                }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            // Allow cancellation continuations to finish their finally blocks
            // before the next serialized test acquires this dispatcher.
            completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds + 5)).GetAwaiter().GetResult();
        }
    }

    public static void Run(Action action, int timeoutSeconds = 45)
    {
        lock (_sync)
        {
            EnsureDispatcher();

            Exception? failure = null;
            var op = _dispatcher!.BeginInvoke(() =>
            {
                try
                {
                    BackgroundTestWindows.Initialize();
                    action();
                }
                catch (Exception ex) { failure = ex; }
            });

            var status = op.Wait(TimeSpan.FromSeconds(timeoutSeconds));
            if (status != DispatcherOperationStatus.Completed)
            {
                op.Abort();
                throw new TimeoutException($"STA test timed out after {timeoutSeconds}s.");
            }

            if (failure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void EnsureDispatcher()
    {
        if (_thread?.IsAlive == true && _dispatcher != null && !_dispatcher.HasShutdownStarted) return;
        using var ready = new ManualResetEventSlim(false);
        Exception? startupFailure = null;
        _thread = new Thread(() =>
        {
            try
            {
                if (!DesktopTestMode.Enabled) BackgroundTestDesktop.AttachCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                _dispatcher = Dispatcher.CurrentDispatcher;
            }
            catch (Exception ex) { startupFailure = ex; }
            finally { ready.Set(); }
            if (startupFailure != null) return;
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "VNotchSharedStaRunner" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (startupFailure != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(startupFailure).Throw();
    }
}
