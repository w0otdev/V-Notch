using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace VNotch.Tests;

/// <summary>
/// Guards the Spotlight arrow-key routing fix. A single-line TextBox consumes
/// Up/Down/PageUp/PageDown through its editing-command bindings during the
/// bubbling KeyDown, so navigation wired to the search box's KeyDown never
/// fires; it must live on the window's tunneling PreviewKeyDown instead.
/// </summary>
public sealed class SpotlightKeyRoutingTests
{
    [Theory]
    [InlineData(Key.Down)]
    [InlineData(Key.Up)]
    [InlineData(Key.PageDown)]
    [InlineData(Key.PageUp)]
    public void TextBox_KeyDown_NeverSeesNavigationKeys(Key key)
    {
        RunSta(() =>
        {
            var (source, root, searchBox) = BuildTree();
            try
            {
                bool keyDownFired = false;
                searchBox.KeyDown += (_, _) => keyDownFired = true;

                var args = MakeKeyEvent(source, key, Keyboard.KeyDownEvent);
                searchBox.RaiseEvent(args);

                // The TextBox's editing commands mark the event handled before
                // instance handlers run — this is the defect the fix works around.
                Assert.True(args.Handled);
                Assert.False(keyDownFired);
                GC.KeepAlive(root);
            }
            finally
            {
                source.Dispose();
            }
        });
    }

    [Theory]
    [InlineData(Key.Down)]
    [InlineData(Key.Up)]
    [InlineData(Key.PageDown)]
    [InlineData(Key.PageUp)]
    public void RootPreviewKeyDown_SeesNavigationKeys_BeforeTextBox(Key key)
    {
        RunSta(() =>
        {
            var (source, root, searchBox) = BuildTree();
            try
            {
                Key? seen = null;
                root.PreviewKeyDown += (_, e) =>
                {
                    seen = e.Key;
                    e.Handled = true;
                };

                searchBox.RaiseEvent(MakeKeyEvent(source, key, Keyboard.PreviewKeyDownEvent));

                Assert.Equal(key, seen);
            }
            finally
            {
                source.Dispose();
            }
        });
    }

    private static (HwndSource Source, Grid Root, TextBox SearchBox) BuildTree()
    {
        var searchBox = new TextBox { AcceptsReturn = false };
        var root = new Grid();
        root.Children.Add(searchBox);

        var parameters = new HwndSourceParameters("spotlight-key-routing-test")
        {
            Width = 100,
            Height = 100,
            PositionX = -32000,
            PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP, never shown
        };
        var source = new HwndSource(parameters) { RootVisual = root };
        root.UpdateLayout();
        return (source, root, searchBox);
    }

    private static KeyEventArgs MakeKeyEvent(PresentationSource source, Key key, RoutedEvent routedEvent) =>
        new(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = routedEvent };

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30));
        if (failure != null) throw failure;
    }
}
