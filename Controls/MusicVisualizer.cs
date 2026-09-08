using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using NAudio.CoreAudioApi;
using NAudio.Dmo;
using NAudio.Dsp;
using NAudio.Wave;

namespace VNotch.Controls
{
    public enum VisualizerState
    {
        Idle,
        Playing,
        Paused,
        Seeking
    }

    public class MusicVisualizer : FrameworkElement
    {
        #region Dependency Properties

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying), typeof(bool), typeof(MusicVisualizer),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty IsBufferingProperty =
            DependencyProperty.Register(nameof(IsBuffering), typeof(bool), typeof(MusicVisualizer),
                new PropertyMetadata(false, OnStateChanged));

        public static readonly DependencyProperty TrackIdProperty =
            DependencyProperty.Register(nameof(TrackId), typeof(string), typeof(MusicVisualizer),
                new PropertyMetadata(string.Empty, OnStateChanged));

        public static readonly DependencyProperty ActiveBrushProperty =
            DependencyProperty.Register(nameof(ActiveBrush), typeof(Brush), typeof(MusicVisualizer),
                new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        public bool IsBuffering
        {
            get => (bool)GetValue(IsBufferingProperty);
            set => SetValue(IsBufferingProperty, value);
        }

        public string TrackId
        {
            get => (string)GetValue(TrackIdProperty);
            set => SetValue(TrackIdProperty, value);
        }

        public Brush ActiveBrush
        {
            get => (Brush)GetValue(ActiveBrushProperty);
            set => SetValue(ActiveBrushProperty, value);
        }

        #endregion

        #region Constants & Config

        private const int BarCount = 5;
        private const double MinHeightRatio = 0.08;
        private const double MaxHeightRatio = 1.00;
        private const double BarWidthRatio = 0.10;
        private const double BarSpacingRatio = 0.05;
        private const double CornerRadiusRatio = 0.5;

        private const double AlphaAttack = 0.70;
        private const double AlphaRelease = 0.84;
        private const double AlphaPauseRelease = 0.985;
        private const double TauOpacity = 200;
        private const double CaptureRetryIntervalMs = 2500;
        private const double NoAudioPulseAmplitude = 0.05;
        private const double NoAudioPulseBase = 0.05;
        private const double LegacyRhythmMinMix = 0.10;
        private const double LegacyRhythmMaxMix = 0.25;
        private const double AudioPresenceThreshold = 0.0012;
        private const double DownwardDropBoost = 0.18;
        private const double MinReleaseAlpha = 0.66;
        private const double MotionContrast = 1.22;
        private const double RightBiasStrength = 0.12;
        private const double RightBiasDeadzone = 0.025;
        private const double MinHeightChangeThreshold = 0.0012;
        private const double SmallBarHeightThreshold = 0.30;
        private const double SmallBarAlphaBoost = 0.0;
        private const double SmallBarTargetDeadzone = 0.001;
        private const double AudioReactiveMotionFloor = 0.04;
        private const double AudioReactiveRhythmPush = 0.08;
        private const double AudioReactiveCrossBandLift = 0.08;

        private const double ReferenceFrameMs = 16.0;

        #endregion

        private readonly Stopwatch _stopwatch = new Stopwatch();
        private double _lastTickSeconds;
        private double _lastDtMs;
        private DateTime _lastCaptureRetryUtc = DateTime.MinValue;
        private bool _isRenderingActive;
        private bool _holdsCaptureLease;

        private readonly double[] _currentHeights = new double[BarCount];
        private readonly double[] _sortedHeights = new double[BarCount];
        private readonly double[] _drawHeights = new double[BarCount];
        private readonly double[] _smoothedHeights = new double[BarCount];
        private readonly float[] _levelsBuffer = new float[BarCount];

        private string? _hashCacheSid;
        private readonly uint[] _noAudioHash = new uint[BarCount];
        private readonly uint[] _floorHash = new uint[BarCount];
        private DpiScale? _cachedDpi;
        private double _currentOpacity = 0.2;
        private VisualizerState _state = VisualizerState.Idle;

        public MusicVisualizer()
        {
            Loaded += (s, e) => UpdateRenderingState();
            Unloaded += (s, e) =>
            {
                StopRendering();
                ReleaseCaptureLease();
            };
            IsVisibleChanged += (s, e) => UpdateRenderingState();

            for (int i = 0; i < BarCount; i++)
            {
                _currentHeights[i] = MinHeightRatio;
                _smoothedHeights[i] = MinHeightRatio;
            }
        }

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MusicVisualizer viz)
            {
                viz.UpdateInternalState();
            }
        }

        private void UpdateInternalState()
        {
            var oldState = _state;

            if (string.IsNullOrEmpty(TrackId))
                _state = VisualizerState.Idle;
            else if (IsBuffering)
                _state = VisualizerState.Seeking;
            else if (IsPlaying)
                _state = VisualizerState.Playing;
            else
                _state = VisualizerState.Paused;

            if (oldState != _state)
            {
                UpdateRenderingState();
                if (_state == VisualizerState.Idle)
                {
                    ReleaseCaptureLease();
                }
            }

            if (ShouldCaptureAudio)
            {
                EnsureAudioCaptureStarted(force: oldState != _state);
            }
        }

        private bool ShouldCaptureAudio =>
            _state == VisualizerState.Playing || _state == VisualizerState.Seeking;

        private void UpdateRenderingState()
        {
            if (!IsVisible || _state == VisualizerState.Idle)
            {
                StopRendering();
                return;
            }

            StartRendering();
        }

        private void StartRendering()
        {
            if (_isRenderingActive) return;
            _isRenderingActive = true;
            if (!_stopwatch.IsRunning) _stopwatch.Start();
            _lastTickSeconds = _stopwatch.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
        }

        private void StopRendering()
        {
            if (!_isRenderingActive) return;
            _isRenderingActive = false;
            CompositionTarget.Rendering -= OnRendering;
            _stopwatch.Stop();
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            EnsureAudioCaptureStarted();

            double totalSec = _stopwatch.Elapsed.TotalSeconds;
            double dt = totalSec - _lastTickSeconds;
            _lastTickSeconds = totalSec;

            if (dt <= 0) return;

            _lastDtMs = dt * 1000.0;
            bool isSettled = UpdateAnimation(dt, totalSec);

            if (isSettled && _state == VisualizerState.Paused)
            {
                InvalidateVisual();
                StopRendering();
                ReleaseCaptureLease();
                return;
            }

            InvalidateVisual();
        }

