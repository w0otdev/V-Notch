using System.Windows;
using VNotch.Services.Translation;
using Xunit;

namespace VNotch.Tests;

[Collection("Localization")]
public sealed class SelectionTextReaderTests
{
    [Fact]
    public void MultipleSelectedRangesPreserveOrderAndExcludeEmptyCarets()
        => Assert.Equal("first\nsecond", SelectionTextReader.JoinSelection(["first", "", "second"]));

    [Fact]
    public void CombinedSelectionKeepsBoundedPrefixWithOverflowMarker()
    {
        Assert.Equal(new string('a', 1500) + "\n" + new string('b', 500), SelectionTextReader.JoinSelection([new string('a', 1500), new string('b', 600)]));
        Assert.Null(SelectionTextReader.JoinSelection(["", " "]));
    }

    [Fact]
    public void InvalidProviderRectanglesAreRejected()
    {
        Assert.False(SelectionTextReader.ValidBounds(Rect.Empty));
        Assert.False(SelectionTextReader.ValidBounds(new Rect(0, 0, 0, 10)));
        Assert.False(SelectionTextReader.ValidBounds(new Rect(double.PositiveInfinity, 0, 10, 10)));
        Assert.True(SelectionTextReader.ValidBounds(new Rect(-1200, 50, 300, 20)));
    }

    [Fact]
    public void NativeWindowsEditReadsOnlySelectedUnicodeText() => SharedStaTestRunner.Run(() =>
    {
        using var box = new System.Windows.Forms.TextBox { Text = "prefix Tiếng Việt suffix" };
        _ = box.Handle;
        box.Select(7, 10);
        Assert.Equal("Tiếng Việt", NativeSelectionReader.ReadControl(box.Handle));
        box.Select(0, 0);
        Assert.Null(NativeSelectionReader.ReadControl(box.Handle));
    });

    [Fact]
    public void NativeWindowsPasswordEditNeverReturnsSelection() => SharedStaTestRunner.Run(() =>
    {
        using var box = new System.Windows.Forms.TextBox { Text = "private", UseSystemPasswordChar = true };
        _ = box.Handle;
        box.SelectAll();
        Assert.Null(NativeSelectionReader.ReadControl(box.Handle));
    });

    [Theory]
    [InlineData(-1, 5)]
    [InlineData(2, 2)]
    [InlineData(0, 100)]
    public void NativeRangesMustStillMatchTheReturnedText(int start, int end)
        => Assert.Null(NativeSelectionReader.Slice("short", start, end));

    [Fact]
    public void NativeRichEditMultilineSelectionNormalizesLineEndingsAndReadsAccurately() => SharedStaTestRunner.Run(() =>
    {
        using var rtb = new System.Windows.Forms.RichTextBox { Text = "Line one\r\nLine two Tiếng Việt\r\nLine three" };
        _ = rtb.Handle;
        int idx = rtb.Text.IndexOf("Tiếng Việt", StringComparison.Ordinal);
        rtb.Select(idx, "Tiếng Việt".Length);
        Assert.Equal("Tiếng Việt", NativeSelectionReader.ReadControl(rtb.Handle));
    });

    [Fact]
    public void NativeComboBoxReadsSelectedEditPortion() => SharedStaTestRunner.Run(() =>
    {
        using var combo = new System.Windows.Forms.ComboBox
        {
            DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDown,
            Text = "Search query text"
        };
        combo.CreateControl();
        _ = combo.Handle;
        combo.SelectionStart = 7;
        combo.SelectionLength = 5;
        Assert.Equal("query", NativeSelectionReader.ReadControl(combo.Handle));
    });

    [Fact]
    public void NativeChildWindowEnumerationFindsSelectionInsideContainer() => SharedStaTestRunner.Run(() =>
    {
        using var form = new System.Windows.Forms.Form { ShowInTaskbar = false, Opacity = 0, Width = 300, Height = 200 };
        using var panel = new System.Windows.Forms.Panel { Dock = System.Windows.Forms.DockStyle.Fill };
        using var box = new System.Windows.Forms.TextBox { Text = "Nested container selection" };
        panel.Controls.Add(box);
        form.Controls.Add(panel);
        _ = form.Handle;
        _ = panel.Handle;
        _ = box.Handle;
        box.Select(7, 9);
        var selection = NativeSelectionReader.Read(form.Handle);
        Assert.NotNull(selection);
        Assert.Equal("container", selection.Text);
    });

    [Fact]
    public void SanitizeSelectionPartStripsObjectReplacementAndNullCharacters()
    {
        Assert.Equal("Hello world", SelectionTextReader.JoinSelection(["\uFFFCHello \0world\uFFFC"]));
        Assert.Null(SelectionTextReader.JoinSelection(["\uFFFC\0   \uFFFC"]));
        Assert.Equal("Valid text", TranslationInput.Prepare("\uFFFCValid text\0"));
    }

