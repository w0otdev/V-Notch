using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

public static partial class RuntimeLog
{
    private const string NativeDll = "VNotch.Native.dll";

    [LibraryImport(NativeDll)]
    private static partial int vn_runtime_log_is_level_enabled(
        byte initialized,
        int level,
        int minimumLevel,
        out byte outEnabled);

    [LibraryImport(NativeDll)]
    private static partial int vn_runtime_log_resolve_queue_capacity(
        int level,
        int normalCap,
        int criticalCap,
        out int outCapacity);

    [LibraryImport(NativeDll)]
    private static partial int vn_runtime_log_should_rotate(
        byte forceSessionRotation,
        long currentLength,
        int incomingBytes,
        long maxLogSizeBytes,
        out byte outRotate);

    [LibraryImport(NativeDll, StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_runtime_log_should_forward_crash(
        byte initialized,
        ReadOnlySpan<char> category,
        int categoryLen,
        out byte outForward);

    private static readonly object _lifecycleLock = new();
    private static readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private static Task? _shutdownCompletion;
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

    private static int _diskCaptureEnabled = 1;
    private const long MaxLogSizeBytes = 5 * 1024 * 1024;
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(2);

    public static LogLevel MinimumLevel
    {
        get => (LogLevel)Volatile.Read(ref _minimumLevel);
        set
        {
            Volatile.Write(ref _minimumLevel, (int)value);
            Volatile.Write(ref _diskCaptureEnabled, value != LogLevel.None ? 1 : 0);
        }
    }

    public static bool IsDiskCaptureEnabled
    {
        get => Volatile.Read(ref _diskCaptureEnabled) != 0 && MinimumLevel != LogLevel.None;
        set => SetDiskCaptureEnabled(value);
    }

    public static void SetDiskCaptureEnabled(bool enabled)
    {
        Volatile.Write(ref _diskCaptureEnabled, enabled ? 1 : 0);
        if (!enabled)
        {
            Volatile.Write(ref _minimumLevel, (int)LogLevel.None);
        }
        else if ((LogLevel)Volatile.Read(ref _minimumLevel) == LogLevel.None)
        {
            Volatile.Write(
                ref _minimumLevel,
#if DEBUG
                (int)LogLevel.Debug
#else
                (int)LogLevel.Info
#endif
            );
        }
    }

    public static event Action<LogLevel, string, string>? EntryWritten;

    public static string LogPath => _logPath;

    public static bool IsEnabled(LogLevel level)
    {
        if (!IsDiskCaptureEnabled) return false;
        byte init = _initialized ? (byte)1 : (byte)0;
        return vn_runtime_log_is_level_enabled(init, (int)level, Volatile.Read(ref _minimumLevel), out byte enabled) == 0
            ? enabled != 0
            : (_initialized && level >= (LogLevel)Volatile.Read(ref _minimumLevel));
    }

    public static async Task InitializeNewSessionAsync(string? fileName = null)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await InitializeNewSessionCoreAsync(fileName).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private static async Task InitializeNewSessionCoreAsync(string? fileName)
    {
        // Initialization is normally called once. If a host reinitializes logging,
        // finish the previous writer before replacing its queue and destination.
        if (!await ShutdownCoreAsync(DefaultShutdownTimeout).ConfigureAwait(false)) return;

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

                RotateIfNeeded(forceSessionRotation: true);

                File.WriteAllText(
                    _logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [SYSTEM] New session started{Environment.NewLine}",
                    _utf8WithoutBom);

                _writer = new AsyncLogWriter(_logPath);
                _shutdownCompletion = null;
                _initialized = true;
            }
            catch (Exception)
            {
                // Failed to initialize disk logging session (e.g. access denied or path locked)
                _writer = null;
                _initialized = false;
            }
        }
    }

    public static void Trace(string category, string message) => WriteEntry(LogLevel.Trace, category, message);

    public static void Trace(string category, [InterpolatedStringHandlerArgument("category")] ref TraceMessageHandler message)
    {
        if (message.Enabled) WriteEntry(LogLevel.Trace, category, message.GetText());
    }