#pragma warning disable S3776 // Cognitive complexity is inherent to the multi-state audio reactivity animation loop
        private bool UpdateAnimation(double dt, double totalSec)
        {
            bool isSettled = true;

            double targetOpacity = _state switch
            {
                VisualizerState.Idle => 0.2,
                VisualizerState.Paused => 0.5,
                VisualizerState.Playing => 1.0,
                VisualizerState.Seeking => 1.0,
                _ => 0.2
            };

            _currentOpacity += (targetOpacity - _currentOpacity) * (1 - Math.Exp(-dt * 1000 / TauOpacity));

            string sid = TrackId ?? "";
            EnsureHashCache(sid);

            float[] levels = GetLatestDisplayLevels(out bool hasFreshAudio, out double beatAccent);
            double audioEnergy = 0;
            for (int i = 0; i < BarCount; i++) audioEnergy += Math.Clamp(levels[i], 0f, 1f);
            audioEnergy = Math.Clamp(Math.Pow(audioEnergy / BarCount, 0.85), 0.0, 1.0);

            bool hasAudibleEnergy = hasFreshAudio && audioEnergy > AudioPresenceThreshold;
            bool canDriveFromAudio = hasAudibleEnergy &&
                                     (_state == VisualizerState.Playing || _state == VisualizerState.Paused);

            for (int i = 0; i < BarCount; i++)
            {
                double targetH;

                if (canDriveFromAudio)
                {
                    double band = Math.Clamp(levels[i], 0.0, 1.0);
                    double crossBandLift = Math.Clamp(audioEnergy * AudioReactiveCrossBandLift, 0.0, 0.16);
                    double audioShaped = Math.Pow(Math.Clamp(band + crossBandLift, 0.0, 1.0), 0.88);
                    double rhythm = GetAudioReactiveRhythmAt(i, totalSec, audioEnergy);
                    double rhythmMixBase = LegacyRhythmMinMix + ((1.0 - audioEnergy) * (LegacyRhythmMaxMix - LegacyRhythmMinMix));
                    double rhythmMix = Math.Clamp(
                        rhythmMixBase + (AudioReactiveRhythmPush * (0.35 + audioEnergy)),
                        0.0,
                        0.30);
                    double normalized = (audioShaped * (1.0 - rhythmMix)) + (rhythm * rhythmMix);
                    normalized += (rhythm - 0.5) * (0.08 + (audioEnergy * 0.08));
                    normalized = Math.Max(normalized, GetAudioReactiveFloor(i, totalSec, audioEnergy, beatAccent));
                    normalized = Math.Clamp(normalized + (beatAccent * GetBeatLiftWeight(i) * 0.42), 0.0, 1.0);
                    normalized = ApplyMiniBarSensitivity(normalized);
                    normalized = ApplyBarPersonality(i, normalized, audioEnergy);
                    normalized = ApplyMotionContrast(normalized);
                    targetH = MapNormalizedToHeight(normalized);
                }
                else if (_state is VisualizerState.Playing or VisualizerState.Seeking)
                {
                    double normalized = ApplyMotionContrast(GetNoAudioPulseAt(i, totalSec));
                    targetH = MapNormalizedToHeight(normalized);
                }
                else if (_state == VisualizerState.Paused)
                {
                    targetH = MinHeightRatio;
                }
                else
                {
                    targetH = MinHeightRatio;
                }

                double dynamicRelease = Math.Max(0.50, AlphaRelease - (beatAccent * 0.08));
                double baseAlpha;
                if (targetH > _currentHeights[i])
                {
                    baseAlpha = AlphaAttack;
                }
                else
                {
                    if (_state == VisualizerState.Paused)
                    {
                        baseAlpha = AlphaPauseRelease;
                    }
                    else
                    {

                        double fallRatio = Math.Clamp(
                            (_currentHeights[i] - targetH) / (MaxHeightRatio - MinHeightRatio),
                            0.0, 1.0);
                        baseAlpha = Math.Max(MinReleaseAlpha, dynamicRelease - (fallRatio * DownwardDropBoost));
                    }
                }

                double smallBarRef = Math.Max(_currentHeights[i], targetH);
                if (smallBarRef < SmallBarHeightThreshold && _state != VisualizerState.Paused)
                {
                    double smallness = 1.0 - (smallBarRef / SmallBarHeightThreshold);
                    baseAlpha = Math.Min(0.985, baseAlpha + (smallness * SmallBarAlphaBoost));

                    if (Math.Abs(targetH - _currentHeights[i]) < SmallBarTargetDeadzone * (1.0 + smallness))
                    {
                        targetH = _currentHeights[i];
                    }
                }

                double dtMs = dt * 1000.0;
                double alpha = Math.Pow(baseAlpha, dtMs / ReferenceFrameMs);

                double oldH = _currentHeights[i];
                double newH = (_currentHeights[i] * alpha) + (targetH * (1 - alpha));

                if (Math.Abs(newH - _currentHeights[i]) > MinHeightChangeThreshold)
                {
                    _currentHeights[i] = newH;
                    if (Math.Abs(_currentHeights[i] - oldH) > 0.001) isSettled = false;
                }
                else
                {
                    _currentHeights[i] = oldH;
                }
            }

            return isSettled;
        }