    [Fact]
    public void WpfRichTextBoxAndTextBoxExposeSelectionThroughUiaReader() => SharedStaTestRunner.Run(() =>
    {
        var rtb = new System.Windows.Controls.RichTextBox { Width = 300, Height = 120 };
        rtb.Document.Blocks.Clear();
        var p = new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run("First paragraph.\nSecond Tiếng Việt line."));
        rtb.Document.Blocks.Add(p);
        var window = new Window { Content = rtb, Width = 320, Height = 160, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show();
            rtb.SelectAll();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var result = SelectionTextReader.Read(hwnd, null, manual: true, out bool protectedInput, rootElement: root, allowOwnProcess: true);
            Assert.False(protectedInput);
            Assert.NotNull(result);
            Assert.Contains("Tiếng Việt", result.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NonPasswordAncestorOrSiblingDoesNotBlockLegitimateSelection() => SharedStaTestRunner.Run(() =>
    {
        var stack = new System.Windows.Controls.StackPanel();
        var pwd = new System.Windows.Controls.PasswordBox { Password = "secret" };
        var tb = new System.Windows.Controls.TextBox { Text = "Readable public text" };
        stack.Children.Add(pwd);
        stack.Children.Add(tb);
        var window = new Window { Content = stack, Width = 320, Height = 160, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show();
            tb.Select(9, 6);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var result = SelectionTextReader.Read(hwnd, null, manual: true, out bool protectedInput, rootElement: root, allowOwnProcess: true);
            Assert.False(protectedInput);
            Assert.NotNull(result);
            Assert.Equal("public", result.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void NativeListBoxAndDropDownListComboBoxReadSelectedItems() => SharedStaTestRunner.Run(() =>
    {
        using var listBox = new System.Windows.Forms.ListBox();
        listBox.Items.AddRange(["First row", "Selected Tiếng Việt row", "Third row"]);
        _ = listBox.Handle;
        listBox.SelectedIndex = 1;
        Assert.Equal("Selected Tiếng Việt row", NativeSelectionReader.ReadControl(listBox.Handle));

        using var multiList = new System.Windows.Forms.ListBox { SelectionMode = System.Windows.Forms.SelectionMode.MultiExtended };
        multiList.Items.AddRange(["Alpha", "Beta", "Gamma"]);
        _ = multiList.Handle;
        multiList.SetSelected(0, true);
        multiList.SetSelected(2, true);
        Assert.Equal("Alpha\nGamma", NativeSelectionReader.ReadControl(multiList.Handle));

        using var dropDownList = new System.Windows.Forms.ComboBox { DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList };
        dropDownList.Items.AddRange(["Option A", "Option B"]);
        dropDownList.CreateControl();
        _ = dropDownList.Handle;
        dropDownList.SelectedIndex = 1;
        Assert.Equal("Option B", NativeSelectionReader.ReadControl(dropDownList.Handle));
    });

    [Fact]
    public void WpfListBoxSelectedItemsAreReadViaSelectionPatternFallback() => SharedStaTestRunner.Run(() =>
    {
        var list = new System.Windows.Controls.ListBox { SelectionMode = System.Windows.Controls.SelectionMode.Multiple };
        list.Items.Add("Item one");
        list.Items.Add("Item two Tiếng Việt");
        list.SelectedItems.Add("Item two Tiếng Việt");
        var window = new Window { Content = list, Width = 320, Height = 160, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var listElement = root.FindFirst(
                System.Windows.Automation.TreeScope.Subtree,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.List));
            var result = SelectionTextReader.Read(hwnd, listElement, manual: true, out bool protectedInput, rootElement: root, allowOwnProcess: true);
            Assert.False(protectedInput);
            Assert.NotNull(result);
            Assert.Equal("Item two Tiếng Việt", result.Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void WpfTreeViewAndTextBlockFallbackReadSelectedAndStaticText() => SharedStaTestRunner.Run(() =>
    {
        var tree = new System.Windows.Controls.TreeView();
        var rootNode = new System.Windows.Controls.TreeViewItem { Header = "Parent folder", IsExpanded = true };
        var childNode = new System.Windows.Controls.TreeViewItem { Header = "Selected Tiếng Việt node", IsSelected = true };
        rootNode.Items.Add(childNode);
        tree.Items.Add(rootNode);
        var textBlock = new System.Windows.Controls.TextBlock { Text = "Static status message" };
        var stack = new System.Windows.Controls.StackPanel();
        stack.Children.Add(tree);
        stack.Children.Add(textBlock);
        var window = new Window { Content = stack, Width = 320, Height = 200, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        BackgroundTestWindows.ProtectInput(window);
        try
        {
            window.Show();
            window.UpdateLayout();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var root = System.Windows.Automation.AutomationElement.FromHandle(hwnd);
            var treeElement = root.FindFirst(
                System.Windows.Automation.TreeScope.Subtree,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty, System.Windows.Automation.ControlType.Tree));
            var treeResult = SelectionTextReader.Read(hwnd, treeElement, manual: true, out bool protectedInput, rootElement: root, allowOwnProcess: true);
            Assert.False(protectedInput);
            Assert.NotNull(treeResult);
            Assert.Equal("Selected Tiếng Việt node", treeResult.Text);

            var textElement = root.FindFirst(
                System.Windows.Automation.TreeScope.Subtree,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.NameProperty, "Static status message"));
            var textResult = SelectionTextReader.Read(hwnd, textElement, manual: true, out protectedInput, rootElement: root, allowOwnProcess: true);
            Assert.False(protectedInput);
            Assert.NotNull(textResult);
            Assert.Equal("Static status message", textResult.Text);
        }
        finally { window.Close(); }
    });
}