    public static void Trace(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Trace)) WriteEntry(LogLevel.Trace, category, messageFactory());
    }

    public static void Debug(string category, string message) => WriteEntry(LogLevel.Debug, category, message);

    public static void Debug(string category, [InterpolatedStringHandlerArgument("category")] ref DebugMessageHandler message)
    {
        if (message.Enabled) WriteEntry(LogLevel.Debug, category, message.GetText());
    }

    public static void Debug(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Debug)) WriteEntry(LogLevel.Debug, category, messageFactory());
    }


    [InterpolatedStringHandler]
    public ref struct DebugMessageHandler
    {
        private DefaultInterpolatedStringHandler _builder;
        public bool Enabled { get; }
        public DebugMessageHandler(int literalLength, int formattedCount, string category, out bool enabled)
        {
            Enabled = enabled = IsEnabled(LogLevel.Debug);
            _builder = enabled ? new(literalLength, formattedCount) : default;
        }
        public void AppendLiteral(string value) => _builder.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment, string? format = null) => _builder.AppendFormatted(value, alignment, format);
        internal string GetText() => _builder.ToStringAndClear();
    }

    [InterpolatedStringHandler]
    public ref struct TraceMessageHandler
    {
        private DefaultInterpolatedStringHandler _builder;
        public bool Enabled { get; }
        public TraceMessageHandler(int literalLength, int formattedCount, string category, out bool enabled)
        {
            Enabled = enabled = IsEnabled(LogLevel.Trace);
            _builder = enabled ? new(literalLength, formattedCount) : default;
        }
        public void AppendLiteral(string value) => _builder.AppendLiteral(value);
        public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);
        public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);
        public void AppendFormatted<T>(T value, int alignment, string? format = null) => _builder.AppendFormatted(value, alignment, format);
        internal string GetText() => _builder.ToStringAndClear();
    }

    public static void Log(string category, string message) => WriteEntry(LogLevel.Info, category, message);

    public static void Info(string category, string message) => WriteEntry(LogLevel.Info, category, message);

    public static void Info(string category, Func<string> messageFactory)
    {
        if (IsEnabled(LogLevel.Info)) WriteEntry(LogLevel.Info, category, messageFactory());
    }

    public static void Warn(string category, string message) => WriteEntry(LogLevel.Warn, category, message);

    public static void Error(string category, string message)
    {
        WriteEntry(LogLevel.Error, category, message);
        ReadOnlySpan<char> catSpan = category is not null ? category.AsSpan() : ReadOnlySpan<char>.Empty;
        if (IsDiskCaptureEnabled && vn_runtime_log_should_forward_crash(_initialized ? (byte)1 : (byte)0, catSpan, catSpan.Length, out byte forward) == 0 && forward != 0)
        {
            CrashReporter.LogCrash($"RuntimeLog.{category}", message);
        }
    }

    public static void Error(string category, Exception ex, string? context = null)
    {
        var msg = context != null
            ? $"{context}: {ex.GetType().Name}: {ex.Message}"
            : $"{ex.GetType().Name}: {ex.Message}";
        WriteEntry(LogLevel.Error, category, $"{msg}{Environment.NewLine}{ex}");

        ReadOnlySpan<char> catSpan = category is not null ? category.AsSpan() : ReadOnlySpan<char>.Empty;
        if (IsDiskCaptureEnabled && vn_runtime_log_should_forward_crash(_initialized ? (byte)1 : (byte)0, catSpan, catSpan.Length, out byte forward) == 0 && forward != 0)
        {
            CrashReporter.LogCrash($"RuntimeLog.{category}", ex, context);
        }

#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[{category}] {msg}");
        if (ex.StackTrace != null)
            System.Diagnostics.Debug.WriteLine(ex.StackTrace);
#endif
    }

    public static Task FlushAsync()
    {
        lock (_lifecycleLock)
        {
            return _writer?.EnqueueFlush() ?? Task.CompletedTask;
        }
    }

    public static async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { return await ShutdownCoreAsync(timeout).ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private static async Task<bool> ShutdownCoreAsync(TimeSpan timeout)
    {
        AsyncLogWriter? writer;
        Task? completion;

        lock (_lifecycleLock)
        {
            _initialized = false;
            writer = _writer;
            _writer = null;
            writer?.RequestStop();
            if (writer != null) _shutdownCompletion = writer.Completion;
            completion = _shutdownCompletion;
        }

        if (completion == null)
        {
            return true;
        }

        try
        {
            await completion.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // Ignore timeout or wait exceptions during writer shutdown
            return false;
        }
    }

    public static async Task ClearLogAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { await ClearLogCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }

    private static async Task ClearLogCoreAsync()
    {
        bool restart = _initialized;
        if (!await ShutdownCoreAsync(DefaultShutdownTimeout).ConfigureAwait(false)) return;
        lock (_lifecycleLock)
        {
            try
            {
                if (File.Exists(_logPath)) File.Delete(_logPath);
                string oldPath = _logPath + ".old";
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }
            catch (Exception)
            {
                // Ignore file deletion errors when clearing log (e.g. file is locked by another process)
            }

        }
        CrashReporter.DeleteCrashLogs();
        if (restart && IsDiskCaptureEnabled) await InitializeNewSessionCoreAsync(null).ConfigureAwait(false);
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

        var sanitizedMessage = SensitiveDataScrubber.Scrub(message);
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{LevelLabel(level)}] [{category}] {sanitizedMessage}{Environment.NewLine}";

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
            EntryWritten?.Invoke(level, category, sanitizedMessage);
        }
        catch (Exception)
        {
            // Subscriber exceptions in the event handler should not disrupt the logging pipeline
        }
    }

    private static bool RotateIfNeeded(bool forceSessionRotation = false, string? path = null, int incomingBytes = 0)
    {
        path ??= _logPath;
        try
        {
            if (!File.Exists(path)) return true;
            var info = new FileInfo(path);
            byte forceFlag = forceSessionRotation ? (byte)1 : (byte)0;
            bool shouldRotate = vn_runtime_log_should_rotate(
                forceFlag,
                info.Length,
                incomingBytes,
                MaxLogSizeBytes,
                out byte rotate) == 0
                ? rotate != 0
                : (info.Length > 0 && (forceSessionRotation || info.Length + incomingBytes > MaxLogSizeBytes));
            if (!shouldRotate) return true;

            var backupPath = path + ".old";
            if (File.Exists(backupPath))
                File.Delete(backupPath);
            File.Move(path, backupPath);
            return true;
        }
        catch (Exception)
        {
            // Ignore rotation failures when log files are locked or inaccessible
            return false;
        }
    }

    internal sealed class AsyncLogWriter : IDisposable
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

        public bool TryEnqueue(string line, LogLevel level = LogLevel.Info)
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return false;
            }

            int fallbackCap = level >= LogLevel.Warn ? CriticalQueueCapacity : NormalQueueCapacity;
            int capacity = vn_runtime_log_resolve_queue_capacity((int)level, NormalQueueCapacity, CriticalQueueCapacity, out int outCap) == 0
                ? outCap
                : fallbackCap;
            if (Volatile.Read(ref _queuedCount) >= capacity)
            {
                Interlocked.Increment(ref _droppedCount);
                return false;
            }

            if (line.Length > MaxBatchCharacters)
                line = line[..MaxBatchCharacters] + " [truncated]" + Environment.NewLine;
            _queue.Enqueue(new QueueItem(line, null));
            int count = Interlocked.Increment(ref _queuedCount);
            SignalIfQueueWasEmpty(count);
            return true;
        }

        public Task EnqueueFlush()
        {
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Enqueue(new QueueItem(null, completion));
            int count = Interlocked.Increment(ref _queuedCount);
            if (Volatile.Read(ref _stopRequested) != 0)
            {
                CompletePendingFlushes();
                return completion.Task;
            }

            SignalIfQueueWasEmpty(count);
            return completion.Task;
        }

        public void RequestStop()
        {
            Volatile.Write(ref _stopRequested, 1);
            SignalWriter();
        }

        public void Dispose() => RequestStop();

        private async Task WriteLoopAsync()
        {
            try
            {
                while (true)
                {
                    if (_queue.IsEmpty)
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

                    if (Volatile.Read(ref _stopRequested) != 0 && _queue.IsEmpty)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Logging must never take down the application.
            }
            finally
            {
                CompletePendingFlushes();
                _queueSignal.Dispose();
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
                    string text = batch.ToString();
                    if (RotateIfNeeded(path: _path, incomingBytes: _utf8WithoutBom.GetByteCount(text)))
                        await File.AppendAllTextAsync(_path, text, _utf8WithoutBom).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
                // Best-effort file write; ignore disk I/O errors to avoid terminating the background loop
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
            try
            {
                if (queueCount <= 1 || _queueSignal.CurrentCount == 0)
                {
                    SignalWriter();
                }
            }
            catch (ObjectDisposedException)
            {
                // Writer loop has already exited and disposed the signal.
            }
        }

        private void SignalWriter()
        {
            try
            {
                _queueSignal.Release();
            }
            catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
            {
                // Semaphore is already signaled or disposed during shutdown; safe to ignore
            }
        }

        private readonly record struct QueueItem(string? Line, TaskCompletionSource? FlushCompletion);
    }
}
