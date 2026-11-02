using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Effects;

namespace VNotch.Controls;

/// <summary>Keeps an animation's BlurEffect alive without an idle offscreen effect surface.</summary>
public static class ActiveBlur
{
    private static readonly ConditionalWeakTable<UIElement, Subscription> Subscriptions = new();
    public static readonly DependencyProperty BlurProperty = DependencyProperty.RegisterAttached(
        "Blur", typeof(BlurDefinition), typeof(ActiveBlur), new PropertyMetadata(null, Changed));
    public static void SetBlur(UIElement element, BlurDefinition? value) => element.SetValue(BlurProperty, value);
    public static BlurDefinition? GetBlur(UIElement element) => (BlurDefinition?)element.GetValue(BlurProperty);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not UIElement element) return;
        if (Subscriptions.TryGetValue(element, out var previous)) previous.Dispose();
        Subscriptions.Remove(element);
        if (args.NewValue is BlurDefinition { Effect: { } effect })
            Subscriptions.Add(element, new Subscription(element, effect));
        else element.Effect = null;
    }

    private sealed class Subscription : IDisposable
    {
        private readonly UIElement _element;
        private readonly BlurEffect _effect;
        private bool _pending, _disposed;
        internal Subscription(UIElement element, BlurEffect effect)
        {
            _element = element; _effect = effect;
            effect.Changed += Update;
            Synchronize();
        }
        private void Update(object? sender, EventArgs args)
        {
            // Freezable is enumerating inheritance contexts during Changed. Altering
            // UIElement.Effect here would mutate that enumeration. Coalesce to render.
            if (_pending || _disposed || _element.Dispatcher.HasShutdownStarted) return;
            _pending = true;
            _element.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () =>
            {
                _pending = false;
                if (!_disposed) Synchronize();
            });
        }
        private void Synchronize()
        {
            Effect? desired = _effect.Radius > 0.001 ? _effect : null;
            if (!ReferenceEquals(_element.Effect, desired)) _element.Effect = desired;
        }
        public void Dispose() { _disposed = true; _effect.Changed -= Update; }
    }
}

// A plain configuration object intentionally supplies no inheritance context to
// the Freezable. Only UIElement.Effect owns that context while blur is active.
[System.Windows.Markup.ContentProperty(nameof(Effect))]
public sealed class BlurDefinition
{
    public BlurEffect? Effect { get; set; }
}
