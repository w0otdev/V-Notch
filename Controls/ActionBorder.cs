using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;

namespace VNotch.Controls;

// Preserve the existing border visuals while exposing button semantics to UIA.
public class ActionBorder : Border
{
    public event MouseButtonEventHandler? Click;

    public ActionBorder() => Focusable = true;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.Handled) return;
        Focus();
        Click?.Invoke(this, e);
        e.Handled = true;
    }

    internal void InvokeAction()
    {
        if (!IsEnabled) throw new ElementNotEnabledException();
        Click?.Invoke(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = MouseLeftButtonDownEvent,
            Source = this
        });
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            if (!e.IsRepeat) InvokeAction();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ActionBorderPeer(this);

    private sealed class ActionBorderPeer(ActionBorder owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(ActionBorder);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetNameCore() => string.IsNullOrWhiteSpace(base.GetNameCore())
            ? owner.ToolTip as string ?? string.Empty : base.GetNameCore();
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Invoke
            ? this : base.GetPattern(patternInterface);
        public void Invoke()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.Dispatcher.BeginInvoke(new Action(owner.InvokeAction));
        }
    }
}
