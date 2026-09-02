using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;

public enum LogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    None = 5
}

public static class RuntimeLog
{
    private static readonly object _lifecycleLock = new();
    private static readonly UTF8Encoding _utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static string _logPath = Path.Combine(AppContext.BaseDirectory, "vnotch-debug.log");
    private static AsyncLogWriter? _writer;
    private static volatile bool _initialized;
    private static int _minimumLevel =
#if DEBUG
        (int)LogLevel.Debug;
#else
        (int)LogLevel.Info;
#endif

    private const long MaxLogSizeBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(2);

    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set => Volatile.Write(ref _minimumLevel, (int)value);
    }

    public static event Action<LogLevel, string, string>? EntryWritten;

    public static string LogPath => _logPath;

    public static bool IsEnabled(LogLevel level) =>
        _initialized && level >= (LogLevel)Volatile.Read(ref _minimumLevel);

    public static void InitializeNewSession(string? fileName = null)
    {
        // Initialization is normally called once. If a host reinitializes logging,
        // finish the previous writer before replacing its queue and destination.
        Shutdown(DefaultShutdownTimeout);

        lock (_lifecycleLock)
        {
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                _logPath = Path.Combine(AppContext.BaseDirectory, fileName);
            }

            try
            {
                var dir = Path.GetDirectoryName(_logPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                RotateIfNeeded();

                File.WriteAllText(
                    _logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] New session started{Environment.NewLine}",
                    _utf8WithoutBom);

                _writer = new AsyncLogWriter(_logPath);
                _initialized = true;
            }
            catch
            {
                _writer = null;
                _initialized = false;
            }
        }
    }

    public static void Trace(string category, string message) => WriteEntry(LogLevel.Trace, category, message);

    public static void Trace(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Trace)) WriteEntry(LogLevel.Trace, category, messageFactory());
    }

    public static void Debug(string category, string message) => WriteEntry(LogLevel.Debug, category, message);

    public static void Debug(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Debug)) WriteEntry(LogLevel.Debug, category, messageFactory());
    }

    public static void Log(string category, string message) => WriteEntry(LogLevel.Info, category, message);

    public static void Info(string category, string message) => WriteEntry(LogLevel.Info, category, message);

    public static void Info(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Info)) WriteEntry(LogLevel.Info, category, messageFactory());
    }

    public static void Warn(string category, string message) => WriteEntry(LogLevel.Warn, category, message);

    public static void Error(string category, string message) => WriteEntry(LogLevel.Error, category, message);

    public static void Error(string category, Exception ex, string? context = null)
    {
        var msg = context != null
            ? $"{context}: {ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message}";
        WriteEntry(LogLevel.Error, category, $"{msg}{Environment.NewLine}{ex}");

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[{category}] {msg}");
        if (ex.StackTrace != null)
            System.Diagnostics.Debug.WriteLine(ex.StackTrace);
#endif
    }

    /// <summary>
    /// Completes after every log item queued before this call has been written.
    /// Normal callers do not need to flush; this is intended for fatal-error paths.
    /// </summary>
    public static Task FlushAsync()
    {
        lock (_lifecycleLock)
        {
            return _writer?.EnqueueFlush() ?? Task.CompletedTask;
        }
    }

    /// <summary>
    /// Stops accepting new entries, drains the queue, and waits briefly for the
    /// background writer. Call this once during application shutdown.
    /// </summary>
    public static bool Shutdown(TimeSpan timeout)
    {
        AsyncLogWriter? writer;

        lock (_lifecycleLock)
        {
            _initialized = false;
            writer = _writer;
            _writer = null;
            writer?.RequestStop();
        }

        if (writer == null)
        {
            return true;
        }

        try
        {
            return writer.Completion.Wait(timeout);
        }
        catch
        {
            return false;
        }
    }

    public static void ClearLog()
    {
        Shutdown(DefaultShutdownTimeout);
        lock (_lifecycleLock)
        {
            try
            {
                if (File.Exists(_logPath)) File.Delete(_logPath);
                string oldPath = _logPath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
            catch
            {
            }

            if (IsEnabled(LogLevel.Info))
            {
                InitializeNewSession();
            }
        }
    }

    private static string LevelLabel(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };

    private static void WriteEntry(LogLevel level, string category, string message)
    {
        if (!IsEnabled(level)) return;

        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelLabel(level)}] [{category}] {message}{Environment.NewLine}";

        // The lock protects only the lifecycle handoff. No formatting or file I/O
        // is performed while holding it, so UI callers return after a queue write.
        lock (_lifecycleLock)
        {
            if (_initialized)
            {
                _writer?.TryEnqueue(line, level);
            }
        }

        try
        {
            EntryWritten?.Invoke(level, category, message);
        }
        catch { }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var info = new FileInfo(_logPath);
            if (info.Length <= MaxLogSizeBytes) return;

            var backupPath = _logPath + ".old";
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(_logPath, backupPath);
        }
        catch
        {
        }
    }

    private sealed class AsyncLogWriter
    {
        private const int NormalQueueCapacity = 4096;
        private const int CriticalQueueCapacity = 8192;
        private const int MaxBatchEntries = 128;
        private const int MaxBatchCharacters = 64 * 1024;
        private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(40);

        private readonly string _path;
        private readonly ConcurrentQueue<QueueItem> _queue = new();
        private readonly SemaphoreSlim _queueSignal = new(0, 1);
        private int _queuedCount;
        private int _droppedCount;
        private int _stopRequested;

        public AsyncLogWriter(string path)
        {
            _path = path;
            Completion = Task.Run(WriteLoopAsync);
        }

        public Task Completion { get; }

        public bool TryEnqueue(string line, LogLevel level)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return false;
            }

            int count = Interlocked.Increment(ref _queuedCount);
            int capacity = level >= LogLevel.Warn ? CriticalQueueCapacity : NormalQueueCapacity;
            if (count > capacity)
            {
                Interlocked.Decrement(ref _queuedCount);
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            _queue.Enqueue(new QueueItem(line, null));
            SignalIfQueueWasEmpty(count);
            return true;
        }

        public Task EnqueueFlush()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int count = Interlocked.Increment(ref _queuedCount);
            _queue.Enqueue(new QueueItem(null, completion));
            SignalIfQueueWasEmpty(count);
            return completion.Task;
        }

        public void RequestStop()
        {
            Volatile.Write(ref _stopRequested, 1);
            SignalWriter();
        }

        private async Task WriteLoopAsync()
        {
            try
            {
                while (true)
                {
                    if (Volatile.Read(ref _queuedCount) == 0)
                    {
                        if (Volatile.Read(ref _stopRequested) != 0)
                        {
                            break;
                        }

                        await _queueSignal.WaitAsync().ConfigureAwait(false);
                    }

                    if (Volatile.Read(ref _stopRequested) == 0)
                    {
                        await Task.Delay(BatchWindow).ConfigureAwait(false);
                    }

                    await WriteNextBatchAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // Logging must never take down the application.
            }
            finally
            {
                CompletePendingFlushes();
            }
        }

        private async Task WriteNextBatchAsync()
        {
            var batch = new StringBuilder(capacity: 4096);
            List<TaskCompletionSource>? flushes = null;
            int entries = 0;

            while (entries < MaxBatchEntries &&
                   batch.Length < MaxBatchCharacters &&
                   _queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _queuedCount);

                if (item.Line != null)
                {
                    batch.Append(item.Line);
                    entries++;
                }

                if (item.FlushCompletion != null)
                {
                    (flushes ??= []).Add(item.FlushCompletion);
                    break;
                }
            }

            int dropped = Interlocked.Exchange(ref _droppedCount, 0);
            if (dropped > 0)
            {
                batch.Append($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [WARN] [LOGGER] " +
                             $"Dropped {dropped} low-priority log entries because the async queue was full.{Environment.NewLine}");
            }

            try
            {
                if (batch.Length > 0)
                {
                    await File.AppendAllTextAsync(_path, batch.ToString(), _utf8WithoutBom).ConfigureAwait(false);
                }
            }
            catch
            {
            }
            finally
            {
                if (flushes != null)
                {
                    foreach (var flush in flushes)
                    {
                        flush.TrySetResult();
                    }
                }
            }
        }

        private void CompletePendingFlushes()
        {
            while (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _queuedCount);
                item.FlushCompletion?.TrySetResult();
            }
        }

        private void SignalIfQueueWasEmpty(int queueCount)
        {
            if (queueCount == 1)
            {
                SignalWriter();
            }
        }

        private void SignalWriter()
        {
            try
            {
                _queueSignal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        private readonly record struct QueueItem(string? Line, TaskCompletionSource? FlushCompletion);
    }
}
