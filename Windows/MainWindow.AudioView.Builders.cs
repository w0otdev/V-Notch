using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;
using VNotch.ViewModels;

namespace VNotch;

public partial class MainWindow
{
    private readonly Dictionary<StackPanel, int> _audioSectionAnimationGenerations = new();

    private IVolumeService? _masterVolumeCached;
    private IVolumeService MasterVolume =>
        _masterVolumeCached ??= (IVolumeService)App.Services.GetService(typeof(IVolumeService))!;

    private FontFamily? _audioFontCached;
    private FontFamily AudioFont => _audioFontCached ??= (FontFamily)FindResource("MainSystemFont");

    private static readonly FontFamily SegoeSymbolFont = new FontFamily("pack://application:,,,/Fonts/#Segoe MDL2 Assets, Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol, Segoe UI");

    private const double ColName = 196;
    private const double ColPercent = 50;
    private const double ColDevice = 206;

    private static readonly Brush AudioGreen = Frozen("#3FD15B");
    private static readonly Brush AudioTrack = Frozen("#4D4D4D");
    private static readonly Brush AudioMuted = Frozen("#8C8C8C");
    private static readonly Brush AudioComboBg = Frozen("#33000000");
    private static readonly Brush AudioComboBorder = Frozen("#26FFFFFF");
    private static readonly Brush AudioComboHover = Frozen("#1FFFFFFF");
    private static readonly Brush AudioHeaderText = Frozen("#8A8A8A");

    private static readonly Geometry OutputIconGeometry = MakeFrozenGeometry(
        "M411.16,97.46C368.43,55.86,311.88,32,256,32S143.57,55.86,100.84,97.46C56.45,140.67,32,197,32,256c0,26.67,8.75,61.09,32.88,125.55S137,473,157.27,477.41c5.81,1.27,12.62,2.59,18.73,2.59a60.06,60.06,0,0,0,30-8l14-8c15.07-8.82,19.47-28.13,10.8-43.35L143.88,268.08a31.73,31.73,0,0,0-43.57-11.76l-13.69,8a56.49,56.49,0,0,0-14,11.59,4,4,0,0,1-7-2A114.68,114.68,0,0,1,64,256c0-50.31,21-98.48,59.16-135.61C160,84.55,208.39,64,256,64s96,20.55,132.84,56.39C427,157.52,448,205.69,448,256a114.68,114.68,0,0,1-1.68,17.91,4,4,0,0,1-7,2,56.49,56.49,0,0,0-14-11.59l-13.69-8a31.73,31.73,0,0,0-43.57,11.76L281.2,420.65c-8.67,15.22-4.27,34.53,10.8,43.35l14,8a60.06,60.06,0,0,0,30,8c6.11,0,12.92-1.32,18.73-2.59C375,473,423,446,447.12,381.55S480,282.67,480,256C480,197,455.55,140.67,411.16,97.46Z");

    private static Geometry MakeFrozenGeometry(string data)
    {
        var g = Geometry.Parse(data);
        g.Freeze();
        return g;
    }

    private static readonly Geometry InputIconGeometry = MakeFrozenGeometry(
        "M11.665 7.915v1.31a5.257 5.257 0 0 1-1.514 3.694 5.174 5.174 0 0 1-1.641 1.126 5.04 5.04 0 0 1-1.456.384v1.899h2.312a.554.554 0 0 1 0 1.108H3.634a.554.554 0 0 1 0-1.108h2.312v-1.899a5.045 5.045 0 0 1-1.456-.384 5.174 5.174 0 0 1-1.641-1.126 5.257 5.257 0 0 1-1.514-3.695v-1.31a.554.554 0 1 1 1.109 0v1.31a4.131 4.131 0 0 0 1.195 2.917 3.989 3.989 0 0 0 5.722 0 4.133 4.133 0 0 0 1.195-2.917v-1.31a.554.554 0 1 1 1.109 0zM3.77 10.37a2.875 2.875 0 0 1-.233-1.146V4.738A2.905 2.905 0 0 1 3.77 3.58a3 3 0 0 1 1.59-1.59 2.902 2.902 0 0 1 1.158-.233 2.865 2.865 0 0 1 1.152.233 2.977 2.977 0 0 1 1.793 2.748l-.012 4.487a2.958 2.958 0 0 1-.856 2.09 3.025 3.025 0 0 1-.937.634 2.865 2.865 0 0 1-1.152.233 2.905 2.905 0 0 1-1.158-.233A2.957 2.957 0 0 1 3.77 10.37z");

    private bool _audioSystemExpanded { get => _viewModel.AudioMixer.IsSystemExpanded; set => _viewModel.AudioMixer.IsSystemExpanded = value; }
    private bool _audioAppsExpanded { get => _viewModel.AudioMixer.IsApplicationsExpanded; set => _viewModel.AudioMixer.IsApplicationsExpanded = value; }
    private int _audioPopulateToken;
    private int _audioPollInFlight;

    private Action<double>? _outputSetVol;
    private TextBlock? _outputDeviceLabel;
    private Action<double>? _inputSetVol;
    private TextBlock? _inputDeviceLabel;
    private readonly Dictionary<uint, (Action<double> SetVol, Border IconHost, TextBlock NameLabel)> _appRows = new();
    private string _audioStructureKey = "";

    private AudioMixerSnapshot? _lastAudioSnapshot { get => _viewModel.AudioMixer.Snapshot; set => _viewModel.AudioMixer.Snapshot = value; }
    private AudioMixerSnapshot? _pendingAudioSnapshot;
    private Action? _pendingAudioAfterBuild;
    private bool _isAudioLoading;
    private int _audioLoadingTransitionVersion;

