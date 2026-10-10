using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using VNotch.Services.Translation;

internal static class SelectionSmoke
{
    internal static void RunHost()
    {
        RequireDisposableSession();
        var thread = new Thread(() =>
        {
            var app = new Application();
            var input = new TextBox { Text = "Before Invoice 42 After", AcceptsReturn = true, FontSize = 16 };
            AutomationProperties.SetAutomationId(input, "translation-smoke-input");
            var window = new Window { Title = "VNotch translation test fixture", Content = input, Width = 380, Height = 110,
                Left = 20, Top = 20, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
            window.Loaded += (_, _) =>
            {
                var handle = new WindowInteropHelper(window).Handle;
                ShowWindow(handle, 5);
                SetForegroundWindow(handle);
                window.Activate(); input.Focus(); input.Select(7, 10);
                Console.WriteLine($"HOST {handle.ToInt64()} foreground={GetForegroundWindow().ToInt64()}");
                // Close an abandoned test host without touching any user application.
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
                timer.Tick += (_, _) => { timer.Stop(); window.Close(); }; timer.Start();
            };
            app.Run(window);
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
    }

    internal static async Task RunAsync()
    {
        RequireDisposableSession();
        IntPtr priorWindow = GetForegroundWindow();
        string executable = Environment.ProcessPath!;
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "VNotch.TranslationBench.dll"));
        start.ArgumentList.Add("--uia-host");
        using var selection = new TranslationSelectionService();
        var captured = new TaskCompletionSource<TranslationSelection>(TaskCreationOptions.RunContinuationsAsynchronously);
        selection.SelectionChanged += item => { if (item?.Text == "Invoice 42") captured.TrySetResult(item); };
        using var host = Process.Start(start) ?? throw new InvalidOperationException("Cannot start fixture.");
        try
        {
            string? hostState = await host.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Console.WriteLine(hostState);
            TranslationSelection snapshot;
            try { snapshot = await captured.Task.WaitAsync(TimeSpan.FromSeconds(12)); }
            catch (TimeoutException)
            {
                await Task.Run(() =>
                {
                    host.Refresh();
                    Console.WriteLine($"Fixture={host.MainWindowHandle.ToInt64()} foreground={GetForegroundWindow().ToInt64()}");
                    var root = AutomationElement.FromHandle(host.MainWindowHandle);
                    var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "translation-smoke-input"));
                    Console.WriteLine($"Patterns: {string.Join(',', element.GetSupportedPatterns().Select(p => p.ProgrammaticName))}");
                    if (element.TryGetCurrentPattern(TextPattern.Pattern, out var found))
                    {
                        var ranges = ((TextPattern)found).GetSelection();
                        Console.WriteLine($"Selection count={ranges.Length} text={ranges.FirstOrDefault()?.GetText(30)} rectangles={ranges.FirstOrDefault()?.GetBoundingRectangles().Length} readOnly={ranges.FirstOrDefault()?.GetAttributeValue(TextPattern.IsReadOnlyAttribute)}");
                    }
                });
                throw;
            }
            if (!snapshot.CanReplace) throw new InvalidOperationException("Editable selection was marked read-only.");
            if (!await selection.ReplaceAsync(snapshot, "Hóa đơn 42", CancellationToken.None)) throw new InvalidOperationException("Replacement failed.");
            var result = await Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(snapshot.Window);
                var element = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "translation-smoke-input"));
                return ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
            });
            if (result != "Before Hóa đơn 42 After") throw new InvalidOperationException("Replacement modified text outside the selection.");
            if (await selection.ReplaceAsync(snapshot, "stale", CancellationToken.None)) throw new InvalidOperationException("Stale selection was accepted.");
            Console.WriteLine("UIA smoke: selection capture, editable replacement and stale rejection passed.");
        }
        finally
        {
            host.CloseMainWindow();
            if (!host.WaitForExit(3000)) host.Kill();
            if (priorWindow != IntPtr.Zero) SetForegroundWindow(priorWindow);
        }
    }

    private static void RequireDisposableSession()
    {
        if (Environment.UserInteractive)
            throw new InvalidOperationException("Foreground UIA smoke tests are disabled on the user's desktop. Use a disposable non-interactive Windows test session.");
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
}