#pragma warning restore S3776

        private static double MapNormalizedToHeight(double normalized)
        {
            double clamped = Math.Clamp(normalized, 0.0, 1.0);
            return MinHeightRatio + clamped * (MaxHeightRatio - MinHeightRatio);
        }

        private static double ApplyMotionContrast(double normalized)
        {
            double clamped = Math.Clamp(normalized, 0.0, 1.0);
            return Math.Clamp(0.5 + ((clamped - 0.5) * MotionContrast), 0.0, 1.0);
        }

        private static double ApplyMiniBarSensitivity(double normalized)
        {
            return Math.Clamp(normalized, 0.0, 1.0);
        }

        private static double ApplyBarPersonality(int barIndex, double normalized, double energy)
        {
            double clamped = Math.Clamp(normalized, 0.0, 1.0);
            double exponent = barIndex switch
            {
                0 => 1.42,
                1 => 1.18,
                2 => 0.88,
                3 => 1.36,
                4 => 1.58,
                _ => 1.0
            };

            double gain = barIndex switch
            {
                0 => 1.06,
                1 => 1.00,
                2 => 1.10,
                3 => 0.92,
                4 => 0.78,
                _ => 1.0
            };

            double shaped = Math.Pow(clamped, exponent) * gain;
            double energyLift = energy * (barIndex == 2 ? 0.045 : 0.012);
            return Math.Clamp(shaped + energyLift, 0.0, 1.0);
        }

        private double GetNoAudioPulseAt(int index, double t)
        {
            uint hash = _noAudioHash[index];
            double phase = (hash % 1000) / 1000.0 * Math.PI * 2;
            double freq = 0.13 + (hash % 15) / 200.0;
            double wavePrimary = 0.5 + 0.5 * Math.Sin((t * freq * Math.PI * 2) + phase);
            double waveSecondary = 0.5 + 0.5 * Math.Sin((t * (freq * 1.35) * Math.PI * 2) + (phase * 0.37));
            double wave = (wavePrimary * 0.85) + (waveSecondary * 0.15);
            return NoAudioPulseBase + (wave * NoAudioPulseAmplitude);
        }

        private static double GetBeatLiftWeight(int barIndex)
        {
            return barIndex switch
            {
                0 => 0.34,
                1 => 0.26,
                2 => 0.10,
                3 => 0.18,
                4 => 0.14,
                _ => 0.18
            };
        }

        private double GetAudioReactiveFloor(int index, double t, double energy, double beatAccent)
        {
            uint hash = _floorHash[index];
            double phase = (hash % 1000) / 1000.0 * Math.PI * 2;
            double freq = 0.34 + ((hash % 21) / 200.0) + (energy * 0.12);
            double pulse = 0.5 + 0.5 * Math.Sin((t * freq * Math.PI * 2) + phase);
            double counterPulse = 0.5 + 0.5 * Math.Sin((t * (freq * 1.73) * Math.PI * 2) + (phase * 0.43));
            double motion = (pulse * 0.72) + (counterPulse * 0.28);
            double floorScale = index switch
            {
                0 => 0.55,
                1 => 0.78,
                2 => 1.02,
                3 => 0.66,
                4 => 0.46,
                _ => 1.0
            };
            double floor = (AudioReactiveMotionFloor + (energy * 0.07) + (motion * (0.035 + (energy * 0.04)))) * floorScale;
            floor += beatAccent * GetBeatLiftWeight(index) * 0.10;
            return Math.Clamp(floor, 0.0, 0.26);
        }

        private double GetAudioReactiveRhythmAt(int index, double t, double energy)
        {
            uint hash = _noAudioHash[index];
            double phase = (hash % 1000) / 1000.0 * Math.PI * 2;
            double baseFreq = 0.20 + (hash % 18) / 200.0;
            double speed = 0.28 + (energy * 0.16);

            double value = Math.Sin((t * baseFreq * speed * Math.PI * 2) + phase) * 0.38;
            value += Math.Sin((t * (0.13 + (energy * 0.10)) * Math.PI * 2) + (phase * 0.5)) * 0.18;
            value += Math.Sin((t * (baseFreq * 0.32) * Math.PI * 2) + (phase * 1.7)) * 0.06;

            double noiseRate = 0.45 + (energy * 0.6);
            uint noiseSeed = ContinueHashInt(_noAudioHash[index], (int)Math.Floor(t * noiseRate));
            value += (((noiseSeed % 200) / 100.0) - 1.0) * (0.003 + (energy * 0.005));

            return Math.Clamp(0.5 + value, 0.0, 1.0);
        }

        private void EnsureHashCache(string sid)
        {
            if (_hashCacheSid == sid) return;
            _hashCacheSid = sid;
            for (int i = 0; i < BarCount; i++)
            {
                _noAudioHash[i] = GetDeterministicHash(sid + i);
                _floorHash[i] = GetDeterministicHash("floor:" + sid + i);
            }
        }

        private static uint GetDeterministicHash(string str)
        {
            uint hash = 2166136261;
            foreach (char c in str)
                hash = (hash ^ (uint)c) * 16777619;
            return hash;
        }

        private static uint ContinueHashInt(uint hash, int value)
        {
            if (value < 0)
            {
                hash = (hash ^ (uint)'-') * 16777619;
                value = -value;
            }
            Span<char> digits = stackalloc char[11];
            int pos = digits.Length;
            do
            {
                digits[--pos] = (char)('0' + (value % 10));
                value /= 10;
            } while (value > 0);
            for (int k = pos; k < digits.Length; k++)
                hash = (hash ^ (uint)digits[k]) * 16777619;
            return hash;
        }

        private void EnsureAudioCaptureStarted(bool force = false)
        {
            if (!ShouldCaptureAudio) return;
            AcquireCaptureLease();
            if (_capture != null) return;

            var now = DateTime.UtcNow;
            if (!force && (now - _lastCaptureRetryUtc).TotalMilliseconds < CaptureRetryIntervalMs) return;

            _lastCaptureRetryUtc = now;
            StartAudioCapture();
        }

#pragma warning disable S2696 // Multi-instance visualizers coordinate a single shared loopback audio capture lease
        private void AcquireCaptureLease()
        {
            lock (_lockObj)
            {
                if (_holdsCaptureLease) return;
                _holdsCaptureLease = true;
                _captureLeaseCount++;
            }
        }

        private void ReleaseCaptureLease()
        {
            bool stopPhysical = false;
            lock (_lockObj)
            {
                if (!_holdsCaptureLease) return;
                _holdsCaptureLease = false;
                _captureLeaseCount--;
                if (_captureLeaseCount <= 0)
                {
                    _captureLeaseCount = 0;
                    stopPhysical = true;
                }
            }

            if (stopPhysical) StopAudioCapture();
        }
