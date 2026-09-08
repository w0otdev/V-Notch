using System;
using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services;


public static class CrashReporter
{
    private static readonly object _crashLock = new();
    private static readonly UTF8Encoding _utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly DateTime _processStartTime = DateTime.UtcNow;
    private static string _crashLogPath = string.Empty;
    private static volatile bool _initialized;
    private const long MaxCrashLogSizeBytes = 2 * 1024 * 1024; // 2 MB
    public const string CrashLogFileName = "vnotch-crash.log";


    public static string CrashLogPath
    {
        get
        {
            if (string.IsNullOrEmpty(_crashLogPath))
            {
                _crashLogPath = ResolvePrimaryCrashLogPath();
            }
            return _crashLogPath;
        }
        internal set => _crashLogPath = value;
    }

    /// <summary>
    /// Initializes global exception hooks as early as possible.
    /// Safe to call multiple times; subsequent calls are ignored.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;

        lock (_crashLock)
        {
            if (_initialized) return;

            try
            {
                _crashLogPath = ResolvePrimaryCrashLogPath();

                AppDomain.CurrentDomain.UnhandledException += OnUnhandledDomainException;
                TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

                _initialized = true;
            }
            catch (Exception ex)
            {
                // Fallback attempt to record initialization error
                try
                {
                    LogDirectToDisk("CrashReporter.Initialize", ex, "Failed to register global exception hooks", isTerminating: false);
                }
                catch
                {
                    // Fail-safe: never throw from Initialize
                }
            }
        }
    }

    [ThreadStatic]
    private static bool _isLogging;

    /// <summary>
    /// Records a crash or critical error synchronously to the crash log file.
    /// This method is fail-safe and will never throw an exception to the caller.
    /// </summary>
    public static void LogCrash(string source, object? exceptionOrError, string? context = null, bool isTerminating = false)
    {
        if (_isLogging) return;

        try
        {
            _isLogging = true;
            LogDirectToDisk(source, exceptionOrError, context, isTerminating);
        }
        catch (Exception ex)
        {
            // Ultimate fallback to stderr/debug stream if disk write fails
            try
            {
                Console.Error.WriteLine($"[CRASH-REPORTER-FATAL] Failed to write crash log: {ex.Message}");
                Trace.TraceError("[CRASH-REPORTER-FATAL] Failed to write crash log: {0}", ex.Message);
            }
            catch
            {
                // Swallow
            }
        }
        finally
        {
            _isLogging = false;
        }
    }

    /// <summary>
    /// Formats a complete, human-readable diagnostic report for an exception or error object.
    /// </summary>
    public static string FormatCrashReport(string source, object? exceptionOrError, string? context, bool isTerminating)
    {
        var nowUtc = DateTime.UtcNow;
        var nowLocal = DateTime.Now;
        var sb = new StringBuilder();

        sb.AppendLine(new string('=', 80));
        sb.AppendLine($"V-NOTCH CRASH REPORT - {nowUtc:yyyy-MM-dd HH:mm:ss.fff} UTC ({nowLocal:yyyy-MM-dd HH:mm:ss.fff zzz})");
        sb.AppendLine(new string('=', 80));

        // Application info
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var versionAttr = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                          ?? assembly.GetName().Version?.ToString()
                          ?? "Unknown";

        sb.AppendLine($"Application   : V-Notch v{versionAttr}");
        sb.AppendLine($"Process       : PID {Environment.ProcessId} ({Path.GetFileName(Environment.ProcessPath ?? "V-Notch.exe")})");
        sb.AppendLine($"Uptime        : {FormatDuration(nowUtc - _processStartTime)}");

        // Environment & OS info
        sb.AppendLine($"OS            : {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine($"Process Arch  : {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET Runtime  : {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"Machine / User: {Environment.MachineName} / {Environment.UserName}");
        sb.AppendLine($"Processors    : {Environment.ProcessorCount} logical cores");

        // Memory usage
        try
        {
            using var currentProcess = Process.GetCurrentProcess();
            sb.AppendLine($"Memory (GC)   : {GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):F2} MB");
            sb.AppendLine($"Working Set   : {currentProcess.WorkingSet64 / (1024.0 * 1024.0):F2} MB (Peak: {currentProcess.PeakWorkingSet64 / (1024.0 * 1024.0):F2} MB)");
            sb.AppendLine($"Private Bytes : {currentProcess.PrivateMemorySize64 / (1024.0 * 1024.0):F2} MB");
            sb.AppendLine($"Threads/Handles: {currentProcess.Threads.Count} threads / {currentProcess.HandleCount} handles");
        }
        catch
        {
            sb.AppendLine($"Memory (GC)   : {GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0):F2} MB");
        }

        // Thread details
        var currentThread = Thread.CurrentThread;
        sb.AppendLine($"Thread        : ID {currentThread.ManagedThreadId}, Name: '{(string.IsNullOrEmpty(currentThread.Name) ? "<unnamed>" : currentThread.Name)}', ThreadPool: {currentThread.IsThreadPoolThread}, Background: {currentThread.IsBackground}");

        // Crash categorization
        sb.AppendLine(new string('-', 80));
        sb.AppendLine($"Source        : {source}");
        sb.AppendLine($"Terminating   : {isTerminating}");
        if (!string.IsNullOrWhiteSpace(context))
        {
            sb.AppendLine($"Context       : {context}");
        }
        sb.AppendLine(new string('-', 80));

        // Exception details
        if (exceptionOrError is Exception ex)
        {
            FormatExceptionChain(sb, ex);
        }
        else if (exceptionOrError != null)
        {
            sb.AppendLine($"Non-CLS Exception Object: [{exceptionOrError.GetType().FullName}]");
            sb.AppendLine(exceptionOrError.ToString());
        }
        else
        {
            sb.AppendLine("No exception or error object was provided.");
        }

        sb.AppendLine(new string('=', 80));
        sb.AppendLine();

        return sb.ToString();
    }

    private static void FormatExceptionChain(StringBuilder sb, Exception rootEx)
    {
        int depth = 1;
        Exception? current = rootEx;

        while (current != null)
        {
            string indent = depth == 1 ? string.Empty : $"[Inner #{depth - 1}] ";
            sb.AppendLine($"{indent}Exception Type: {current.GetType().FullName}");
            sb.AppendLine($"{indent}Message       : {current.Message}");
            sb.AppendLine($"{indent}HResult       : 0x{current.HResult:X8} ({current.HResult})");

            if (!string.IsNullOrWhiteSpace(current.Source))
            {
                sb.AppendLine($"{indent}Source        : {current.Source}");
            }

            if (current.TargetSite != null)
            {
                sb.AppendLine($"{indent}Target Site   : {current.TargetSite.DeclaringType?.FullName}.{current.TargetSite.Name}");
            }

            if (current is Win32Exception win32Ex)
            {
                sb.AppendLine($"{indent}Win32 Error   : {win32Ex.NativeErrorCode} (0x{win32Ex.NativeErrorCode:X8}) - {win32Ex.Message}");
            }

            if (current is ReflectionTypeLoadException typeLoadEx)
            {
                sb.AppendLine($"{indent}Loader Exceptions ({typeLoadEx.LoaderExceptions.Length}):");
                for (int i = 0; i < typeLoadEx.LoaderExceptions.Length; i++)
                {
                    var loaderEx = typeLoadEx.LoaderExceptions[i];
                    sb.AppendLine($"{indent}  [{i + 1}] {loaderEx?.GetType().Name}: {loaderEx?.Message}");
                }
            }

            if (current.Data.Count > 0)
            {
                sb.AppendLine($"{indent}Exception Data:");
                foreach (DictionaryEntry entry in current.Data)
                {
                    sb.AppendLine($"{indent}  {entry.Key} = {entry.Value}");
                }
            }

            if (!string.IsNullOrWhiteSpace(current.StackTrace))
            {
                sb.AppendLine($"{indent}Stack Trace:");
                sb.AppendLine(current.StackTrace);
            }
            else
            {
                sb.AppendLine($"{indent}Stack Trace   : <not available>");
            }

            // Handle AggregateException special flattening
            if (current is AggregateException aggEx && aggEx.InnerExceptions.Count > 1)
            {
                sb.AppendLine($"{indent}AggregateException contains {aggEx.InnerExceptions.Count} inner exceptions:");
                int childIndex = 1;
                foreach (var child in aggEx.InnerExceptions)
                {
                    sb.AppendLine($"{indent}--- Child Exception #{childIndex++} ---");
                    FormatExceptionChain(sb, child);
                }
                break;
            }

            current = current.InnerException;
            depth++;

            if (current != null)
            {
                sb.AppendLine();
            }
        }
    }

    private static void LogDirectToDisk(string source, object? exceptionOrError, string? context, bool isTerminating)
    {
        string report = FormatCrashReport(source, exceptionOrError, context, isTerminating);

        // Mirror to console/debug streams
        try
        {
            Console.Error.Write(report);
            Trace.TraceError("{0}", report);
        }
        catch
        {
            // Ignore console writing failures
        }

        lock (_crashLock)
        {
            // Primary path: %APPDATA%\V-Notch\vnotch-crash.log
            string primaryPath = CrashLogPath;
            bool written = TryAppendToFile(primaryPath, report);

            // Secondary path: AppContext.BaseDirectory\vnotch-crash.log (if different from primary)
            string appDir = AppContext.BaseDirectory;
            string secondaryPath = Path.Combine(appDir, CrashLogFileName);
            if (!string.Equals(primaryPath, secondaryPath, StringComparison.OrdinalIgnoreCase))
            {
                TryAppendToFile(secondaryPath, report);
            }

            // If primary failed, write to temp directory as guaranteed fallback
            if (!written)
            {
                string tempFallback = Path.Combine(Path.GetTempPath(), CrashLogFileName);
                TryAppendToFile(tempFallback, report);
            }
        }

        // Also forward to RuntimeLog if it is initialized and source didn't originate from RuntimeLog
        try
        {
            if (RuntimeLog.IsEnabled(LogLevel.Error) && !source.StartsWith("RuntimeLog", StringComparison.OrdinalIgnoreCase))
            {
                if (exceptionOrError is Exception ex)
                {
                    RuntimeLog.Error($"CRASH:{source}", ex, context);
                }
                else if (exceptionOrError != null)
                {
                    RuntimeLog.Error($"CRASH:{source}", $"{context}: {exceptionOrError}");
                }
            }
        }
        catch
        {
            // Ignore secondary logging failures
        }
    }

    private static bool TryAppendToFile(string filePath, string content)
    {
        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            RotateIfNeeded(filePath);

            using var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(stream, _utf8WithoutBom);
            writer.Write(content);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RotateIfNeeded(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;

            var info = new FileInfo(filePath);
            if (info.Length <= MaxCrashLogSizeBytes) return;

            var backupPath = filePath + ".old";
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
            File.Move(filePath, backupPath);
        }
        catch
        {
            // Ignore rotation failures (e.g. file lock)
        }
    }

    private static string ResolvePrimaryCrashLogPath()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
            {
                var folder = Path.Combine(appData, "V-Notch");
                return Path.Combine(folder, CrashLogFileName);
            }
        }
        catch
        {
            // Fallback
        }

        try
        {
            return Path.Combine(AppContext.BaseDirectory, CrashLogFileName);
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), CrashLogFileName);
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m {duration.Seconds}s";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{duration.TotalSeconds:F1}s";
    }

    private static void OnUnhandledDomainException(object? sender, UnhandledExceptionEventArgs args)
    {
        LogCrash("AppDomain.UnhandledException", args.ExceptionObject,
            "Unhandled exception on background or worker thread", isTerminating: args.IsTerminating);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        LogCrash("TaskScheduler.UnobservedTaskException", args.Exception,
            "Unobserved task exception finalized by Garbage Collector", isTerminating: false);
    }
}
