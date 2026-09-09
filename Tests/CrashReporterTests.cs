using System;
using System.ComponentModel;
using System.IO;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class CrashReporterTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _originalCrashLogPath;

    public CrashReporterTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"vnotch-crash-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
        _originalCrashLogPath = CrashReporter.CrashLogPath;
    }

    public void Dispose()
    {
        CrashReporter.CrashLogPath = _originalCrashLogPath;
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failure in tests
        }
    }

    [Fact]
    public void FormatCrashReport_ContainsCompleteDiagnosticInfo()
    {
        var ex = new InvalidOperationException("Test critical operation failed");

        string report = CrashReporter.FormatCrashReport(
            source: "UnitTest.Runner",
            exceptionOrError: ex,
            context: "Testing error scenario",
            isTerminating: true);

        Assert.Contains("V-NOTCH CRASH REPORT", report);
        Assert.Contains("Application   : V-Notch", report);
        Assert.Contains("Process       : PID", report);
        Assert.Contains("OS            :", report);
        Assert.Contains(".NET Runtime  :", report);
        Assert.Contains("Memory (GC)   :", report);
        Assert.Contains("Thread        : ID", report);
        Assert.Contains("Source        : UnitTest.Runner", report);
        Assert.Contains("Terminating   : True", report);
        Assert.Contains("Context       : Testing error scenario", report);
        Assert.Contains("Exception Type: System.InvalidOperationException", report);
        Assert.Contains("Message       : Test critical operation failed", report);
        Assert.Contains("HResult       :", report);
    }

    [Fact]
    public void FormatCrashReport_UnrollsInnerExceptionsAndFlattenAggregates()
    {
#pragma warning disable CA2208 // Param name is intentional for testing exception formatting
        var inner1 = new ArgumentNullException("paramA", "Param cannot be null");
#pragma warning restore CA2208
        var inner2 = new IOException("Disk write failed");
        var agg = new AggregateException("Multiple background errors", inner1, inner2);

        string report = CrashReporter.FormatCrashReport(
            source: "TaskPool.Worker",
            exceptionOrError: agg,
            context: "Batch operation",
            isTerminating: false);

        Assert.Contains("AggregateException", report);
        Assert.Contains("ArgumentNullException", report);
        Assert.Contains("Param cannot be null", report);
        Assert.Contains("IOException", report);
        Assert.Contains("Disk write failed", report);
    }

    [Fact]
    public void FormatCrashReport_HandlesNonExceptionObject()
    {
        string rawError = "Fatal native C++ runtime error code 0xDEADBEEF";

        string report = CrashReporter.FormatCrashReport(
            source: "NativeInterop",
            exceptionOrError: rawError,
            context: "Foreign thread crash",
            isTerminating: true);

        Assert.Contains("Non-CLS Exception Object: [System.String]", report);
        Assert.Contains(rawError, report);
    }

    [Fact]
    public void FormatCrashReport_HandlesWin32ExceptionWithNativeCode()
    {
        var win32Ex = new Win32Exception(5); // ERROR_ACCESS_DENIED

        string report = CrashReporter.FormatCrashReport(
            source: "SecurityCheck",
            exceptionOrError: win32Ex,
            context: "Accessing protected resource",
            isTerminating: false);

        Assert.Contains("Win32 Error   : 5", report);
    }

    [Fact]
    public void LogCrash_WritesSynchronouslyToDisk()
    {
        string testLogPath = Path.Combine(_tempDirectory, "test-crash.log");
        CrashReporter.CrashLogPath = testLogPath;

        var ex = new ApplicationException("Simulated unexpected crash");

        CrashReporter.LogCrash("UnitTests.DiskWrite", ex, "Immediate disk flush test", isTerminating: true);

        Assert.True(File.Exists(testLogPath), "Crash log file was not created on disk.");
        string contents = File.ReadAllText(testLogPath);
        Assert.Contains("Simulated unexpected crash", contents);
        Assert.Contains("UnitTests.DiskWrite", contents);
        Assert.Contains("Immediate disk flush test", contents);
    }

    [Fact]
    public void LogCrash_RotatesWhenFileSizeExceedsLimit()
    {
        string testLogPath = Path.Combine(_tempDirectory, "rotating-crash.log");
        CrashReporter.CrashLogPath = testLogPath;

        // Pre-fill file to > 2MB
        byte[] dummyData = new byte[2 * 1024 * 1024 + 100];
        File.WriteAllBytes(testLogPath, dummyData);

        var ex = new Exception("Post-rotation crash event");
        CrashReporter.LogCrash("UnitTests.Rotation", ex, isTerminating: false);

        string oldPath = testLogPath + ".old";
        Assert.True(File.Exists(oldPath), "Previous crash log was not rotated to .old");
        Assert.True(File.Exists(testLogPath), "New crash log was not created after rotation.");

        string newContents = File.ReadAllText(testLogPath);
        Assert.Contains("Post-rotation crash event", newContents);
    }

    [Fact]
    public void LogCrash_NeverThrowsEvenWhenPathIsInvalid()
    {
        CrashReporter.CrashLogPath = Path.Combine(_tempDirectory, "invalid\0name", "crash.log");

        // Should not throw any exception
        var exception = Record.Exception(() =>
        {
            CrashReporter.LogCrash("InvalidPathTest", new Exception("Safe execution test"));
        });

        Assert.Null(exception);
    }
}
