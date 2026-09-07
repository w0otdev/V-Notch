using System;
using System.Threading;
using System.Windows.Threading;

namespace VNotch.Tests;

internal static class SharedStaTestRunner
{
    private static readonly object _sync = new();
    private static Thread? _thread;
    private static Dispatcher? _dispatcher;

    public static void Run(Action action, int timeoutSeconds = 45)
    {
        lock (_sync)
        {
            if (_thread == null || !_thread.IsAlive || _dispatcher == null || _dispatcher.HasShutdownStarted)
            {
                using var ready = new ManualResetEventSlim(false);
                _thread = new Thread(() =>
                {
                    SynchronizationContext.SetSynchronizationContext(
                        new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "VNotchSharedStaRunner"
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait();
            }

            Exception? failure = null;
            var op = _dispatcher!.BeginInvoke(() =>
            {
                try { action(); }
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
}