#pragma warning restore S2696

        private void PrepareDrawHeights()
        {
            double smoothingFactor = Math.Pow(0.56, _lastDtMs / ReferenceFrameMs);
            for (int i = 0; i < BarCount; i++)
            {
                double targetSmoothing = smoothingFactor;
                double refHeight = Math.Max(_smoothedHeights[i], _currentHeights[i]);
                if (refHeight < SmallBarHeightThreshold)
                {
                    double smallness = 1.0 - (refHeight / SmallBarHeightThreshold);
                    double extra = Math.Pow(0.58, _lastDtMs / ReferenceFrameMs) - smoothingFactor;
                    targetSmoothing = Math.Min(0.78, smoothingFactor + (extra * smallness));
                }

                _smoothedHeights[i] = (_smoothedHeights[i] * targetSmoothing) + (_currentHeights[i] * (1 - targetSmoothing));
            }

            Array.Copy(_smoothedHeights, _sortedHeights, BarCount);
            for (int i = 0; i < BarCount - 1; i++)
            {
                for (int j = i + 1; j < BarCount; j++)
                {
                    if (_sortedHeights[j] > _sortedHeights[i])
                    {
                        double temp = _sortedHeights[i];
                        _sortedHeights[i] = _sortedHeights[j];
                        _sortedHeights[j] = temp;
                    }
                }
            }

            double max = _sortedHeights[0];
            double min = _sortedHeights[BarCount - 1];
            double spread = Math.Clamp(max - min, 0.0, 1.0);
            double bias = RightBiasStrength;
            if (spread < RightBiasDeadzone)
            {
                bias *= (spread / RightBiasDeadzone);
            }

            for (int i = 0; i < BarCount; i++)
            {
                _drawHeights[i] = (_smoothedHeights[i] * (1.0 - bias)) + (_sortedHeights[i] * bias);
            }
        }

        private Color _cachedGradientBaseColor;
        private LinearGradientBrush? _cachedBarGradient;

        private LinearGradientBrush GetBarGradientBrush()
        {
            Color baseColor;
            if (ActiveBrush is SolidColorBrush scb)
            {
                baseColor = scb.Color;
            }
            else
            {
                baseColor = Colors.White;
            }

            if (_cachedBarGradient == null || baseColor != _cachedGradientBaseColor)
            {
                _cachedGradientBaseColor = baseColor;

                byte dr = (byte)(baseColor.R * 0.55);
                byte dg = (byte)(baseColor.G * 0.55);
                byte db = (byte)(baseColor.B * 0.55);
                var darkColor = Color.FromArgb(baseColor.A, dr, dg, db);

                _cachedBarGradient = new LinearGradientBrush(baseColor, darkColor, 90.0);
                _cachedBarGradient.MappingMode = BrushMappingMode.RelativeToBoundingBox;
            }

            return _cachedBarGradient;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _cachedDpi = newDpi;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            double width = ActualWidth;
            double height = ActualHeight;

            if (width < 1 || height < 1 || ActiveBrush == null) return;

            DpiScale dpi = _cachedDpi ??= VisualTreeHelper.GetDpi(this);

            double barWidth = width * BarWidthRatio;
            double spacing = (width * BarSpacingRatio) + 0.2;
            double totalContentWidth = (barWidth * BarCount) + (spacing * (BarCount - 1));

            double startX = (width - totalContentWidth) / 2;
            double centerY = height / 2;

            double snappedW = Math.Max(1.0, Math.Round(barWidth * dpi.DpiScaleX) / dpi.DpiScaleX);

            drawingContext.PushOpacity(_currentOpacity);

            PrepareDrawHeights();

            var gradientBrush = GetBarGradientBrush();

            for (int i = 0; i < BarCount; i++)
            {
                double barHeight = _drawHeights[i] * height;
                double x = startX + i * (barWidth + spacing);

                double halfHeight = barHeight / 2;
                double top = centerY - halfHeight;
                double bottom = centerY + halfHeight;

                double snappedX = Math.Round(x * dpi.DpiScaleX) / dpi.DpiScaleX;
                double snappedTop = Math.Round(top * dpi.DpiScaleY) / dpi.DpiScaleY;
                double snappedBottom = Math.Round(bottom * dpi.DpiScaleY) / dpi.DpiScaleY;
                double snappedH = snappedBottom - snappedTop;

                double radius = snappedW * CornerRadiusRatio;

                drawingContext.DrawRoundedRectangle(gradientBrush, null,
                    new Rect(snappedX, snappedTop, snappedW, snappedH),
                    radius, radius);
            }

            drawingContext.Pop();
        }

        #region Audio Loopback Capture

        private static WasapiLoopbackCapture? _capture;
        private static readonly object _lockObj = new object();
        private static int _captureLeaseCount;
        private static string _audioDeviceId = string.Empty;
        private const int FftLength = 512;
        private const int FftM = 9;
        private const double MinDb = -90.0;
        private const double MaxDb = 0.0;
        private const double CompressionPower = 0.38;
        private const double SpectralPreGain = 48.0;
        private const double RmsWindowSeconds = 0.020;
        private const int FreshAudioTimeoutMs = 800;
        private const double AgcFloor = 0.008;
        private const double AgcRelease = 0.985;
        private const double KickTransientThreshold = 0.018;
        private const double SnareTransientThreshold = 0.016;
        private const double KickTransientGain = 9.0;
        private const double SnareTransientGain = 10.0;
        private const double KickAccentDecay = 0.955;
        private const double SnareAccentDecay = 0.950;
        private const double BeatTransientThreshold = 0.014;
        private const double BeatTransientGain = 10.0;
        private const double BeatAccentDecay = 0.955;
        private const double BeatRmsDeltaThreshold = 0.004;
        private const double BassDominanceStart = 1.10;
        private const double BassDominanceSpan = 1.00;
        private const double MaxLowAttenuation = 0.34;
        private const double MaxKickAttenuation = 0.42;
        private const double SpectralContrastStart = 0.03;
        private const double SpectralContrastSpan = 0.30;
        private const double SpectralContrastStrength = 1.60;
        private const double SpectralDominantBoost = 0.30;
        private const double SpectralSubtractiveCut = 0.28;
        private const double DynamicRangeExpansionPower = 1.20;
        private const double DynamicRangeExpansionBlend = 0.18;
        private const double BarContrastStart = 0.06;
        private const double BarContrastSpan = 0.35;
        private const double BarContrastStrength = 0.85;
        private const double BarContrastBoost = 0.22;
        private const double BarContrastCut = 0.16;
        private const double VisualMaxPeakTarget = 0.74;
        private const double VisualMaxFloor = 0.18;
        private const double VisualMaxBandFloor = 0.06;
        private const double VisualMaxBandLift = 0.10;
        private const double VisualMaxGainLimit = 1.55;
        private const double RolePeakRelease = 0.992;
        private const double RolePeakFloor = 0.16;

        private static readonly float[] _fftInputBuffer = new float[FftLength];
        private static int _fftInputPos = 0;
        private static readonly Complex[] _fftData = new Complex[FftLength];
        private static readonly double[] _hammingWindow = CreateHammingWindow(FftLength);

        private static double[] CreateHammingWindow(int length)
        {
            var window = new double[length];
            for (int i = 0; i < length; i++)
            {
                window[i] = 0.54 - 0.46 * Math.Cos((2 * Math.PI * i) / (length - 1));
            }
            return window;
        }
        private static readonly float[] _displayTargets = new float[BarCount];
        private static readonly double[] _rolePeaks = { 0.38, 0.36, 0.40, 0.34, 0.32 };
        private static double _rmsSumSquares;
        private static int _rmsSampleCount;
        private static int _rmsWindowSamples = 882;
        private static float _latestRmsNormalized;
        private static int _sampleRate = 44100;
        private static double _agcPeak = 0.35;
        private static double _prevKickEnergy;
        private static double _prevSnareEnergy;
        private static double _kickAccent;
        private static double _snareAccent;
        private static double _prevRmsForBeat;
        private static double _beatAccent;

        private static readonly object _outputLock = new object();
        private static readonly float[] _publishedTargets = new float[BarCount];
        private static float _publishedBeatAccent;
        private static long _publishedFrameTicks;

        public static void ConfigureAudioDevice(string? deviceId)
        {
            deviceId ??= string.Empty;
            bool shouldRestart;

            lock (_lockObj)
            {
                if (string.Equals(_audioDeviceId, deviceId, StringComparison.Ordinal))
                {
                    return;
                }

                _audioDeviceId = deviceId;
                shouldRestart = _capture != null;
            }

            if (shouldRestart)
            {
                StopAudioCapture();
            }
        }

        private static void StartAudioCapture()
        {
            lock (_lockObj)
            {
                if (_capture != null) return;

                try
                {
                    var device = ResolveLoopbackDevice(_audioDeviceId);
                    _capture = device != null
                        ? new WasapiLoopbackCapture(device)
                        : new WasapiLoopbackCapture();
                    _sampleRate = _capture.WaveFormat.SampleRate;
                    _rmsWindowSamples = Math.Max(64, (int)(_sampleRate * RmsWindowSeconds));
                    _capture.DataAvailable += OnAudioDataAvailable;
                    _capture.RecordingStopped += OnCaptureStopped;
                    _capture.StartRecording();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to initialize audio capture: " + ex.Message);
                    _capture = null;
                }
            }
        }

        private static MMDevice? ResolveLoopbackDevice(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                return null;
            }

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.GetDevice(deviceId);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Failed to resolve visualizer audio device: " + ex.Message);
                return null;
            }
        }

        private static void StopAudioCapture()
        {
            WasapiLoopbackCapture? captureToDispose = null;
            lock (_lockObj)
            {
                if (_capture == null) return;
                captureToDispose = _capture;
                _capture = null;
                ResetAudioState();
            }

            try
            {
                captureToDispose.DataAvailable -= OnAudioDataAvailable;
                captureToDispose.RecordingStopped -= OnCaptureStopped;
                captureToDispose.StopRecording();
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error("MUSIC-VIS-STOP", ex.ToString());
            }
            finally
            {
                captureToDispose.Dispose();
            }
        }

        private static void OnCaptureStopped(object? sender, StoppedEventArgs e)
        {
            lock (_lockObj)
            {
                if (sender != null && ReferenceEquals(_capture, sender))
                {
                    _capture.DataAvailable -= OnAudioDataAvailable;
                    _capture.RecordingStopped -= OnCaptureStopped;
                    _capture.Dispose();
                    _capture = null;
                    ResetAudioState();
                }
            }
        }

        private static void ResetAudioState()
        {
            Array.Clear(_fftInputBuffer, 0, _fftInputBuffer.Length);
            Array.Clear(_displayTargets, 0, _displayTargets.Length);
            ResetRolePeaks();
            _fftInputPos = 0;
            _rmsSumSquares = 0;
            _rmsSampleCount = 0;
            _latestRmsNormalized = 0;
            _agcPeak = 0.35;
            _prevKickEnergy = 0;
            _prevSnareEnergy = 0;
            _kickAccent = 0;
            _snareAccent = 0;
            _prevRmsForBeat = 0;
            _beatAccent = 0;

            lock (_outputLock)
            {
                Array.Clear(_publishedTargets, 0, _publishedTargets.Length);
                _publishedBeatAccent = 0;
                _publishedFrameTicks = 0;
            }
        }

        private static void ResetRolePeaks()
        {
            _rolePeaks[0] = 0.38;
            _rolePeaks[1] = 0.36;
            _rolePeaks[2] = 0.40;
            _rolePeaks[3] = 0.34;
            _rolePeaks[4] = 0.32;
        }

        private static void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
        {
            var capture = _capture;
            if (capture == null) return;

            var waveFormat = capture.WaveFormat;
            int bytesPerSample = waveFormat.BitsPerSample / 8;
            if (bytesPerSample <= 0)
            {
                bytesPerSample = waveFormat.Encoding is WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Extensible
                    ? 4
                    : 2;
            }
            int channels = Math.Max(1, waveFormat.Channels);
            int bytesPerFrame = bytesPerSample * channels;
            if (bytesPerFrame <= 0) return;

            int framesRecorded = e.BytesRecorded / bytesPerFrame;
            if (framesRecorded <= 0) return;

            for (int frame = 0; frame < framesRecorded; frame++)
            {
                int frameOffset = frame * bytesPerFrame;
                double mixed = 0;

                for (int ch = 0; ch < channels; ch++)
                {
                    int sampleOffset = frameOffset + (ch * bytesPerSample);
                    mixed += ReadSampleAsFloat(e.Buffer, sampleOffset, waveFormat);
                }

                PushSample((float)(mixed / channels));
            }

            lock (_outputLock)
            {
                _publishedFrameTicks = DateTime.UtcNow.Ticks;
            }
        }

        private static void PushSample(float sample)
        {
            _fftInputBuffer[_fftInputPos++] = sample;
            _rmsSumSquares += sample * sample;
            _rmsSampleCount++;

            if (_rmsSampleCount >= _rmsWindowSamples)
            {
                double rms = Math.Sqrt(_rmsSumSquares / Math.Max(1, _rmsSampleCount));
                _latestRmsNormalized = (float)NormalizeAmplitude(rms);
                _rmsSumSquares = 0;
                _rmsSampleCount = 0;
            }

            if (_fftInputPos >= FftLength)
            {
                ComputeDisplayTargets();
                _fftInputPos = 0;
            }
        }

        private static void ComputeDisplayTargets()
        {
            for (int i = 0; i < FftLength; i++)
            {
                _fftData[i].X = (float)(_fftInputBuffer[i] * _hammingWindow[i]);
                _fftData[i].Y = 0;
            }

            FastFourierTransform.FFT(true, FftM, _fftData);

            double subBass = NormalizeAmplitude(ComputeBandEnergy(20, 60) * (SpectralPreGain * 0.9));
            double bass = NormalizeAmplitude(ComputeBandEnergy(60, 250) * (SpectralPreGain * 0.85));
            double lowMid = NormalizeAmplitude(ComputeBandEnergy(250, 500) * (SpectralPreGain * 1.2));
            double mid = NormalizeAmplitude(ComputeBandEnergy(500, 2000) * (SpectralPreGain * 1.5));
            double highMid = NormalizeAmplitude(ComputeBandEnergy(2000, 4000) * (SpectralPreGain * 3.2));
            double high = NormalizeAmplitude(ComputeBandEnergy(4000, 8000) * (SpectralPreGain * 3.8));
            double kick = NormalizeAmplitude(ComputeBandEnergy(45, 130) * (SpectralPreGain * 1.0));
            double snare = NormalizeAmplitude(ComputeBandEnergy(1400, 5000) * (SpectralPreGain * 2.8));
            double melody = NormalizeAmplitude(ComputeBandEnergy(350, 2400) * (SpectralPreGain * 1.7));
            double hat = NormalizeAmplitude(ComputeBandEnergy(6500, 11000) * (SpectralPreGain * 4.2));
            double air = NormalizeAmplitude(ComputeBandEnergy(9000, 16000) * (SpectralPreGain * 5.0));
            double rms = _latestRmsNormalized;

            double peak = Math.Max(
                Math.Max(Math.Max(Math.Max(subBass, bass), Math.Max(lowMid, mid)), Math.Max(highMid, high)),
                Math.Max(Math.Max(kick, snare), rms));
            _agcPeak = Math.Max(peak, _agcPeak * AgcRelease);
            double agcScale = 1.0 / Math.Max(AgcFloor, _agcPeak);

            double quietBoost = 1.0;
            if (_agcPeak < 0.05)
            {
                double quietness = 1.0 - (_agcPeak / 0.05);
                quietBoost = 1.0 + (quietness * 1.2);
            }
            agcScale *= quietBoost;

            subBass = Math.Clamp(subBass * agcScale, 0.0, 1.0);
            bass = Math.Clamp(bass * agcScale, 0.0, 1.0);
            lowMid = Math.Clamp(lowMid * agcScale, 0.0, 1.0);
            mid = Math.Clamp(mid * agcScale, 0.0, 1.0);
            highMid = Math.Clamp(highMid * agcScale, 0.0, 1.0);
            high = Math.Clamp(high * agcScale, 0.0, 1.0);
            kick = Math.Clamp(kick * agcScale, 0.0, 1.0);
            snare = Math.Clamp(snare * agcScale, 0.0, 1.0);
            melody = Math.Clamp(melody * agcScale, 0.0, 1.0);
            hat = Math.Clamp(hat * agcScale, 0.0, 1.0);
            air = Math.Clamp(air * agcScale, 0.0, 1.0);
            rms = Math.Clamp(rms * agcScale, 0.0, 1.0);

            double postAgcMax = Math.Max(Math.Max(Math.Max(subBass, bass), Math.Max(lowMid, mid)), Math.Max(highMid, high));
            double postAgcMin = Math.Min(Math.Min(Math.Min(subBass, bass), Math.Min(lowMid, mid)), Math.Min(highMid, high));
            double postAgcSpread = postAgcMax - postAgcMin;
            if (postAgcSpread < 0.15 && postAgcMax > 0.5)
            {
                double flatness = 1.0 - (postAgcSpread / 0.15);
                double loudness = Math.Clamp((postAgcMax - 0.5) / 0.5, 0.0, 1.0);
                double spreadGain = flatness * loudness * 2.5;
                double mean = (subBass + bass + lowMid + mid + highMid + high) / 6.0;
                subBass = Math.Clamp(mean + (subBass - mean) * (1.0 + spreadGain), 0.0, 1.0);
                bass = Math.Clamp(mean + (bass - mean) * (1.0 + spreadGain), 0.0, 1.0);
                lowMid = Math.Clamp(mean + (lowMid - mean) * (1.0 + spreadGain), 0.0, 1.0);
                mid = Math.Clamp(mean + (mid - mean) * (1.0 + spreadGain), 0.0, 1.0);
                highMid = Math.Clamp(mean + (highMid - mean) * (1.0 + spreadGain), 0.0, 1.0);
                high = Math.Clamp(mean + (high - mean) * (1.0 + spreadGain), 0.0, 1.0);
            }

            double nonBass = (mid * 0.7) + (high * 0.6) + 1e-5;
            double bassDominance = (subBass + bass) / nonBass;
            double bassExcess = Math.Clamp((bassDominance - BassDominanceStart) / BassDominanceSpan, 0.0, 1.0);
            double bassScale = 1.0 - (bassExcess * (MaxLowAttenuation * 0.2));
            double kickScale = 1.0 - (bassExcess * (MaxKickAttenuation * 0.2));
            subBass *= bassScale;
            bass *= bassScale;
            kick *= kickScale;

            double highBoost = 1.0 + (bassExcess * 0.7);
            highMid *= highBoost;
            high *= highBoost;

            double kickDelta = Math.Max(0.0, kick - _prevKickEnergy);
            double snareDelta = Math.Max(0.0, snare - _prevSnareEnergy);
            double rmsDelta = Math.Max(0.0, rms - _prevRmsForBeat);
            _prevKickEnergy = kick;
            _prevSnareEnergy = snare;
            _prevRmsForBeat = rms;

            double kickHit = Math.Clamp((kickDelta - KickTransientThreshold) * KickTransientGain, 0.0, 1.0);
            double snareHit = Math.Clamp((snareDelta - SnareTransientThreshold) * SnareTransientGain, 0.0, 1.0);
            double rmsHit = Math.Clamp((rmsDelta - BeatRmsDeltaThreshold) * 18.0, 0.0, 1.0);
            _kickAccent = Math.Max(kickHit, _kickAccent * KickAccentDecay);
            _snareAccent = Math.Max(snareHit, _snareAccent * SnareAccentDecay);
            double beatDriver = (_kickAccent * 0.55) + (_snareAccent * 0.30) + (rmsHit * 0.35);
            double beatHit = Math.Clamp((beatDriver - BeatTransientThreshold) * BeatTransientGain, 0.0, 1.0);
            _beatAccent = Math.Max(beatHit, _beatAccent * BeatAccentDecay);

            ApplySpectralContrast(ref subBass, ref bass, ref lowMid, ref mid, ref highMid, ref high, ref kick, ref snare, ref rms);
            melody = ExpandDynamicRange(melody);
            hat = ExpandDynamicRange(hat);
            air = ExpandDynamicRange(air);

            double grooveLift = rms * 0.05;
            _displayTargets[0] = (float)Math.Clamp((kick * 0.48) + (subBass * 0.30) + (bass * 0.12) + grooveLift + (_kickAccent * 0.26), 0.0, 1.0);
            _displayTargets[1] = (float)Math.Clamp((snare * 0.48) + (highMid * 0.20) + (mid * 0.14) + (lowMid * 0.08) + (_snareAccent * 0.28), 0.0, 1.0);
            _displayTargets[2] = (float)Math.Clamp((melody * 0.46) + (mid * 0.26) + (lowMid * 0.18) + (rms * 0.10), 0.0, 1.0);
            _displayTargets[3] = (float)Math.Clamp((hat * 0.52) + (high * 0.20) + (snare * 0.12) + (highMid * 0.10) + (_snareAccent * 0.12), 0.0, 1.0);
            _displayTargets[4] = (float)Math.Clamp((air * 0.54) + (high * 0.22) + (hat * 0.12) + (rms * 0.04), 0.0, 1.0);

            ApplyInstrumentRoleResponse(_displayTargets, rms);
            ApplyBarContrast(_displayTargets);
            NormalizeDisplayTargetsToMaxVisual(_displayTargets);

            lock (_outputLock)
            {
                Array.Copy(_displayTargets, _publishedTargets, BarCount);
                _publishedBeatAccent = (float)_beatAccent;
            }
        }

        private static double ComputeBandEnergy(int fromHz, int toHz)
        {
            int maxBin = (FftLength / 2) - 1;
            if (_sampleRate <= 0 || maxBin <= 1) return 0;

            int start = FrequencyToBin(fromHz);
            int end = FrequencyToBin(toHz);
            if (end < start) (start, end) = (end, start);

            start = Math.Clamp(start, 1, maxBin);
            end = Math.Clamp(end, start, maxBin);

            double sumSquares = 0;
            int count = 0;
            for (int i = start; i <= end; i++)
            {
                double mag = Math.Sqrt((_fftData[i].X * _fftData[i].X) + (_fftData[i].Y * _fftData[i].Y));
                double normalizedMag = mag / (FftLength * 0.5);
                sumSquares += normalizedMag * normalizedMag;
                count++;
            }

            return count > 0 ? Math.Sqrt(sumSquares / count) : 0;
        }

        private static int FrequencyToBin(int frequencyHz)
        {
            if (_sampleRate <= 0) return 1;
            return (int)Math.Round((frequencyHz / (double)_sampleRate) * FftLength);
        }

        private static double NormalizeAmplitude(double amplitude)
        {
            double db = 20 * Math.Log10(Math.Max(amplitude, 1e-9));
            double normalized = (db - MinDb) / (MaxDb - MinDb);
            normalized = Math.Clamp(normalized, 0.0, 1.0);
            return Math.Pow(normalized, CompressionPower);
        }