    internal static bool ShouldDeferAudioSnapshotDuringTransition(
        bool isAudioView, bool isAnimating, bool hasBuiltUi)
        => isAudioView && isAnimating && hasBuiltUi;

    private void SetAudioLoadingState(bool isLoading)
    {
        if (AudioLoadingPanel == null) return;

        if (isLoading)
        {
            _isAudioLoading = true;
            _audioLoadingTransitionVersion++;

            AudioLoadingPanel.BeginAnimation(OpacityProperty, null);
            AudioLoadingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            AudioRoot.BeginAnimation(OpacityProperty, null);
            AudioRootTranslate.BeginAnimation(TranslateTransform.YProperty, null);

            AudioLoadingPanel.Visibility = Visibility.Visible;
            AudioLoadingPanel.Opacity = 1;
            AudioLoadingTranslate.Y = 0;
            AudioRoot.Opacity = 0;
            AudioRootTranslate.Y = 10;
            return;
        }

        // The detailed icon snapshot often arrives while the quick snapshot is
        // still revealing. Do not restart the same transition in that case.
        if (!_isAudioLoading)
        {
            if (AudioLoadingPanel.Visibility != Visibility.Visible)
            {
                AudioRoot.BeginAnimation(OpacityProperty, null);
                AudioRootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                AudioRoot.Opacity = 1;
                AudioRootTranslate.Y = 0;
            }
            return;
        }

        _isAudioLoading = false;
        int transitionVersion = ++_audioLoadingTransitionVersion;

        void FinishLoadingPanel()
        {
            if (_isAudioLoading || transitionVersion != _audioLoadingTransitionVersion)
                return;

            AudioLoadingPanel.BeginAnimation(OpacityProperty, null);
            AudioLoadingTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            AudioLoadingPanel.Opacity = 0;
            AudioLoadingTranslate.Y = 0;
            AudioLoadingPanel.Visibility = Visibility.Collapsed;
        }

        void FinishAudioReveal()
        {
            if (_isAudioLoading || transitionVersion != _audioLoadingTransitionVersion)
                return;

            AudioRoot.BeginAnimation(OpacityProperty, null);
            AudioRootTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            AudioRoot.Opacity = 1;
            AudioRootTranslate.Y = 0;
        }

        if (AnimationConfig.ReduceMotion)
        {
            FinishLoadingPanel();
            FinishAudioReveal();
            return;
        }

        int fps = AnimationConfig.TargetFps;
        var exitEase = new CubicEase { EasingMode = EasingMode.EaseIn };
        var enterEase = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var loadingDuration = new Duration(TimeSpan.FromMilliseconds(180));
        var contentDuration = new Duration(TimeSpan.FromMilliseconds(360));
        var contentDelay = TimeSpan.FromMilliseconds(55);

        var loadingFade = new DoubleAnimation(AudioLoadingPanel.Opacity, 0, loadingDuration)
        {
            EasingFunction = exitEase
        };
        var loadingSlide = new DoubleAnimation(AudioLoadingTranslate.Y, -6, loadingDuration)
        {
            EasingFunction = exitEase
        };
        loadingFade.Completed += (_, _) => FinishLoadingPanel();

        var contentFade = new DoubleAnimation(0, 1, contentDuration)
        {
            BeginTime = contentDelay,
            EasingFunction = enterEase
        };
        var contentSlide = new DoubleAnimation(10, 0, contentDuration)
        {
            BeginTime = contentDelay,
            EasingFunction = enterEase
        };
        contentFade.Completed += (_, _) => FinishAudioReveal();

        Timeline.SetDesiredFrameRate(loadingFade, fps);
        Timeline.SetDesiredFrameRate(loadingSlide, fps);
        Timeline.SetDesiredFrameRate(contentFade, fps);
        Timeline.SetDesiredFrameRate(contentSlide, fps);

        AudioLoadingPanel.BeginAnimation(OpacityProperty, loadingFade);
        AudioLoadingTranslate.BeginAnimation(TranslateTransform.YProperty, loadingSlide);
        AudioRoot.BeginAnimation(OpacityProperty, contentFade);
        AudioRootTranslate.BeginAnimation(TranslateTransform.YProperty, contentSlide);

        StaggerAudioMixerReveal(contentDelay);
    }

    private void StaggerAudioMixerReveal(TimeSpan baseDelay)
    {
        if (AudioRoot == null) return;

        var targets = new List<FrameworkElement>();
        foreach (var child in AudioRoot.Children)
        {
            if (child is StackPanel rows)
            {
                if (rows.Visibility != Visibility.Visible) continue;
                foreach (var row in rows.Children)
                    if (row is FrameworkElement rowEl) targets.Add(rowEl);
            }
            else if (child is FrameworkElement el)
            {
                targets.Add(el);
            }
        }
        if (targets.Count == 0) return;

        int fps = AnimationConfig.TargetFps;
        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(320));
        var stagger = TimeSpan.FromMilliseconds(38);

