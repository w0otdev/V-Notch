using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VNotch.Services;

internal enum InteractionCue { Click, ToggleOn, ToggleOff, Navigate, Select, Slider, Menu, Confirm, Delete, Transport }

// One shared, asynchronous playback channel. No files, polling or audio capture.
// Pacing rules and playback live in Native/Services/InteractionSoundPlayback.cpp.
internal sealed partial class InteractionSoundPlayer : IDisposable
{
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_interaction_sounds_create_wave(
        int cue,
        int variant,
        Span<byte> outBuf,
        int outCap,
        out int outLen);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct VnInteractionGateState
    {
        public long LastPlayed;
        public byte HasLast;
        public fixed long LastCuePlayed[CueCount];
        public fixed int LastVariants[CueCount];
        public int RapidBurstCount;
    }

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_interaction_gate_reset(ref VnInteractionGateState state);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_interaction_gate_evaluate(
        ref VnInteractionGateState state, int cue, long nowMs, byte hasOverride, uint random,
        out byte allow, out int variant, out int burstCount);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_interaction_play(int cue, int variant, float volume, int burstCount);

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_interaction_stop();

#pragma warning disable S1144 // Used in fixed-size struct attribute
    private const int CueCount = 10;
#pragma warning restore S1144
    internal const int VariantCount = 6;
    private VnInteractionGateState _gate;
    private float _volume = .55f;
    private readonly Action<InteractionCue, int>? _playOverride;
    private readonly Func<long> _clock;
    private bool _reportedPlaybackFailure;

    internal bool Enabled { get; set; }

    internal float Volume
    {
        get => _volume;
        set
        {
            _volume = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
            if (_volume == 0) vn_interaction_stop();
        }
    }

    internal InteractionSoundPlayer(Action<InteractionCue, int>? play = null, Func<long>? clock = null)
    {
        _playOverride = play;
        _clock = clock ?? (() => Environment.TickCount64);
        vn_interaction_gate_reset(ref _gate);
    }

    internal void Play(InteractionCue cue)
    {
        if (!Enabled || Volume == 0) return;
        if (vn_interaction_gate_evaluate(ref _gate, (int)cue, _clock(), _playOverride != null ? (byte)1 : (byte)0,
                (uint)Random.Shared.Next(), out byte allow, out int variant, out int burstCount) != 0 || allow == 0)
            return;

        if (_playOverride != null)
        {
            try { _playOverride(cue, variant); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException) { ReportFailure(ex); }
            return;
        }

        if (vn_interaction_play((int)cue, variant, Volume, burstCount) == 0)
        {
            _reportedPlaybackFailure = false;
            return;
        }

        // Feedback must never interrupt the user's action if audio is unavailable.
        ReportFailure(new InvalidOperationException("Native interaction sound playback failed."));
    }

    private void ReportFailure(Exception ex)
    {
        if (_reportedPlaybackFailure) return;
        _reportedPlaybackFailure = true;
        RuntimeLog.Error("SFX", ex, "Interaction sound playback failed");
    }

    internal static byte[] CreateWave(InteractionCue cue, int variant = 0)
    {
        if (variant < 0 || variant >= VariantCount) throw new ArgumentOutOfRangeException(nameof(variant));
        Span<byte> buf = stackalloc byte[16384];
        if (vn_interaction_sounds_create_wave((int)cue, variant, buf, buf.Length, out int len) != 0 || len <= 44)
            throw new InvalidOperationException("Failed to synthesize interaction wave.");
        return buf[..len].ToArray();
    }

    public void Dispose() => vn_interaction_stop();
}
internal static partial class InteractionSounds
{
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_interaction_sounds_classify_action(
        ReadOnlySpan<char> namePtr,
        int nameLen,
        byte isTabItem,
        out int outCue);

    private static readonly InteractionSoundPlayer Player = new();
    private static bool _initialized;

