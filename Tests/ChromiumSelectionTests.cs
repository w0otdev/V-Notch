using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class ChromiumSelectionTests
{
    [Fact]
    public void ChromiumReadsExactDomSelectionAcrossMessageNodesAndRejectsUnselectedText() => RunChromiumAsync(async () =>
    {
        using var view = new WebView2();
        string profile = Path.Combine(Path.GetTempPath(), "VNotchSelectionTests", Guid.NewGuid().ToString("N"));
        var browserExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CoreWebView2Environment? environment = null;
        var window = new Window { Content = view, Width = 800, Height = 600, ShowActivated = false, ShowInTaskbar = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show();
            environment = await CoreWebView2Environment.CreateAsync(null, profile);
            environment.BrowserProcessExited += (_, _) => browserExited.TrySetResult();
            await view.EnsureCoreWebView2Async(environment);
            var loaded = new TaskCompletionSource();
            view.NavigationCompleted += (_, e) => { if (e.IsSuccess) loaded.TrySetResult(); else loaded.TrySetException(new InvalidOperationException(e.WebErrorStatus.ToString())); };
            view.NavigateToString("""
                <!doctype html><meta charset="utf-8"><title>Selection fixture</title>
                <p>Unselected heading and channel name</p>
                <div role="log"><div id="first">Prefix <span id="start">selected Tiếng Việt</span></div>
                <div id="second"><span id="end">second message</span> suffix</div></div>
                <div contenteditable="true" role="textbox" id="draft">Unselected draft</div>
                <script>
                function selectMessages() {
                  const r = document.createRange();
                  r.setStart(document.getElementById('start').firstChild, 2);
                  r.setEnd(document.getElementById('end').firstChild, 6);
                  const s = getSelection(); s.removeAllRanges(); s.addRange(r);
                  return s.toString();
                }
                </script>
                """);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var hwnd = new WindowInteropHelper(window).Handle;
            string expected = JsonSerializer.Deserialize<string>(await view.ExecuteScriptAsync("selectMessages()"))!;
            TranslationSelection? cold = null;
            // Exercise the production shortcut's bounded retries without a warm
            // accessibility tree, event element, focus, or cursor inside the view.
            await TranslationSelectionService.CaptureManualAsync(() => true, () =>
            {
                using var result = SelectionTextReader.Read(hwnd, null, true, out _, allowOwnProcess: true);
                return result == null ? null : new TranslationSelection(1, result.Text, result.Bounds, hwnd, false);
            }, selection => cold = selection);
            Assert.NotNull(cold);
            Assert.Equal(expected.Replace("\r\n", "\n"), cold.Text.Replace("\r\n", "\n"));
            var root = AutomationElement.FromHandle(hwnd);
            AutomationElement? leaf = null;
            for (int attempt = 0; attempt < 20 && leaf == null; attempt++)
            {
                leaf = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, "selected Tiếng Việt"));
                if (leaf == null) await Task.Delay(100);
            }
            Assert.True(leaf != null, string.Join(" | ", root.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                .Cast<AutomationElement>().Select(e => $"{e.Current.ControlType.ProgrammaticName}: {e.Current.Name}")));
            await Task.Delay(150);
            foreach (bool manual in new[] { false, true })
            foreach (var seed in new[] { leaf, null })
            {
                using var result = SelectionTextReader.Read(hwnd, seed, manual, out bool protectedInput, allowOwnProcess: true);
                Assert.False(protectedInput);
                Assert.NotNull(result);
                Assert.Equal(expected.Replace("\r\n", "\n"), result.Text.Replace("\r\n", "\n"));
            }
            await view.ExecuteScriptAsync("getSelection().removeAllRanges()");
            await Task.Delay(100);
            using var empty = SelectionTextReader.Read(hwnd, leaf, true, out _, allowOwnProcess: true);
            Assert.Null(empty);

            await view.ExecuteScriptAsync("""
                const draft = document.getElementById('draft'); draft.focus();
                const caret = document.createRange(); caret.setStart(draft.firstChild, 4); caret.collapse(true);
                getSelection().removeAllRanges(); getSelection().addRange(caret);
                """);
            await Task.Delay(100);
            using var caretOnly = SelectionTextReader.Read(hwnd, null, true, out _, allowOwnProcess: true);
            Assert.Null(caretOnly);

            await view.ExecuteScriptAsync("""
                const selected = document.createRange(); selected.setStart(draft.firstChild, 2); selected.setEnd(draft.firstChild, 10);
                getSelection().removeAllRanges(); getSelection().addRange(selected);
                """);
            await Task.Delay(100);
            using var draftSelection = SelectionTextReader.Read(hwnd, null, true, out _, allowOwnProcess: true);
            Assert.NotNull(draftSelection);
            Assert.Equal("selected", draftSelection.Text);

            await view.ExecuteScriptAsync("document.body.innerHTML = '<p>New channel without a selection</p>'");
            await Task.Delay(100);
            using var changedChannel = SelectionTextReader.Read(hwnd, null, true, out _, allowOwnProcess: true, previousSelection: draftSelection.Native);
            Assert.Null(changedChannel);
        }
        finally
        {
            view.Dispose();
            window.Close();
            if (environment != null)
            {
                try { await browserExited.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException) { }
            }
            try { if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true); }
            catch (IOException) { /* Browser shutdown may still be releasing its profile. */ }
            catch (UnauthorizedAccessException) { }
        }
    });

    private static void RunChromiumAsync(Func<Task> action)
    {
        // WebView2's browser process is created on the process desktop and cannot
        // attach to SharedStaTestRunner's thread-only private desktop. All fixture
        // windows remain transparent, NOACTIVATE and input-transparent; this test
        // changes selection through its own DOM and sends no desktop input.
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completed.TrySetResult(); }
                catch (Exception ex) { completed.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); }
            });
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Chromium selection fixture" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        completed.Task.WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        Assert.True(thread.Join(3000));
    }
}
