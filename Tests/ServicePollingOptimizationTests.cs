using System.Reflection;
using System.Runtime.InteropServices;
using VNotch.Services;
using Xunit;

namespace VNotch.Tests;

public sealed class ServicePollingOptimizationTests
{
    [Theory]
    [InlineData("Song - YOUTUBE", true)]
    [InlineData("HÌNH TRONG HÌNH", true)]
    [InlineData("播放 - 哔哩哔哩", true)]
    [InlineData("Песня - КАРТИНКА В КАРТИНКЕ", true)]
    [InlineData("Untitled - Notepad", false)]
    [InlineData(" \t\r\n", false)]
    [InlineData("", false)]
    public void TitleFilteringKeepsOrdinalCaseInsensitiveMatching(string title, bool expected)
    {
        Assert.Equal(expected, WindowTitleScanner.MatchesPlatformTitle(title));
    }

    [Theory]
    [InlineData("Progman", true)]
    [InlineData("Windows.UI.Core.CoreWindow", true)]
    [InlineData("MSCTFIME UI", true)]
    [InlineData("progman", false)]
    [InlineData("ProgmanGame", false)]
    [InlineData("Static", false)]
    [InlineData("", false)]
    public void BlockedClassesKeepExactAndPrefixRules(string className, bool expected)
    {
        Assert.Equal(expected, FullscreenDetector.IsBlockedClassName(className));
    }

    [Fact]
    public void NativeBuffersHandleGrowthShorterTitlesAndEmptyTitles()
    {
        using var scanner = new WindowTitleScanner();
        var addTitle = CreateTitleReader(scanner);
        string longTitle = new string('界', 600) + " - YouTube";
        IntPtr window = CreateWindow(longTitle);
        try
        {
            var titles = new List<string>();
            addTitle(window, titles);
            Assert.Equal(longTitle, Assert.Single(titles));

            titles.Clear();
            Assert.True(SetWindowText(window, "Spotify"));
            addTitle(window, titles);
            Assert.Equal("Spotify", Assert.Single(titles));

            titles.Clear();
            Assert.True(SetWindowText(window, "Notes"));
            addTitle(window, titles);
            Assert.Empty(titles);
            Assert.True(SetWindowText(window, ""));
            addTitle(window, titles);
            addTitle(IntPtr.Zero, titles);
            Assert.Empty(titles);
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    [Fact]
    public void NativeTitleRejectionAndClassChecksAllocateNothingAfterWarmup()
    {
        using var scanner = new WindowTitleScanner();
        var addTitle = CreateTitleReader(scanner);
        var titles = new List<string>();
        IntPtr window = CreateWindow("Untitled - Notepad");
        try
        {
            for (int i = 0; i < 100; i++)
            {
                addTitle(window, titles);
                FullscreenDetector.IsBlockedClass(window);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            bool blocked = false;
            for (int i = 0; i < 1000; i++)
            {
                addTitle(window, titles);
                blocked |= FullscreenDetector.IsBlockedClass(window);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Empty(titles);
            Assert.False(blocked);
            Assert.Equal(0, allocated);
        }
        finally
        {
            DestroyWindow(window);
        }
    }

    private static Action<IntPtr, List<string>> CreateTitleReader(WindowTitleScanner scanner) =>
        typeof(WindowTitleScanner).GetMethod("AddMatchingWindowTitle", BindingFlags.Instance | BindingFlags.NonPublic)!
            .CreateDelegate<Action<IntPtr, List<string>>>(scanner);

    private static IntPtr CreateWindow(string title)
    {
        IntPtr window = CreateWindowEx(0, "Static", title, 0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        return window;
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowText(IntPtr window, string text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);
}
