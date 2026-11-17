using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SelectionTabSwitchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SwitchingTabsReadsCurrentSelectionWhilePreviousTabKeepsItsHighlight(bool manual) => SharedStaTestRunner.Run(() =>
    {
        var firstText = new TextBox { Text = "First tab selection", Height = 80 };
        var secondText = new TextBox { Text = "Second tab selection", Height = 80 };
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "First", Content = firstText });
        tabs.Items.Add(new TabItem { Header = "Second", Content = secondText });
        tabs.SelectedIndex = 0;
        var window = new Window { Content = tabs, Width = 400, Height = 220 };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show(); window.UpdateLayout(); firstText.SelectAll();
            var hwnd = new WindowInteropHelper(window).Handle;
            using var first = SelectionTextReader.Read(hwnd, null, manual, out _, allowOwnProcess: true);
            Assert.NotNull(first);
            Assert.Equal(firstText.Text, first.Text);

            tabs.SelectedIndex = 1;
            window.UpdateLayout(); secondText.SelectAll();
            Assert.Equal(firstText.Text, firstText.SelectedText);
            using var second = SelectionTextReader.Read(hwnd, null, manual, out _, allowOwnProcess: true, previousSelection: first.Native);
            Assert.NotNull(second);
            Assert.Equal(secondText.Text, second.Text);
            Assert.False(first.Native.SameSelection(second.Native));

            tabs.SelectedIndex = 0;
            window.UpdateLayout();
            Assert.Equal(secondText.Text, secondText.SelectedText);
            using var returned = SelectionTextReader.Read(hwnd, null, manual, out _, allowOwnProcess: true, previousSelection: second.Native);
            Assert.NotNull(returned);
            Assert.Equal(firstText.Text, returned.Text);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HiddenSelectedTextDoesNotOverrideTheVisibleSelection(bool manual) => SharedStaTestRunner.Run(() =>
    {
        var oldText = new TextBox { Text = "Old hidden selection", Height = 80 };
        var currentText = new TextBox { Text = "Current visible selection", Height = 80 };
        var panel = new StackPanel();
        panel.Children.Add(oldText); panel.Children.Add(currentText);
        var window = new Window { Content = panel, Width = 400, Height = 220 };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show(); window.UpdateLayout(); oldText.SelectAll();
            var hwnd = new WindowInteropHelper(window).Handle;
            using var previous = SelectionTextReader.Read(hwnd, null, manual, out _, allowOwnProcess: true);
            Assert.NotNull(previous);
            Assert.Equal(oldText.Text, previous.Text);

            oldText.Visibility = Visibility.Hidden;
            window.UpdateLayout(); currentText.SelectAll();
            Assert.Equal(oldText.Text, oldText.SelectedText);
            using var current = SelectionTextReader.Read(hwnd, null, manual, out _, allowOwnProcess: true, previousSelection: previous.Native);
            Assert.NotNull(current);
            Assert.Equal(currentText.Text, current.Text);

            using var fresh = SelectionTextReader.Read(hwnd, null, manual, out _, allowOwnProcess: true);
            Assert.NotNull(fresh);
            Assert.Equal(currentText.Text, fresh.Text);
        }
        finally { window.Close(); }
    });
}