    internal static void Initialize(bool enabled, double volume)
    {
        if (!_initialized)
        {
            _initialized = true;
            EventManager.RegisterClassHandler(typeof(Window), ButtonBase.ClickEvent, new RoutedEventHandler(OnClick), true);
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPointerDown), true);
            EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnPointerUp), true);
            EventManager.RegisterClassHandler(typeof(Window), Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnDragCompleted), true);
            EventManager.RegisterClassHandler(typeof(Window), Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnSelection), true);
            EventManager.RegisterClassHandler(typeof(MenuItem), MenuItem.ClickEvent, new RoutedEventHandler(OnMenuClick), true);
        }
        Configure(enabled, volume);
    }

    internal static void Configure(bool enabled, double volume)
    {
        // Tests and setup windows do not initialize application feedback.
        if (!_initialized) return;
        Player.Enabled = enabled;
        Player.Volume = (float)volume;
        if (!enabled) Player.Dispose();
    }

    internal static void Shutdown() { Player.Enabled = false; Player.Dispose(); }
    internal static void Preview() { if (_initialized) Player.Play(InteractionCue.Select); }
    // The notch intentionally accepts pointer input without activating its window.
    private static bool CanPlay(object sender) => sender is Window { IsVisible: true, IsEnabled: true };

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (!CanPlay(sender) || e.OriginalSource is not ButtonBase { IsEnabled: true } button) return;
        var cue = ClassifyAction(button);
        if (cue == InteractionCue.Click && button is ToggleButton toggle)
            cue = toggle.IsChecked == true ? InteractionCue.ToggleOn : InteractionCue.ToggleOff;
        Player.Play(cue);
    }

    private static void OnMenuClick(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is MenuItem { IsEnabled: true, HasItems: false } menu)
        {
            var cue = ClassifyAction(menu);
            InteractionCue resolvedCue;
            if (menu.IsCheckable)
                resolvedCue = menu.IsChecked ? InteractionCue.ToggleOn : InteractionCue.ToggleOff;
            else
                resolvedCue = cue == InteractionCue.Click ? InteractionCue.Menu : cue;
            Player.Play(resolvedCue);
        }
    }

    private static void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanPlay(sender) || (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right)) return;
        bool customAction = false;
        var cue = InteractionCue.Click;
        for (var node = e.OriginalSource as DependencyObject; node != null && node is not Window; node = Parent(node))
        {
            // Standard controls have semantic events; editable content stays quiet.
            if (node is ButtonBase or MenuItem or Slider or TextBoxBase or PasswordBox or Thumb) return;
            // ComboBoxItem and TabItem fire OnSelection when their selection actually changes; only ListBoxItem needs direct pointer sound.
            if (node is ListBoxItem)
            {
                if (e.ChangedButton == MouseButton.Left) Player.Play(InteractionCue.Select);
                return;
            }
            if (node is ComboBoxItem or TabItem or Selector) return;
            if (node is FrameworkElement { IsEnabled: false }) return;
            if (node is FrameworkElement element)
            {
                if (element.Cursor == Cursors.Hand && (e.ChangedButton == MouseButton.Left || element.Name == "SettingsButton")) customAction = true;
                if (cue == InteractionCue.Click) cue = ClassifyAction(element);
            }
        }
        if (customAction) Player.Play(cue);
    }

    private static void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (CanPlay(sender) && !e.Canceled && e.OriginalSource is DependencyObject source)
            for (var node = source; node != null; node = Parent(node))
                if (node is Slider slider)
                {
                    if (slider.Name != "InteractionSoundVolumeSlider") Player.Play(InteractionCue.Slider);
                    break;
                }
    }

    private static void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (!CanPlay(sender) || (e.ChangedButton != MouseButton.Left && e.ChangedButton != MouseButton.Right)) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = Parent(node))
        {
            if (node is Thumb) return;
            if (node is Slider slider)
            {
                // The volume control previews its chosen gain in its own handler.
                if (slider.Name != "InteractionSoundVolumeSlider") Player.Play(InteractionCue.Slider);
                return;
            }
        }
    }

    private static void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (CanPlay(sender) && e.AddedItems.Count > 0 && e.OriginalSource is Selector { IsKeyboardFocusWithin: true } selector &&
            (selector is TabControl or ComboBox))
            Player.Play(selector is TabControl ? InteractionCue.Navigate : InteractionCue.Select);
    }

    internal static InteractionCue ClassifyAction(FrameworkElement element)
    {
        ReadOnlySpan<char> name = (element.Name ?? string.Empty).AsSpan();
        vn_interaction_sounds_classify_action(name, name.Length, element is TabItem ? (byte)1 : (byte)0, out int cue);
        return (InteractionCue)cue;
    }

    private static DependencyObject? Parent(DependencyObject node) => node is Visual or Visual3D
        ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
