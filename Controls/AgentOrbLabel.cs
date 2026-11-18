using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch.Controls;

/// <summary>Two text layers follow the orb's exact transition clock; no extra timers.</summary>
public sealed class AgentOrbLabel : ContentControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ThinkingOrb), typeof(AgentOrbLabel),
        new PropertyMetadata(null, (owner, _) => ((AgentOrbLabel)owner).Attach()));

    private readonly TextBlock _current = new(), _outgoing = new();
    private readonly TranslateTransform _currentSlide = new(), _outgoingSlide = new();
    private ThinkingOrb? _attached;
    private readonly Dictionary<ThinkingOrbState, string> _labels = new();

    public ThinkingOrb? Source { get => (ThinkingOrb?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    internal TextBlock CurrentLabel => _current;
    internal TextBlock OutgoingLabel => _outgoing;

    public AgentOrbLabel()
    {
        Focusable = IsHitTestVisible = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        var layers = new Grid { ClipToBounds = true };
        _current.RenderTransform = _currentSlide;
        _outgoing.RenderTransform = _outgoingSlide;
        _current.VerticalAlignment = _outgoing.VerticalAlignment = VerticalAlignment.Center;
        _outgoing.Visibility = Visibility.Hidden;
        layers.Children.Add(_outgoing);
        layers.Children.Add(_current);
        Content = layers;
        Loaded += (_, _) => { RefreshLocalization(); Attach(); };
        Unloaded += (_, _) => Detach();
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    internal void RefreshLocalization()
    {
        foreach (var state in new[] { ThinkingOrbState.Searching, ThinkingOrbState.Shaping,
                     ThinkingOrbState.Listening, ThinkingOrbState.Solving })
            _labels[state] = Loc.Get("spotlight.ai.agent." + state.ToString().ToLowerInvariant());
        // Reserve the widest label up front so cycling cannot move nearby controls.
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        MinWidth = Math.Ceiling(_labels.Values.Max(text => new FormattedText(text,
            CultureInfo.CurrentUICulture, FlowDirection, typeface, FontSize, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace)) + 2;
        if (Source is { } orb) Present(orb.AgentTransition);
    }

    private void Attach()
    {
        Detach();
        if (Source is not { } orb) return;
        if (_labels.Count == 0) RefreshLocalization();
        Present(orb.AgentTransition);
        if (!IsLoaded) return;
        _attached = orb;
        orb.AgentTransitionChanged += Present;
        foreach (var label in new[] { _current, _outgoing })
            label.SetBinding(TextShimmer.IsActiveProperty, new Binding(nameof(ThinkingOrb.IsActive)) { Source = orb });
    }

    private void Detach()
    {
        if (_attached != null) _attached.AgentTransitionChanged -= Present;
        _attached = null;
        foreach (var label in new[] { _current, _outgoing })
        {
            BindingOperations.ClearBinding(label, TextShimmer.IsActiveProperty);
            TextShimmer.SetIsActive(label, false);
        }
    }

    private void Present(AgentOrbTransition frame)
    {
        string text = _labels.GetValueOrDefault(frame.State, Loc.Get("spotlight.ai.thinking"));
        if (_current.Text != text)
        {
            _current.Text = text;
            AutomationProperties.SetName(this, text);
        }
        bool transitioning = frame.Previous != null && frame.Progress < 1;
        _outgoing.Text = frame.Previous is { } previous ? _labels.GetValueOrDefault(previous, "") : "";
        _outgoing.Visibility = transitioning ? Visibility.Visible : Visibility.Hidden;
        _current.Opacity = frame.Progress;
        _outgoing.Opacity = 1 - frame.Progress;
        _currentSlide.Y = (1 - frame.Progress) * 8;
        _outgoingSlide.Y = -frame.Progress * 8;
    }
}