        for (int i = 0; i < targets.Count; i++)
        {
            var el = targets[i];
            var delay = baseDelay + TimeSpan.FromTicks(stagger.Ticks * i);

            el.BeginAnimation(OpacityProperty, null);
            el.Opacity = 0;
            var translate = new TranslateTransform(0, 10);
            el.RenderTransform = translate;

            var fade = new DoubleAnimation(0, 1, dur) { BeginTime = delay, EasingFunction = ease };
            var slide = new DoubleAnimation(10, 0, dur) { BeginTime = delay, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, fps);
            Timeline.SetDesiredFrameRate(slide, fps);

            var captured = el;
            var capturedTranslate = translate;
            fade.Completed += (_, _) =>
            {
                captured.BeginAnimation(OpacityProperty, null);
                captured.Opacity = 1;
                // Only detach the transform if the section toggle or a rebuild
                // has not replaced it in the meantime.
                if (ReferenceEquals(captured.RenderTransform, capturedTranslate))
                    captured.RenderTransform = null;
            };

            el.BeginAnimation(OpacityProperty, fade);
            translate.BeginAnimation(TranslateTransform.YProperty, slide);
        }
    }

    private static Brush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    private AudioMixerSnapshot ReadAudioSnapshot(bool includeIcons, bool includeDevices = true)
    {
        return new AudioMixerSnapshot
        {
            Output = includeDevices ? SafeCall(() => AudioMixer.GetOutputDevices()) ?? new() : new(),
            Input = includeDevices ? SafeCall(() => AudioMixer.GetInputDevices()) ?? new() : new(),
            Sessions = SafeCall(() => AudioMixer.GetSessions(includeIcons)) ?? new(),
            Master = SafeCall(() => MasterVolume.IsAvailable ? MasterVolume.GetVolume() : 0.5f),
            Capture = SafeCall(() => AudioMixer.GetCaptureVolume())
        };
    }

    private void RefreshAudioData(Action? afterBuild = null)
    {
        int token = ++_audioPopulateToken;

        System.Threading.Tasks.Task.Run(() =>
        {
            // Shell and file-version lookups for application icons can be much
            // slower than the Core Audio queries. Publish a lightweight snapshot
            // first so the mixer is usable without waiting for that work.
            var quick = ReadAudioSnapshot(includeIcons: false);
            QueueAudioSnapshot(token, quick);

            var detailedSessions = SafeCall(() => AudioMixer.GetSessions(includeIcons: true));
            if (detailedSessions == null || token != System.Threading.Volatile.Read(ref _audioPopulateToken))
                return;

            // Preserve the device lists from the quick pass. Re-enumerating them
            // here adds latency and can make the second snapshot look structural.
            var detailed = new AudioMixerSnapshot
            {
                Output = quick.Output,
                Input = quick.Input,
                Sessions = detailedSessions,
                Master = quick.Master,
                Capture = quick.Capture
            };
            QueueAudioSnapshot(token, detailed, afterBuild);
        });
    }

    private void QueueAudioSnapshot(int token, AudioMixerSnapshot snap, Action? afterBuild = null)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (token != _audioPopulateToken) return;
            _lastAudioSnapshot = snap;

            // Rebuilding an existing dynamic tree during the scale/fade can
            // still invalidate layout and stall Liquid Glass. Defer later
            // structural updates, but always build the empty first-boot view
            // immediately so the opening animation never reveals a blank panel.
            bool hasBuiltUi = AudioRoot?.Children.Count > 0;
            if (ShouldDeferAudioSnapshotDuringTransition(_isAudioView, _isAnimating, hasBuiltUi))
            {
                _pendingAudioSnapshot = snap;
                _pendingAudioAfterBuild = afterBuild;
                return;
            }

            ApplyAudioSnapshot(snap, afterBuild);
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void ApplyAudioSnapshot(AudioMixerSnapshot snap, Action? afterBuild = null)
    {
        if (_appRows.Count > 0 && snap.StructureKey == _audioStructureKey)
            PatchInPlace(snap);
        else
            BuildAudioUI(snap);

        _lastAudioSnapshot = snap;
        SetAudioLoadingState(false);
        afterBuild?.Invoke();
    }

    private void RefreshAudioLocalization()
    {
        AudioLoadingText.Text = Loc.Get("audio.loading");
        if (_lastAudioSnapshot != null && AudioRoot?.Children.Count > 0)
        {
            BuildAudioUI(_lastAudioSnapshot);
        }
    }

    private bool ApplyPendingAudioSnapshot()
    {
        var snap = _pendingAudioSnapshot;
        var afterBuild = _pendingAudioAfterBuild;
        _pendingAudioSnapshot = null;
        _pendingAudioAfterBuild = null;
        if (snap == null) return false;
        ApplyAudioSnapshot(snap, afterBuild);
        return true;
    }

    private void CacheSessionVolume(uint pid, float v)
        => _viewModel.AudioMixer.CacheSessionVolume(pid, v);

    private void PollAudioVolumes()
    {
        if (!_isAudioView || _isAnimating) return;
        if (AudioLoadingPanel?.Visibility == Visibility.Visible) return;
        if (System.Threading.Interlocked.CompareExchange(ref _audioPollInFlight, 1, 0) != 0) return;

        System.Threading.Tasks.Task.Run(() =>
        {
            var sessions = SafeCall(() => AudioMixer.GetSessions(includeIcons: false)) ?? new();
            float capture = SafeCall(() => AudioMixer.GetCaptureVolume());
            float master = SafeCall(() => MasterVolume.IsAvailable ? MasterVolume.GetVolume() : 0.5f);

            try
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (!_isAudioView || _isAnimating) return;

                        var pids = new HashSet<uint>(sessions.Where(x => !x.IsSystemSounds).Select(x => x.ProcessId));
                        bool sameSet = _appRows.Count == pids.Count && pids.All(_appRows.ContainsKey);

                        if (sameSet)
                        {
                            _outputSetVol?.Invoke(master);
                            _inputSetVol?.Invoke(capture);
                            foreach (var s in sessions)
                            {
                                if (s.IsSystemSounds) continue;
                                if (_appRows.TryGetValue(s.ProcessId, out var h))
                                    h.SetVol(s.IsMuted ? 0 : s.Volume);
                            }

                            if (_lastAudioSnapshot != null)
                            {
                                _lastAudioSnapshot.Master = master;
                                _lastAudioSnapshot.Capture = capture;
                                foreach (var s in sessions)
                                {
                                    if (s.IsSystemSounds) continue;
                                    var c = _lastAudioSnapshot.Sessions.Find(x => x.ProcessId == s.ProcessId);
                                    if (c != null) { c.Volume = s.Volume; c.IsMuted = s.IsMuted; }
                                }
                            }
                        }
                        else
                        {
                            RefreshAudioData();
                        }
                    }
                    finally
                    {
                        System.Threading.Volatile.Write(ref _audioPollInFlight, 0);
                    }
                }), System.Windows.Threading.DispatcherPriority.Background);
            }
            catch
            {
                System.Threading.Volatile.Write(ref _audioPollInFlight, 0);
            }
        });
    }

    private void PatchInPlace(AudioMixerSnapshot snap)
    {
        _outputSetVol?.Invoke(snap.Master);
        if (_inputSetVol != null) _inputSetVol(snap.Capture);
        string outputName = snap.GetDefaultOutputName();
        string inputName = snap.GetDefaultInputName();
        if (_outputDeviceLabel != null && !string.Equals(_outputDeviceLabel.Text, outputName, StringComparison.Ordinal))
            _outputDeviceLabel.Text = outputName;
        if (_inputDeviceLabel != null && !string.Equals(_inputDeviceLabel.Text, inputName, StringComparison.Ordinal))
            _inputDeviceLabel.Text = inputName;

        foreach (var session in snap.Sessions)
        {
            if (session.IsSystemSounds) continue;
            if (!_appRows.TryGetValue(session.ProcessId, out var h)) continue;

            h.SetVol(session.IsMuted ? 0 : session.Volume);
            if (session.Icon != null)
            {
                if (h.IconHost.Child is Image image)
                {
                    if (!ReferenceEquals(image.Source, session.Icon)) image.Source = session.Icon;
                }
                else
                {
                    h.IconHost.Child = new Image
                    {
                        Source = session.Icon,
                        Width = 18,
                        Height = 18,
                        SnapsToDevicePixels = true
                    };
                }
            }
            if (!string.IsNullOrWhiteSpace(session.DisplayName) &&
                !string.Equals(h.NameLabel.Text, session.DisplayName, StringComparison.Ordinal))
                h.NameLabel.Text = session.DisplayName;
        }
    }

    private void EnsureAudioUIBuilt(AudioMixerSnapshot snap)
    {
        if (_appRows.Count > 0 && snap.StructureKey == _audioStructureKey)
        {
            PatchInPlace(snap);
            AudioScrollViewer?.ScrollToTop();
        }
        else
        {
            BuildAudioUI(snap);
        }
    }

    private void BuildAudioUI(AudioMixerSnapshot snap)
    {
        try
        {
            CloseDeviceMenu();
            _appRows.Clear();
            _outputSetVol = null;
            _inputSetVol = null;
            _outputDeviceLabel = null;
            _inputDeviceLabel = null;
            _audioStructureKey = snap.StructureKey;
            AudioRoot.Children.Clear();

            if (AudioScrollViewer != null)
            {
                AudioScrollViewer.Width = _audioViewWidth - 38;
                AudioScrollViewer.HorizontalAlignment = HorizontalAlignment.Left;
                AudioScrollViewer.VerticalAlignment = VerticalAlignment.Top;
            }

            string outName = snap.GetDefaultOutputName(Loc.Get("audio.speakers"));
            string inName = snap.GetDefaultInputName(Loc.Get("audio.microphone"));

            var systemRows = new StackPanel { ClipToBounds = true, Visibility = _audioSystemExpanded ? Visibility.Visible : Visibility.Collapsed };

            float master = snap.Master;
            systemRows.Children.Add(BuildSystemRow("\uE7F5", OutputIconGeometry, Loc.Get("audio.output"), master,
                r => { if (MasterVolume.IsAvailable) MasterVolume.SetVolume((float)r); },
                deviceGlyph: "\uE7F5", deviceText: outName, devices: snap.Output,
                onDevice: SelectDefaultOutputDevice,
                out _outputSetVol, out _outputDeviceLabel));

            systemRows.Children.Add(BuildSystemRow("\uE720", InputIconGeometry, Loc.Get("audio.input"), snap.Capture,
                r => { AudioMixer.SetCaptureVolume((float)r); if (_lastAudioSnapshot != null) _lastAudioSnapshot.Capture = (float)r; },
                deviceGlyph: "\uE720", deviceText: inName, devices: snap.Input,
                onDevice: id => { if (AudioMixer.SetDefaultInputDevice(id)) RefreshAudioData(); },
                out _inputSetVol, out _inputDeviceLabel));

            AudioRoot.Children.Add(BuildSectionHeader(Loc.Get("audio.system"), _audioSystemExpanded,
                showVolumeLabel: true, deviceLabel: Loc.Get("audio.device"), out var sysChevron, out var sysClick, out var sysLabels, topMargin: 0));
            AudioRoot.Children.Add(systemRows);
            WireSectionToggle(sysClick, sysChevron, () => _audioSystemExpanded, v => _audioSystemExpanded = v, systemRows, sysLabels);

            var appRows = new StackPanel { ClipToBounds = true, Visibility = _audioAppsExpanded ? Visibility.Visible : Visibility.Collapsed };
            foreach (var session in snap.Sessions)
            {
                if (session.IsSystemSounds) continue;
                uint pid = session.ProcessId;
                appRows.Children.Add(BuildAppRow(session, r =>
                {
                    AudioMixer.SetSessionVolume(pid, (float)r);
                    CacheSessionVolume(pid, (float)r);
                }));
            }

            AudioRoot.Children.Add(BuildSectionHeader(Loc.Get("audio.applications"), _audioAppsExpanded,
                showVolumeLabel: false, deviceLabel: Loc.Get("audio.redirectTo"), out var appChevron, out var appClick, out var appLabels, topMargin: 14));
            AudioRoot.Children.Add(appRows);
            WireSectionToggle(appClick, appChevron, () => _audioAppsExpanded, v => _audioAppsExpanded = v, appRows, appLabels);
        }
        catch (Exception ex)
        {
            RuntimeLog.Log("AUDIOVIEW", $"Build error: {ex.Message}");
        }

        _audioViewHeight = MeasureAudioFitHeight();
        if (AudioScrollViewer != null)
            AudioScrollViewer.Height = _audioViewHeight - _audioViewChrome;

        if (AudioScrollViewer != null)
            AudioScrollViewer.ScrollToTop();
    }

    private void SelectDefaultOutputDevice(string deviceId)
    {
        if (!AudioMixer.SetDefaultOutputDevice(deviceId))
            return;

        MasterVolume.RefreshDefaultDevice();
        _mediaService.InvalidateVolumeSessionCache();
        _volumeSynced = false;
        RefreshAudioData();
    }

    private double MeasureAudioFitHeight()
    {
        if (AudioRoot == null) return _audioViewMaxHeight;
        double contentWidth = _audioViewWidth - 38 - 7;
        AudioRoot.Measure(new Size(contentWidth, double.PositiveInfinity));
        double desired = AudioRoot.DesiredSize.Height;
        return Math.Clamp(desired + _audioViewChrome, _audioViewMinHeight, _audioViewMaxHeight);
    }

    private void WireSectionToggle(FrameworkElement clickTarget, RotateTransform chevron, Func<bool> get, Action<bool> set, StackPanel rows, List<FrameworkElement> columnLabels)
    {
        clickTarget.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            bool next = !get();
            set(next);
            AnimateSectionToggle(rows, chevron, columnLabels, next);
        };
    }

    private void AnimateSectionToggle(StackPanel rows, RotateTransform chevron, List<FrameworkElement> columnLabels, bool expand)
    {
        int fps = AnimationConfig.TargetFps;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(260));
        int generation = _audioSectionAnimationGenerations.TryGetValue(rows, out int previousGeneration)
            ? previousGeneration + 1
            : 1;
        _audioSectionAnimationGenerations[rows] = generation;

        double currentAngle = chevron.Angle;
        chevron.BeginAnimation(RotateTransform.AngleProperty, null);
        chevron.Angle = currentAngle;

        var rotAnim = new DoubleAnimation(currentAngle, expand ? 0 : -90, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(rotAnim, fps);
        chevron.BeginAnimation(RotateTransform.AngleProperty, rotAnim, HandoffBehavior.SnapshotAndReplace);

        if (columnLabels != null)
        {
            foreach (var lbl in columnLabels)
            {
                bool wasVisible = lbl.Visibility == Visibility.Visible;
                double labelOpacity = wasVisible ? Math.Clamp(lbl.Opacity, 0.0, 1.0) : 0.0;
                lbl.BeginAnimation(OpacityProperty, null);
                lbl.Opacity = labelOpacity;
                if (expand) lbl.Visibility = Visibility.Visible;
                var lblFade = new DoubleAnimation(labelOpacity, expand ? 1 : 0, dur) { EasingFunction = ease };
                Timeline.SetDesiredFrameRate(lblFade, fps);
                var captured = lbl;
                lblFade.Completed += (_, _) =>
                {
                    if (!_audioSectionAnimationGenerations.TryGetValue(rows, out int currentGeneration) ||
                        currentGeneration != generation)
                    {
                        return;
                    }

                    captured.BeginAnimation(OpacityProperty, null);
                    captured.Opacity = expand ? 1 : 0;
                    if (!expand) captured.Visibility = Visibility.Collapsed;
                };
                lbl.BeginAnimation(OpacityProperty, lblFade, HandoffBehavior.SnapshotAndReplace);
            }
        }

        bool rowsWereVisible = rows.Visibility == Visibility.Visible;
        double currentHeight = rowsWereVisible
            ? Math.Max(0.0, rows.ActualHeight)
            : 0.0;
        double rowsOpacity = rowsWereVisible
            ? Math.Clamp(rows.Opacity, 0.0, 1.0)
            : 0.0;

        rows.BeginAnimation(HeightProperty, null);
        rows.BeginAnimation(OpacityProperty, null);
        rows.Height = currentHeight;
        rows.Opacity = rowsOpacity;

        double newFit;

        if (expand)
        {
            rows.Visibility = Visibility.Visible;

            double availableWidth = rows.ActualWidth > 0
                ? rows.ActualWidth
                : (AudioScrollViewer?.ActualWidth ?? (_audioViewWidth - 38));
            rows.Height = double.NaN;
            rows.Measure(new Size(availableWidth, double.PositiveInfinity));
            double target = rows.DesiredSize.Height;

            newFit = MeasureAudioFitHeight();

            rows.Height = currentHeight;
            rows.Opacity = rowsOpacity;

            var hAnim = new DoubleAnimation(currentHeight, target, dur) { EasingFunction = ease };
            var oAnim = new DoubleAnimation(rowsOpacity, 1, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(hAnim, fps);
            Timeline.SetDesiredFrameRate(oAnim, fps);
            hAnim.Completed += (_, _) =>
            {
                if (!_audioSectionAnimationGenerations.TryGetValue(rows, out int currentGeneration) ||
                    currentGeneration != generation)
                {
                    return;
                }

                rows.BeginAnimation(HeightProperty, null);
                rows.Height = double.NaN;
                rows.BeginAnimation(OpacityProperty, null);
                rows.Opacity = 1;
            };
            rows.BeginAnimation(HeightProperty, hAnim, HandoffBehavior.SnapshotAndReplace);
            rows.BeginAnimation(OpacityProperty, oAnim, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            rows.Height = 0;
            newFit = MeasureAudioFitHeight();
            rows.Height = currentHeight;
            rows.Opacity = rowsOpacity;

            var hAnim = new DoubleAnimation(currentHeight, 0, dur) { EasingFunction = ease };
            var oAnim = new DoubleAnimation(rowsOpacity, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(hAnim, fps);
            Timeline.SetDesiredFrameRate(oAnim, fps);
            hAnim.Completed += (_, _) =>
            {
                if (!_audioSectionAnimationGenerations.TryGetValue(rows, out int currentGeneration) ||
                    currentGeneration != generation)
                {
                    return;
                }

                rows.Visibility = Visibility.Collapsed;
                rows.BeginAnimation(HeightProperty, null);
                rows.Height = double.NaN;
                rows.BeginAnimation(OpacityProperty, null);
                rows.Opacity = 1;
            };
            rows.BeginAnimation(HeightProperty, hAnim, HandoffBehavior.SnapshotAndReplace);
            rows.BeginAnimation(OpacityProperty, oAnim, HandoffBehavior.SnapshotAndReplace);
        }

        _audioViewHeight = newFit;
        AnimateAudioNotchHeight(newFit, dur, ease);
    }

    private T? SafeCall<T>(Func<T> fn)
    {
        try { return fn(); } catch { return default; }
    }

    private static Grid NewRowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColName) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColPercent) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColDevice) });
        return g;
    }

    private FrameworkElement BuildSectionHeader(string title, bool expanded,
        bool showVolumeLabel, string deviceLabel, out RotateTransform chevronTransform,
        out FrameworkElement clickTarget, out List<FrameworkElement> columnLabels, double topMargin = 0)
    {
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, topMargin, 0, 6);
        columnLabels = new List<FrameworkElement>();

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        clickTarget = titlePanel;
        var rotate = new RotateTransform(expanded ? 0 : -90);
        chevronTransform = rotate;
        var chevron = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Fill = AudioHeaderText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 8, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotate
        };
        titlePanel.Children.Add(chevron);
        titlePanel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(titlePanel, 0);
        grid.Children.Add(titlePanel);

        if (showVolumeLabel)
        {
            var vol = ColumnLabel(Loc.Get("audio.volume"));
            vol.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            vol.Opacity = expanded ? 1 : 0;
            Grid.SetColumn(vol, 1);
            grid.Children.Add(vol);
            columnLabels.Add(vol);
        }

        var dev = ColumnLabel(deviceLabel);
        dev.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        dev.Opacity = expanded ? 1 : 0;
        Grid.SetColumn(dev, 3);
        grid.Children.Add(dev);
        columnLabels.Add(dev);

        return grid;
    }

    private TextBlock ColumnLabel(string text) => new TextBlock
    {
        Text = text,
        // White on the liquid glass skin (the grey header reads poorly over the
        // live refracted backdrop); default grey otherwise.
        Foreground = IsLiquidGlassEnabled ? Brushes.White : AudioHeaderText,
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        FontFamily = AudioFont,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    private FrameworkElement BuildSystemRow(string glyph, Geometry? iconGeometry, string label, double ratio,
        Action<double> onVol, string deviceGlyph, string deviceText,
        List<AudioDeviceInfo>? devices, Action<string>? onDevice,
        out Action<double> setVol, out TextBlock deviceLabel)
    {
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, 4, 0, 4);
        grid.Height = 34;

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (iconGeometry != null)
        {
            namePanel.Children.Add(new Viewbox
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new System.Windows.Shapes.Path { Data = iconGeometry, Fill = Brushes.White, Stretch = Stretch.Uniform }
            });
        }
        else
        {
            namePanel.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = SegoeSymbolFont,
                FontSize = 16,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
        }
        namePanel.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        setVol = AddVolumeColumns(grid, ratio, onVol);

        var combo = CreateDeviceCombo(deviceGlyph, iconGeometry, deviceText, devices, onDevice, out deviceLabel);
        Grid.SetColumn(combo, 3);
        grid.Children.Add(combo);

        return grid;
    }

    private FrameworkElement BuildAppRow(AudioSessionInfo session, Action<double> onVol)
    {
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, 4, 0, 4);
        grid.Height = 34;

        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var iconHost = new Border
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Child = session.Icon != null
                ? new Image { Source = session.Icon, Width = 18, Height = 18, SnapsToDevicePixels = true }
                : new TextBlock
                {
                    Text = "\uE71D",
                    FontFamily = SegoeSymbolFont,
                    FontSize = 15,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
        };
        namePanel.Children.Add(iconHost);

        var nameLabel = new TextBlock
        {
            Text = session.DisplayName,
            Foreground = Brushes.White,
            FontSize = 13.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 130
        };
        namePanel.Children.Add(nameLabel);
        Grid.SetColumn(namePanel, 0);
        grid.Children.Add(namePanel);

        var setVol = AddVolumeColumns(grid, session.IsMuted ? 0 : session.Volume, onVol);
        _appRows[session.ProcessId] = (setVol, iconHost, nameLabel);

        var combo = CreateDeviceCombo("\uE898", null, Loc.Get("audio.noRedirect"), null, null, out _);
        Grid.SetColumn(combo, 3);
        grid.Children.Add(combo);

        return grid;
    }

    private Action<double> AddVolumeColumns(Grid grid, double ratio, Action<double> onVol)
    {
        var percent = new TextBlock
        {
            // White on the liquid glass skin so the percentages stay legible over
            // the refracted backdrop; muted grey on the default skin.
            Foreground = IsLiquidGlassEnabled ? Brushes.White : AudioMuted,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        };
        Grid.SetColumn(percent, 2);
        grid.Children.Add(percent);

        var volCell = CreateVolumeCell(ratio, onVol, percent, out var setVisual);
        Grid.SetColumn(volCell, 1);
        grid.Children.Add(volCell);
        return setVisual;
    }

    private FrameworkElement CreateVolumeCell(double ratio, Action<double> onChanged, TextBlock percentLabel, out Action<double> setVisual)
    {
        double currentRatio = Math.Clamp(ratio, 0, 1);
        const double thumbSize = 15;

        var cell = new Grid();
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        cell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var speaker = new TextBlock
        {
            Text = "\uE767",
            FontFamily = SegoeSymbolFont,
            FontSize = 14,
            Foreground = AudioMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        Grid.SetColumn(speaker, 0);
        cell.Children.Add(speaker);

        var area = new Grid
        {
            Height = 30,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 110
        };

        var trackScale = new ScaleTransform(1, 1);
        var track = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(4),
            Background = AudioTrack,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransform = trackScale,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        area.Children.Add(track);

        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(4),
            Background = AudioGreen,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 0,
            RenderTransformOrigin = new Point(0, 0.5)
        };
        var fillScale = new ScaleTransform(0, 1);
        fill.RenderTransform = fillScale;
        area.Children.Add(fill);

        var thumb = new Ellipse
        {
            Width = thumbSize,
            Height = thumbSize,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var thumbScale = new ScaleTransform(1, 1);
        var thumbTranslate = new TranslateTransform(0, 0);
        var thumbGroup = new TransformGroup();
        thumbGroup.Children.Add(thumbScale);
        thumbGroup.Children.Add(thumbTranslate);
        thumb.RenderTransform = thumbGroup;
        thumb.RenderTransformOrigin = new Point(0.5, 0.5);
        area.Children.Add(thumb);

        Grid.SetColumn(area, 1);
        cell.Children.Add(area);

        void UpdateVisual(double r)
        {
            double w = area.ActualWidth;
            if (w <= 0) return;
            double usable = Math.Max(0, w - thumbSize);
            double clamped = Math.Clamp(r, 0, 1);
            if (Math.Abs(fill.Width - w) > 0.1) fill.Width = w;
            if (Math.Abs(fillScale.ScaleX - clamped) > 0.0005) fillScale.ScaleX = clamped;
            double thumbX = clamped * usable;
            if (Math.Abs(thumbTranslate.X - thumbX) > 0.05) thumbTranslate.X = thumbX;
            string percent = ((int)Math.Round(clamped * 100)) + "%";
            if (!string.Equals(percentLabel.Text, percent, StringComparison.Ordinal))
                percentLabel.Text = percent;
        }

        area.Loaded += (_, _) => UpdateVisual(currentRatio);
        area.SizeChanged += (_, _) => UpdateVisual(currentRatio);
        percentLabel.Text = ((int)Math.Round(currentRatio * 100)) + "%";

        bool dragging = false;
        DateTime suppressUntil = DateTime.MinValue;
        void SetFromX(double x)
        {
            double w = area.ActualWidth;
            if (w <= 0) return;
            currentRatio = Math.Clamp(x / w, 0, 1);
            UpdateVisual(currentRatio);
            try { onChanged(currentRatio); } catch { }
        }

        setVisual = r =>
        {
            if (dragging || DateTime.UtcNow < suppressUntil) return;
            double next = Math.Clamp(r, 0, 1);
            if (Math.Abs(next - currentRatio) < 0.0005) return;
            currentRatio = next;
            UpdateVisual(currentRatio);
        };

        area.MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            area.CaptureMouse();
            SetFromX(e.GetPosition(area).X);
            e.Handled = true;
        };
        area.MouseMove += (_, e) => { if (dragging) SetFromX(e.GetPosition(area).X); };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            area.ReleaseMouseCapture();
            try { onChanged(currentRatio); } catch { }
            suppressUntil = DateTime.UtcNow.AddMilliseconds(600);
            if (!area.IsMouseOver) AnimateSliderHover(false);
            e.Handled = true;
        };

        void AnimateSliderHover(bool on)
        {
            int hoverFps = AnimationConfig.TargetFps;
            var dur = TimeSpan.FromMilliseconds(on ? 350 : 250);
            IEasingFunction ease = on
                ? new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
                : new CubicEase { EasingMode = EasingMode.EaseOut };

            double barScaleY = on ? 1.75 : 1.0;
            var barScaleAnim = new DoubleAnimation { To = barScaleY, Duration = dur, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(barScaleAnim, hoverFps);
            trackScale.BeginAnimation(ScaleTransform.ScaleYProperty, barScaleAnim);
            fillScale.BeginAnimation(ScaleTransform.ScaleYProperty, barScaleAnim);

            double thumbS = on ? 1.18 : 1.0;
            var sAnim = new DoubleAnimation { To = thumbS, Duration = dur, EasingFunction = ease };
            Timeline.SetDesiredFrameRate(sAnim, hoverFps);
            thumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, sAnim);
            thumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, sAnim);
        }

        area.MouseEnter += (_, _) => AnimateSliderHover(true);
        area.MouseLeave += (_, _) => { if (!dragging) AnimateSliderHover(false); };

        return cell;
    }

    private FrameworkElement CreateDeviceCombo(string glyph, Geometry? iconGeometry, string text,
        List<AudioDeviceInfo>? devices, Action<string>? onSelect, out TextBlock labelOut)
    {
        bool interactive = devices != null && devices.Count > 0 && onSelect != null;

        var border = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = AudioComboBg,
            BorderBrush = AudioComboBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = ColDevice,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = interactive ? Cursors.Hand : Cursors.Arrow
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = SegoeSymbolFont,
            FontSize = 13,
            Foreground = AudioMuted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        Grid.SetColumn(icon, 0);
        if (iconGeometry != null)
        {
            var vb = new Viewbox
            {
                Width = 13,
                Height = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Child = new System.Windows.Shapes.Path { Data = iconGeometry, Fill = AudioMuted, Stretch = Stretch.Uniform }
            };
            Grid.SetColumn(vb, 0);
            grid.Children.Add(vb);
        }
        else
        {
            grid.Children.Add(icon);
        }

        var label = new TextBlock
        {
            Text = text,
            Foreground = interactive ? Brushes.White : AudioMuted,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            FontFamily = AudioFont,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        labelOut = label;

        if (interactive)
        {
            var caret = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L4,4 L8,0 M0,6 L4,2 L8,6"),
                Stroke = AudioMuted,
                StrokeThickness = 1.3,
                Width = 8,
                Height = 8,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(caret, 2);
            grid.Children.Add(caret);
        }

        border.Child = grid;

        if (interactive)
        {
            border.MouseEnter += (_, _) => border.Background = AudioComboHover;
            border.MouseLeave += (_, _) => border.Background = AudioComboBg;
            border.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                ShowDeviceMenu(border, devices!, text, onSelect!);
            };
        }

        return border;
    }

    private void CloseDeviceMenu()
    {
        if (AudioOverlay == null) return;
        AudioOverlay.Children.Clear();
        AudioOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowDeviceMenu(Border anchor, List<AudioDeviceInfo> devices, string currentText, Action<string> onSelect)
    {
        if (AudioOverlay == null) return;
        CloseDeviceMenu();

        var list = new StackPanel();
        var container = new Border
        {
            Background = Frozen("#F21C1C1C"),
            BorderBrush = AudioComboBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(4),
            Width = anchor.ActualWidth > 0 ? anchor.ActualWidth : 206,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = list,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.5,
                Color = Colors.Black,
                Direction = 270,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            }
        };
        container.MouseLeftButtonDown += (_, e) => e.Handled = true;

        foreach (var device in devices)
        {
            bool selected = device.IsDefault ||
                string.Equals(device.FriendlyName, currentText, StringComparison.OrdinalIgnoreCase);

            var item = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Background = selected ? AudioComboHover : Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = device.FriendlyName,
                Foreground = Brushes.White,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                FontFamily = AudioFont,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 0);
            g.Children.Add(name);

            if (selected)
            {
                var check = new TextBlock
                {
                    Text = "\uE73E",
                    FontFamily = SegoeSymbolFont,
                    FontSize = 12,
                    Foreground = AudioGreen,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(check, 1);
                g.Children.Add(check);
            }

            item.Child = g;
            item.MouseEnter += (_, _) => { if (!selected) item.Background = AudioComboHover; };
            item.MouseLeave += (_, _) => { if (!selected) item.Background = Brushes.Transparent; };

            string id = device.Id;
            item.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                CloseDeviceMenu();
                onSelect(id);
            };
            list.Children.Add(item);
        }

        try
        {
            var pos = anchor.TransformToVisual(AudioContent).Transform(new Point(0, 0));
            container.Margin = new Thickness(pos.X, pos.Y + anchor.ActualHeight + 4, 0, 0);
        }
        catch { }

        AudioOverlay.Children.Add(container);
        AudioOverlay.Visibility = Visibility.Visible;
        AudioOverlay.MouseLeftButtonDown -= AudioOverlay_OutsideClick;
        AudioOverlay.MouseLeftButtonDown += AudioOverlay_OutsideClick;

        var slide = new TranslateTransform(0, -8);
        container.RenderTransform = slide;
        int fps = AnimationConfig.TargetFps;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = new Duration(TimeSpan.FromMilliseconds(160));
        var fade = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
        var drop = new DoubleAnimation(-8, 0, dur) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(fade, fps);
        Timeline.SetDesiredFrameRate(drop, fps);
        container.BeginAnimation(OpacityProperty, fade);
        slide.BeginAnimation(TranslateTransform.YProperty, drop);
    }

    private void AudioOverlay_OutsideClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseDeviceMenu();
    }
}
