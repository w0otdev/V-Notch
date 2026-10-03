using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VNotch.Controls;

public sealed class AccessibleVolumePanel : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AccessibleVolumePanel), new FrameworkPropertyMetadata(0.5,
            (d, e) =>
            {
                if (UIElementAutomationPeer.FromElement((AccessibleVolumePanel)d) is { } peer)
                    peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, e.OldValue, e.NewValue);
            }, (_, value) => Math.Clamp((double)value, 0, 1)), value => double.IsFinite((double)value));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public event RoutedPropertyChangedEventHandler<double>? ValueRequested;

    public AccessibleVolumePanel() => Focusable = true;

    internal void RequestValue(double value)
    {
        if (!IsEnabled) throw new ElementNotEnabledException();
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new ArgumentOutOfRangeException(nameof(value));
        double previous = Value;
        SetCurrentValue(ValueProperty, value);
        ValueRequested?.Invoke(this, new RoutedPropertyChangedEventArgs<double>(previous, value));
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        double direction = FlowDirection == FlowDirection.RightToLeft ? -1 : 1;
        double? next = e.Key switch
        {
            Key.Left => Value - .05 * direction,
            Key.Right => Value + .05 * direction,
            Key.Down => Value - .05,
            Key.Up => Value + .05,
            Key.PageDown => Value - .1,
            Key.PageUp => Value + .1,
            Key.Home => 0,
            Key.End => 1,
            _ => null
        };
        if (next.HasValue)
        {
            RequestValue(Math.Clamp(next.Value, 0, 1));
            e.Handled = true;
        }
        else base.OnKeyDown(e);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (IsKeyboardFocused && ActualWidth > 2 && ActualHeight > 2)
            dc.DrawRectangle(null, new Pen(SystemColors.HighlightBrush, 1), new Rect(1, 1, ActualWidth - 2, ActualHeight - 2));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new VolumePeer(this);

    private sealed class VolumePeer(AccessibleVolumePanel owner) : FrameworkElementAutomationPeer(owner), IRangeValueProvider
    {
        protected override string GetClassNameCore() => nameof(AccessibleVolumePanel);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Slider;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.RangeValue
            ? this : base.GetPattern(patternInterface);
        public bool IsReadOnly => false;
        public double LargeChange => .1;
        public double SmallChange => .05;
        public double Maximum => 1;
        public double Minimum => 0;
        public double Value => owner.Value;
        public void SetValue(double value) => owner.RequestValue(value);
    }
}
