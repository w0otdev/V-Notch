using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using VNotch.Services;

namespace VNotch.Controls;

public sealed partial class ThinkingOrb
{
    public static readonly DependencyProperty RandomizeStatesProperty = DependencyProperty.Register(
        nameof(RandomizeStates), typeof(bool), typeof(ThinkingOrb),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnRandomizeStatesChanged));

    private static readonly SolidColorBrush[] AgentBrushes = Enumerable.Range(0, 256).Select(gray =>
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)gray, (byte)gray, (byte)gray));
        brush.Freeze();
        return brush;
    }).ToArray();
    private readonly DrawingVisual _outgoingAgent = new(), _incomingAgent = new();
    private readonly BlurEffect _outgoingBlur = new() { Radius = 0 }, _incomingBlur = new() { Radius = 0 };
    private readonly TranslateTransform _outgoingSlide = new(), _incomingSlide = new();
    private AgentOrbSequence _agentSequence = new();
    private ThinkingOrbState? _previousAgentState;
    private double _agentChangedAt, _agentTimeOffset;
    private bool _continueAgent;
    internal AgentOrbTransition AgentTransition { get; private set; }
    internal event Action<AgentOrbTransition>? AgentTransitionChanged;

    public bool RandomizeStates { get => (bool)GetValue(RandomizeStatesProperty); set => SetValue(RandomizeStatesProperty, value); }
    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _outgoingAgent, 1 => _incomingAgent, _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private void InitializeAgentLayers()
    {
        AddVisualChild(_outgoingAgent);
        AddVisualChild(_incomingAgent);
        _outgoingAgent.Transform = _outgoingSlide;
        _incomingAgent.Transform = _incomingSlide;
    }

    private static void OnRandomizeStatesChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var orb = (ThinkingOrb)sender;
        if ((bool)args.NewValue) orb.SetCurrentValue(StateProperty, ThinkingOrbState.Searching);
        else
        {
            using (orb._outgoingAgent.RenderOpen()) { }
            using (orb._incomingAgent.RenderOpen()) { }
        }
        if (orb.IsActive) orb.UpdateAgentActivity(true);
        orb.PublishAgentTransition();
    }

    // Keep the same shuffled cycle and phase when the waiting row hands off
    // to the streaming footer. Both still use the control's visibility clock.
    internal void ContinueAgentFrom(ThinkingOrb source)
    {
        _agentSequence = source._agentSequence;
        _agentTimeOffset = source._agentTimeOffset + source._clock.Elapsed.TotalSeconds;
        _agentChangedAt = source._agentChangedAt;
        _previousAgentState = source._previousAgentState;
        SetCurrentValue(StateProperty, source.State);
        _continueAgent = true;
        PublishAgentTransition();
    }

    private void UpdateAgentActivity(bool active)
    {
        if (!RandomizeStates) return;
        if (!active)
        {
            _agentTimeOffset += _clock.Elapsed.TotalSeconds;
            _continueAgent = true;
            return;
        }
        if (!_continueAgent)
        {
            SetCurrentValue(StateProperty, _agentSequence.Next());
            _agentTimeOffset = _agentChangedAt = 0;
            _previousAgentState = null;
        }
        _continueAgent = false;
    }

    private void AdvanceAgentState(double now)
    {
        if (!RandomizeStates || now - _agentChangedAt < AgentOrbSequence.HoldSeconds) return;
        _previousAgentState = State;
        _agentChangedAt = now;
        SetCurrentValue(StateProperty, _agentSequence.Next());
    }

    private void RenderAgent(double size)
    {
        double now = _clock.Elapsed.TotalSeconds + _agentTimeOffset;
        double eased = AgentTransition.Progress;
        _incomingSlide.Y = (1 - eased) * 8;
        _outgoingSlide.Y = -eased * 8;
        DrawAgentLayer(_incomingAgent, _incomingBlur, State, size, now, eased, (1 - eased) * 8 * size / 40);
        if (eased < 1 && AgentTransition.Previous is { } previous)
            DrawAgentLayer(_outgoingAgent, _outgoingBlur, previous, size, now, 1 - eased, eased * 8 * size / 40);
        else
        {
            using (_outgoingAgent.RenderOpen()) { }
            _outgoingAgent.Effect = null;
        }
    }

    private void PublishAgentTransition()
    {
        if (!RandomizeStates) return;
        double now = _clock.Elapsed.TotalSeconds + _agentTimeOffset;
        double progress = AnimationConfig.ReduceMotion || _previousAgentState == null ? 1 :
            Math.Clamp((now - _agentChangedAt) / AgentOrbSequence.TransitionSeconds, 0, 1);
        if (progress >= 1) _previousAgentState = null;
        var transition = new AgentOrbTransition(State, _previousAgentState,
            progress >= 1 ? 1 : AgentTransitionEase(progress));
        if (transition == AgentTransition) return;
        AgentTransition = transition;
        AgentTransitionChanged?.Invoke(transition);
    }

    private void DrawAgentLayer(DrawingVisual visual, BlurEffect blur, ThinkingOrbState state,
        double size, double seconds, double opacity, double blurRadius)
    {
        visual.Opacity = opacity;
        blur.Radius = blurRadius;
        visual.Effect = blurRadius > .01 ? blur : null;
        int count = AgentOrbGeometry.WriteFrame(_dots, state, size, seconds);
        double x = (ActualWidth - size) / 2, y = (ActualHeight - size) / 2;
        using var dc = visual.RenderOpen();
        for (int i = 0; i < count; i++)
        {
            var dot = _dots[i];
            int gray = (int)Math.Floor(Math.Clamp(1 - dot.Ink, 0, 1) * 255 + .5);
            dc.PushOpacity(dot.Alpha);
            dc.DrawEllipse(AgentBrushes[gray], null, new Point(dot.X + x, dot.Y + y), dot.Radius * DotScale, dot.Radius * DotScale);
            dc.Pop();
        }
    }

    // The demo's cubic-bezier(.16, 1, .3, 1), including its x-to-time mapping.
    private static double AgentTransitionEase(double progress)
    {
        double low = 0, high = 1;
        for (int i = 0; i < 16; i++)
        {
            double t = (low + high) / 2, inverse = 1 - t;
            double x = 3 * inverse * inverse * t * .16 + 3 * inverse * t * t * .3 + t * t * t;
            if (x < progress) low = t; else high = t;
        }
        return 1 - Math.Pow(1 - (low + high) / 2, 3);
    }
}