#pragma warning disable S107 // High parameter count is required for passing 6 FFT frequency bands and 3 transient components by ref
        private static void ApplySpectralContrast(ref double subBass, ref double bass, ref double lowMid, ref double mid, ref double highMid, ref double high, ref double kick, ref double snare, ref double rms)
        {
            double max = Math.Max(Math.Max(Math.Max(subBass, bass), Math.Max(lowMid, mid)), Math.Max(highMid, high));
            double min = Math.Min(Math.Min(Math.Min(subBass, bass), Math.Min(lowMid, mid)), Math.Min(highMid, high));
            double spread = Math.Clamp(max - min, 0.0, 1.0);
            double contrast = Math.Clamp((spread - SpectralContrastStart) / SpectralContrastSpan, 0.0, 1.0);

            if (contrast <= 0.0001)
            {
                subBass = ExpandDynamicRange(subBass);
                bass = ExpandDynamicRange(bass);
                lowMid = ExpandDynamicRange(lowMid);
                mid = ExpandDynamicRange(mid);
                highMid = ExpandDynamicRange(highMid);
                high = ExpandDynamicRange(high);
                kick = ExpandDynamicRange(kick);
                snare = ExpandDynamicRange(snare);
                rms = ExpandDynamicRange(rms);
                return;
            }

            double avg = (subBass + bass + lowMid + mid + highMid + high) / 6.0;

            subBass = EnhanceBand(subBass, avg, contrast);
            bass = EnhanceBand(bass, avg, contrast);
            lowMid = EnhanceBand(lowMid, avg, contrast);
            mid = EnhanceBand(mid, avg, contrast);
            highMid = EnhanceBand(highMid, avg, contrast);
            high = EnhanceBand(high, avg, contrast);

            double bassDelta = (subBass + bass) / 2.0 - avg;
            double highDelta = (highMid + high) / 2.0 - avg;

            kick = Math.Clamp(kick * (1.0 + (bassDelta * 0.70 * contrast)), 0.0, 1.0);
            snare = Math.Clamp(snare * (1.0 + (highDelta * 0.70 * contrast)), 0.0, 1.0);

            subBass = ExpandDynamicRange(subBass);
            bass = ExpandDynamicRange(bass);
            lowMid = ExpandDynamicRange(lowMid);
            mid = ExpandDynamicRange(mid);
            highMid = ExpandDynamicRange(highMid);
            high = ExpandDynamicRange(high);
            kick = ExpandDynamicRange(kick);
            snare = ExpandDynamicRange(snare);
            rms = ExpandDynamicRange(rms);
        }
