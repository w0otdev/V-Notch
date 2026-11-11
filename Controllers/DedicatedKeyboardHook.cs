using System.Windows.Threading;
using static VNotch.Services.Win32Interop;

namespace VNotch.Controllers;

/// <summary>Shares a single reference-counted low-level keyboard hook thread across all listeners.</summary>
internal sealed class DedicatedKeyboardHook : IDisposable
{
    private static readonly object Gate = new();
    private static SharedHookHost? s_current;

    private readonly SharedHookHost _host;
    private readonly Func<KBDLLHOOKSTRUCT, int, bool> _callback;
    private int _disposed;

    internal int InstallationError { get; }
    internal int ManagedThreadId => _host.ManagedThreadId;
    internal Dispatcher? Dispatcher => _host.Dispatcher;
    internal bool IsInstalled => Volatile.Read(ref _disposed) == 0 && _host.IsInstalled;

    internal DedicatedKeyboardHook(Func<KBDLLHOOKSTRUCT, int, bool> handleKey)
    {
        ArgumentNullException.ThrowIfNull(handleKey);
        _callback = (key, message) => Volatile.Read(ref _disposed) == 0 && handleKey(key, message);

        lock (Gate)
        {
            if (s_current == null || !s_current.IsActive)
            {
                s_current = new SharedHookHost();
            }

            _host = s_current;
            if (!_host.IsInstalled)
            {
                InstallationError = _host.InstallationError;
                s_current = null;
                return;
            }

            _host.AddListener(_callback);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        bool shouldShutdown = false;
        lock (Gate)
        {
            if (_host.RemoveListener(_callback) == 0 && ReferenceEquals(s_current, _host))
            {
                s_current = null;
                shouldShutdown = true;
            }
        }

        if (shouldShutdown)
        {
            _host.Shutdown();
        }
    }

    private sealed class SharedHookHost
    {
        private readonly Thread _thread;
        private readonly LowLevelKeyboardHook _hook;
        private Func<KBDLLHOOKSTRUCT, int, bool>[] _listeners = [];
        private int _stopped;

        internal Dispatcher? Dispatcher { get; private set; }
        internal int InstallationError { get; private set; }
        internal int ManagedThreadId => _thread.ManagedThreadId;
        internal bool IsInstalled => Volatile.Read(ref _stopped) == 0 && _hook.IsInstalled;
        internal bool IsActive => Volatile.Read(ref _stopped) == 0 && _hook.IsInstalled && Dispatcher is { HasShutdownStarted: false };

        internal SharedHookHost()
        {
            _hook = new LowLevelKeyboardHook(DispatchKey);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "VNotch keyboard hook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            // Startup only installs the native hook; it never calls the UI dispatcher.
            ready.Task.GetAwaiter().GetResult();
        }

        private bool DispatchKey(KBDLLHOOKSTRUCT key, int message)
        {
            var snapshot = Volatile.Read(ref _listeners);
            bool swallow = false;
            for (int i = 0; i < snapshot.Length; i++)
            {
                if (snapshot[i](key, message))
                    swallow = true;
            }
            return swallow;
        }

        internal void AddListener(Func<KBDLLHOOKSTRUCT, int, bool> listener)
        {
            var current = _listeners;
            var next = new Func<KBDLLHOOKSTRUCT, int, bool>[current.Length + 1];
            Array.Copy(current, next, current.Length);
            next[current.Length] = listener;
            Volatile.Write(ref _listeners, next);
        }

        internal int RemoveListener(Func<KBDLLHOOKSTRUCT, int, bool> listener)
        {
            var current = _listeners;
            int idx = Array.IndexOf(current, listener);
            if (idx < 0) return current.Length;
            if (current.Length == 1)
            {
                Volatile.Write(ref _listeners, []);
                return 0;
            }
            var next = new Func<KBDLLHOOKSTRUCT, int, bool>[current.Length - 1];
            if (idx > 0) Array.Copy(current, 0, next, 0, idx);
            if (idx < current.Length - 1) Array.Copy(current, idx + 1, next, idx, current.Length - idx - 1);
            Volatile.Write(ref _listeners, next);
            return next.Length;
        }

        private void Run(TaskCompletionSource ready)
        {
            try
            {
                Dispatcher = Dispatcher.CurrentDispatcher;
                bool installed = _hook.Install();
                if (!installed) InstallationError = _hook.InstallationError;
                ready.TrySetResult();
                if (installed) Dispatcher.Run();
            }
            catch (Exception ex)
            {
                InstallationError = ex.HResult;
            }
            finally
            {
                Interlocked.Exchange(ref _stopped, 1);
                ready.TrySetResult();
                _hook.Dispose();
            }
        }

        internal void Shutdown()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            // No synchronous UI call is made from the hook thread, so shutdown cannot
            // deadlock with activation/layout work on the UI dispatcher.
            var dispatcher = Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            if (Thread.CurrentThread == _thread)
            {
                _hook.Dispose();
                dispatcher.InvokeShutdown();
                return;
            }
            try
            {
                dispatcher.Invoke(() =>
                {
                    _hook.Dispose();
                    dispatcher.InvokeShutdown();
                }, DispatcherPriority.Send);
            }
            catch
            {
                try { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
                catch { /* already shutting down */ }
            }
        }
    }
}