#pragma warning restore S107

        private static double EnhanceBand(double band, double avg, double contrast)
        {
            double delta = band - avg;
            double enhanced = band + (delta * (SpectralContrastStrength * contrast));
            if (delta >= 0)
            {
                enhanced *= 1.0 + (SpectralDominantBoost * contrast);
            }
            else
            {
                enhanced *= 1.0 - (SpectralSubtractiveCut * contrast);
            }
            return Math.Clamp(enhanced, 0.0, 1.0);
        }

        private static double ExpandDynamicRange(double value)
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (clamped <= 0) return 0;
            double expanded = Math.Pow(clamped, DynamicRangeExpansionPower);
            return (expanded * (1.0 - DynamicRangeExpansionBlend)) + (clamped * DynamicRangeExpansionBlend);
        }

        private static void ApplyInstrumentRoleResponse(float[] targets, double rms)
        {
            double[] roleTargets = { 0.72, 0.66, 0.62, 0.56, 0.50 };
            double[] maxGains = { 1.45, 1.55, 1.35, 1.85, 2.05 };
            double[] fallback = { 0.025, 0.035, 0.070, 0.030, 0.022 };
            double[] caps = { 0.86, 0.82, 0.76, 0.70, 0.64 };

            double energy = Math.Clamp(rms, 0.0, 1.0);

            for (int i = 0; i < BarCount; i++)
            {
                double raw = Math.Clamp(targets[i], 0.0, 1.0);
                _rolePeaks[i] = Math.Max(raw, _rolePeaks[i] * RolePeakRelease);

                double gain = Math.Clamp(roleTargets[i] / Math.Max(RolePeakFloor, _rolePeaks[i]), 0.70, maxGains[i]);
                double adapted = raw * gain;

                adapted += fallback[i] * energy * (1.0 - Math.Clamp(raw * 2.0, 0.0, 1.0));

                targets[i] = (float)Math.Clamp(CompressRoleUpperRange(adapted, caps[i]), 0.0, caps[i]);
            }
        }

        private static double CompressRoleUpperRange(double value, double cap)
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            double knee = cap * 0.72;
            if (clamped <= knee) return clamped;

            double over = (clamped - knee) / Math.Max(0.001, 1.0 - knee);
            double compressed = knee + ((cap - knee) * (1.0 - Math.Exp(-over * 1.65)));
            return Math.Min(cap, compressed);
        }

        private static void NormalizeDisplayTargetsToMaxVisual(float[] targets)
        {
            double peak = 0.0;
            double sum = 0.0;

            for (int i = 0; i < BarCount; i++)
            {
                double value = Math.Clamp(targets[i], 0.0, 1.0);
                peak = Math.Max(peak, value);
                sum += value;
            }

            if (peak <= 0.0001) return;

            double desiredGain = VisualMaxPeakTarget / Math.Max(VisualMaxFloor, peak);
            double gain = Math.Clamp(desiredGain, 0.70, VisualMaxGainLimit);
            double energy = Math.Clamp(sum / BarCount * gain, 0.0, 1.0);
            double bandFloor = VisualMaxBandFloor * energy;
            double[] caps = { 0.86, 0.82, 0.76, 0.70, 0.64 };

            for (int i = 0; i < BarCount; i++)
            {
                double boosted = CompressUpperRange(Math.Clamp(targets[i] * gain, 0.0, 1.0));
                double lifted = boosted < bandFloor
                    ? boosted + ((bandFloor - boosted) * VisualMaxBandLift)
                    : boosted;

                targets[i] = (float)Math.Clamp(lifted, 0.0, caps[i]);
            }
        }

        private static double CompressUpperRange(double value)
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            const double knee = 0.62;
            if (clamped <= knee) return clamped;

            double over = (clamped - knee) / (1.0 - knee);
            double compressed = knee + ((1.0 - knee) * (1.0 - Math.Exp(-over * 1.35)) / (1.0 - Math.Exp(-1.35)));
            return Math.Min(0.86, compressed);
        }

        private static void ApplyBarContrast(float[] targets)
        {
            double max = 0.0;
            double min = 1.0;
            double sum = 0.0;

            for (int i = 0; i < BarCount; i++)
            {
                double v = Math.Clamp(targets[i], 0.0, 1.0);
                if (v > max) max = v;
                if (v < min) min = v;
                sum += v;
            }

            double spread = Math.Clamp(max - min, 0.0, 1.0);
            double contrast = Math.Clamp((spread - BarContrastStart) / BarContrastSpan, 0.0, 1.0);
            if (contrast <= 0.0001) return;

            double avg = sum / BarCount;

            for (int i = 0; i < BarCount; i++)
            {
                double v = Math.Clamp(targets[i], 0.0, 1.0);
                double delta = v - avg;
                double enhanced = v + (delta * (BarContrastStrength * contrast));
                if (delta >= 0)
                {
                    enhanced *= 1.0 + (BarContrastBoost * contrast);
                }
                else
                {
                    enhanced *= 1.0 - (BarContrastCut * contrast);
                }
                targets[i] = (float)Math.Clamp(enhanced, 0.0, 1.0);
            }
        }

        private static float ReadSampleAsFloat(byte[] buffer, int offset, WaveFormat waveFormat)
        {
            bool isFloatLike = waveFormat.Encoding == WaveFormatEncoding.IeeeFloat;

            if (waveFormat.Encoding == WaveFormatEncoding.Extensible &&
                waveFormat is WaveFormatExtensible extensible)
            {
                isFloatLike = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
            }

            if (isFloatLike)
            {
                if (waveFormat.BitsPerSample == 64)
                {
                    return (float)BitConverter.ToDouble(buffer, offset);
                }
                return BitConverter.ToSingle(buffer, offset);
            }

            return waveFormat.BitsPerSample switch
            {
                8 => (buffer[offset] - 128) / 128f,
                16 => BitConverter.ToInt16(buffer, offset) / 32768f,
                24 => Read24BitSample(buffer, offset) / 8388608f,
                32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
                _ => 0f
            };
        }

        private static int Read24BitSample(byte[] buffer, int offset)
        {
            int sample = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
            return sample;
        }

        private float[] GetLatestDisplayLevels(out bool hasFreshAudio, out double beatAccent)
        {
            lock (_outputLock)
            {
                long ticks = _publishedFrameTicks;
                hasFreshAudio = ticks != 0 &&
                    (DateTime.UtcNow.Ticks - ticks) <= TimeSpan.FromMilliseconds(FreshAudioTimeoutMs).Ticks;
                beatAccent = _publishedBeatAccent;

                Array.Copy(_publishedTargets, _levelsBuffer, BarCount);
                return _levelsBuffer;
            }
        }

        #endregion
    }
}
