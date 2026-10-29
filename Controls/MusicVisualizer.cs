using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

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
        private const double BarWidthRatio = 0.10;
        private const double BarSpacingRatio = 0.05;
        private const double CornerRadiusRatio = 0.5;
        private const double CaptureRetryIntervalMs = 2500;
        private const double MinHeightChangeThreshold = 0.0012;

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
        private const double PlaybackFeedbackSeconds = 1.5;
        private double _feedbackUntil;
        private double _iconMix;
        private double _iconVelocity;
        private double _playMix;
        private double _playVelocity;
        private bool _copiedFeedback;
        private double _checkMix;
        private double _checkVelocity;

        public void SetCopiedFeedback(bool copied)
        {
            _copiedFeedback = copied;
            _feedbackUntil = 0;
            if (IsLoaded && IsVisible) StartRendering();
            else UpdateRenderingState();
            InvalidateVisual();
        }

        // Native filled-vector morph inspired by https://www.morphicons.com/.
        // Five quadrilaterals connect the visualizer bars to solid icon silhouettes.
        public MusicVisualizer()
        {
            Loaded += (s, e) => UpdateRenderingState();
            Unloaded += (s, e) =>
            {
                ResetPlaybackFeedback();
                StopRendering();
                ReleaseCaptureLease();
            };
            IsVisibleChanged += (s, e) => UpdateRenderingState();

            for (int i = 0; i < BarCount; i++)
            {
                _currentHeights[i] = MinHeightRatio;
                _smoothedHeights[i] = MinHeightRatio;
                _drawHeights[i] = MinHeightRatio;
            }
        }

        private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MusicVisualizer viz)
            {
                if (e.Property == TrackIdProperty && !viz._copiedFeedback)
                    viz.ResetPlaybackFeedback();
                else if (e.Property == IsPlayingProperty && viz.IsLoaded && viz.IsVisible &&
                         !string.IsNullOrEmpty(viz.TrackId) && !viz._copiedFeedback)
                {
                    viz._feedbackUntil = viz._stopwatch.Elapsed.TotalSeconds + PlaybackFeedbackSeconds;
                    viz.StartRendering();
                }
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
            IsLoaded && IsVisible && (_state == VisualizerState.Playing || _state == VisualizerState.Seeking);

        private void UpdateRenderingState()
        {
            if (!IsLoaded || !IsVisible || _state == VisualizerState.Idle)
            {
                _checkMix = 0;
                _iconMix = 0;
                ResetPlaybackFeedback();
                StopRendering();
                ReleaseCaptureLease();
                InvalidateVisual();
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
            // A delayed UI frame must not advance the morph by a large jump.
            dt = Math.Min(dt, 0.05);

            _lastDtMs = dt * 1000.0;
            double oldOpacity = _currentOpacity;
            bool isSettled = UpdateAnimation(dt, totalSec);
            double oldIconMix = _iconMix, oldCheckMix = _checkMix, oldPlayMix = _playMix;
            bool feedbackActive = UpdatePlaybackFeedback(dt, totalSec);
            bool feedbackChanged =
                Math.Abs(oldIconMix - _iconMix) > 0.0001 ||
                Math.Abs(oldCheckMix - _checkMix) > 0.0001 ||
                Math.Abs(oldPlayMix - _playMix) > 0.0001;
            // Advance smoothing once per animation tick, never once per paint.
            PrepareDrawHeights();

            bool drawSettled = true;
            for (int i = 0; i < BarCount; i++)
                drawSettled &= Math.Abs(_smoothedHeights[i] - _currentHeights[i]) <= MinHeightChangeThreshold;
            bool opacityChanged = Math.Abs(oldOpacity - _currentOpacity) > 0.0001;
            bool copiedFeedbackSettled = _copiedFeedback && Math.Abs(_iconMix - 1) < 1e-6 && Math.Abs(_checkMix - 1) < 1e-6 &&
                Math.Abs(_playMix - (IsPlaying ? 1 : 0)) < 1e-6;

            if (copiedFeedbackSettled ||
                (isSettled && drawSettled && !opacityChanged && !feedbackActive && _state == VisualizerState.Paused))
            {
                if (feedbackChanged || !copiedFeedbackSettled)
                    RefreshDrawingFrame();
                if (copiedFeedbackSettled) StopRendering();
                if (_state != VisualizerState.Playing)
                {
                    StopRendering();
                    ReleaseCaptureLease();
                }
                return;
            }

            if ((!isSettled || !drawSettled || opacityChanged || feedbackChanged) && (feedbackChanged || NeedsDrawingUpdate()))
            {
                RefreshDrawingFrame();
            }
        }

        private void ResetPlaybackFeedback()
        {
            _feedbackUntil = 0;
            _copiedFeedback = false;
            _checkMix = _checkVelocity = 0;
            _iconMix = _iconVelocity = _playVelocity = 0;
            _playMix = IsPlaying ? 1 : 0;
            InvalidateVisual();
        }

        private bool UpdatePlaybackFeedback(double dt, double now)
        {
            double target = _copiedFeedback || now < _feedbackUntil ? 1 : 0;
            // Keep the outgoing check shape while it returns to the bars;
            // do not pass through the play/pause silhouette on the way out.
            double inactiveCheckTarget = target > 0 ? 0 : _checkMix;
            double checkTarget = _copiedFeedback ? 1 : inactiveCheckTarget;
            StepSpring(ref _checkMix, ref _checkVelocity, checkTarget, dt);
            StepSpring(ref _iconMix, ref _iconVelocity, target, dt);
            StepSpring(ref _playMix, ref _playVelocity, IsPlaying ? 1 : 0, dt);
            return target > 0 || _iconMix > 0;
        }

        private static void StepSpring(ref double value, ref double velocity, double target, double dt) =>
            VNotch.Services.VisualizerDsp.StepSpring(ref value, ref velocity, target, dt);

        private void UpdatePlaybackMorph(double width, double height,
            double startX, double barWidth, double spacing)
        {
            double size = Math.Min(width, height);
            // Rasterize all slices together so shared edges do not get
            // antialiased separately and leave translucent seams.
            using (var path = _morphGeometry!.Open())
            {
                for (int i = 0; i < BarCount; i++)
                {
                    Rect barBounds = GetBarBounds(i, height, startX, barWidth, spacing);
                    Point Corner(int corner)
                    {
                        bool right = corner == 1 || corner == 2;
                        bool bottom = corner >= 2;
                        // Adjacent slices form one solid triangle with no outline.
                        double playX = 7 + 13.0 * (i + (right ? 1 : 0)) / BarCount;
                        double playHalfHeight = 8 * (20 - playX) / 13;
                        var play = new Point(playX, 12 + (bottom ? playHalfHeight : -playHalfHeight));
                        // Three slices form the left pause bar, two form the right.
                        int slice = i < 3 ? i : i - 3;
                        int slices = i < 3 ? 3 : 2;
                        double pauseX = (i < 3 ? 6 : 14) + 4.0 * (slice + (right ? 1 : 0)) / slices;
                        var pause = new Point(pauseX, bottom ? 20 : 4);
                        Point icon = pause + (play - pause) * _playMix;
                        // Five adjoining filled slices form a continuous check mark.
                        bool leftArm = i < 2;
                        int checkSlice = leftArm ? i : i - 2;
                        int rightOffset = right ? 1 : 0;
                        double checkX = leftArm
                            ? 3 + 6.0 * (checkSlice + rightOffset) / 2
                            : 9 + 12.0 * (checkSlice + rightOffset) / 3;
                        double checkY = leftArm ? checkX + 9 : 27 - checkX;
                        var check = new Point(checkX, checkY + (bottom ? 1.7 : -1.7));
                        icon += (check - icon) * _checkMix;
                        icon = new Point((width - size) / 2 + icon.X * size / 24,
                            (height - size) / 2 + icon.Y * size / 24);
                        var bar = new Point(right ? barBounds.Right : barBounds.Left,
                            bottom ? barBounds.Bottom : barBounds.Top);
                        return bar + (icon - bar) * _iconMix;
                    }
                    path.BeginFigure(Corner(0), isFilled: true, isClosed: true);
                    path.LineTo(Corner(1), isStroked: false, isSmoothJoin: false);
                    path.LineTo(Corner(2), isStroked: false, isSmoothJoin: false);
                    path.LineTo(Corner(3), isStroked: false, isSmoothJoin: false);
                }
            }
        }

        private bool UpdateAnimation(double dt, double totalSec)
        {
            string sid = TrackId ?? string.Empty;
            EnsureHashCache(sid);

            float[] levels = GetLatestDisplayLevels(out bool hasFreshAudio, out double beatAccent);
            return VNotch.Services.VisualizerDsp.UpdateAnimation(
                (int)_state,
                dt,
                totalSec,
                hasFreshAudio,
                beatAccent,
                levels,
                _noAudioHash,
                _floorHash,
                ref _currentOpacity,
                _currentHeights);
        }

        private void EnsureHashCache(string sid)
        {
            if (_hashCacheSid == sid) return;
            _hashCacheSid = sid;
            VNotch.Services.VisualizerDsp.ComputeHashes(sid, _noAudioHash, _floorHash);
        }

        private void EnsureAudioCaptureStarted(bool force = false)
        {
            if (!ShouldCaptureAudio) return;
            bool newLease = !_holdsCaptureLease;
            AcquireCaptureLease();

            var now = DateTime.UtcNow;
            if (!force && !newLease && (now - _lastCaptureRetryUtc).TotalMilliseconds < CaptureRetryIntervalMs) return;

            _lastCaptureRetryUtc = now;
            RequestCaptureUpdate();
        }

        // UI/render callbacks only publish intent. Device enumeration, driver
        // startup and StopRecording can block, so serialize them off-dispatcher.
        private static readonly object _captureRequestLock = new();
        private static bool _captureWorkerRunning;
        private static bool _captureUpdateRequested;
        private static bool _captureRestartRequested;

        private static void RequestCaptureUpdate(bool restart = false)
        {
            lock (_captureRequestLock)
            {
                _captureUpdateRequested = true;
                _captureRestartRequested |= restart;
                if (_captureWorkerRunning) return;
                _captureWorkerRunning = true;
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ => ProcessCaptureUpdates());
        }

        private static void ProcessCaptureUpdates()
        {
            while (true)
            {
                bool restart;
                lock (_captureRequestLock)
                {
                    if (!_captureUpdateRequested)
                    {
                        _captureWorkerRunning = false;
                        return;
                    }
                    _captureUpdateRequested = false;
                    restart = _captureRestartRequested;
                    _captureRestartRequested = false;
                }
                try
                {
                    if (System.Threading.Volatile.Read(ref _captureLeaseCount) == 0)
                    {
                        StopAudioCapture();
                        continue;
                    }
                    if (restart || AudioCaptureNeedsRestart(DateTime.UtcNow)) StopAudioCapture();
                    StartAudioCapture();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Visualizer capture update failed: " + ex.Message);
                }
            }
        }

        private static bool AudioCaptureNeedsRestart(DateTime now)
        {
            string requestedDeviceId;
            lock (_lockObj)
            {
                if (_capture == null) return false;
                requestedDeviceId = _audioDeviceId;
            }

            try
            {
                string? expectedId = VNotch.Services.NativeLoopbackCapture.ResolveDeviceId(requestedDeviceId);
                lock (_lockObj)
                {
                    if (_capture == null) return false;
                    if (expectedId == null || expectedId != _capture.DeviceId ||
                        !_capture.IsDeviceActive()) return true;
                    long lastFrame;
                    lock (_outputLock) lastFrame = _publishedFrameTicks;
                    // A stopped endpoint does not always raise a stop notification.
                    // Allow silence, but periodically recover a stream delivering no packets.
                    var lastActivity = lastFrame > 0 ? new DateTime(lastFrame, DateTimeKind.Utc) : _captureStartedUtc;
                    return (now - lastActivity).TotalSeconds >= 8;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Visualizer endpoint unavailable: " + ex.Message);
                return true;
            }
        }

#pragma warning disable S2696 // Multi-instance visualizers coordinate a single shared loopback audio capture lease
        private void AcquireCaptureLease()
        {
            // This instance flag is owned by the WPF dispatcher. The audio
            // callback only touches shared capture state. Do not wait behind
            // its FFT work on every render tick when we already hold a lease.
            if (_holdsCaptureLease) return;
            _holdsCaptureLease = true;
            System.Threading.Interlocked.Increment(ref _captureLeaseCount);
        }

        private void ReleaseCaptureLease()
        {
            if (!_holdsCaptureLease) return;
            _holdsCaptureLease = false;
            if (System.Threading.Interlocked.Decrement(ref _captureLeaseCount) == 0)
                RequestCaptureUpdate();
        }
#pragma warning restore S2696

        private void PrepareDrawHeights() =>
            VNotch.Services.VisualizerDsp.PrepareDrawHeights(_lastDtMs, _currentHeights, _smoothedHeights, _sortedHeights, _drawHeights);

        private Color _cachedGradientBaseColor;
        private LinearGradientBrush? _cachedBarGradient;
        private LinearGradientBrush? _playbackMorphBrush;

        private LinearGradientBrush GetPlaybackMorphBrush()
        {
            var original = GetBarGradientBrush();
            _playbackMorphBrush ??= original.Clone();
            float mix = (float)Math.Clamp(_iconMix, 0, 1);
            for (int i = 0; i < original.GradientStops.Count; i++)
            {
                Color color = original.GradientStops[i].Color;
                // Blend in linear light, following the same spring as the geometry.
                // At rest this exactly matches each visualizer bar's original gradient.
                _playbackMorphBrush.GradientStops[i].Color = Color.FromScRgb(
                    color.ScA + (1 - color.ScA) * mix,
                    color.ScR + (1 - color.ScR) * mix,
                    color.ScG + (1 - color.ScG) * mix,
                    color.ScB + (1 - color.ScB) * mix);
            }
            return _playbackMorphBrush;
        }

        private LinearGradientBrush GetBarGradientBrush()
        {
            Color baseColor = ActiveBrush is SolidColorBrush scb ? scb.Color : Colors.White;

            if (_cachedBarGradient == null || baseColor != _cachedGradientBaseColor)
            {
                _cachedGradientBaseColor = baseColor;

                byte dr = (byte)(baseColor.R * 0.55);
                byte dg = (byte)(baseColor.G * 0.55);
                byte db = (byte)(baseColor.B * 0.55);
                var darkColor = Color.FromArgb(baseColor.A, dr, dg, db);

                _cachedBarGradient = new LinearGradientBrush(baseColor, darkColor, 90.0)
                {
                    MappingMode = BrushMappingMode.RelativeToBoundingBox
                };
                _cachedBarGradient.Freeze();
            }

            return _cachedBarGradient;
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            _cachedDpi = newDpi;
        }

        protected override void OnRender(DrawingContext drawingContext) => RenderFrame(drawingContext);

        private readonly Rect[] _renderedBarBounds = new Rect[BarCount];
        private bool _hasRenderedBars;
        private double _renderedWidth, _renderedHeight, _renderedOpacity;
        private DpiScale _renderedDpi;
        private Color _renderedBaseColor;
        private DrawingGroup? _frameDrawing;
        private DrawingGroup? _barsDrawing;
        private DrawingGroup? _morphDrawing;
        private StreamGeometry? _morphGeometry;
        private readonly RectangleGeometry[] _barGeometries = new RectangleGeometry[BarCount];
        private readonly Dictionary<Rect, object> _boxedBarBounds = new();
        private (double Width, double Height, double DpiX, double DpiY) _barBoundsLayout;
        private Brush? _retainedBarBrush, _retainedMorphBrush;

        private void EnsureFrameDrawing()
        {
            if (_frameDrawing != null) return;
            _frameDrawing = new DrawingGroup();
            _barsDrawing = new DrawingGroup();
            _morphDrawing = new DrawingGroup();
            _morphGeometry = new StreamGeometry { FillRule = FillRule.Nonzero };
            for (int i = 0; i < BarCount; i++)
            {
                _barGeometries[i] = new RectangleGeometry();
                _barsDrawing.Children.Add(new GeometryDrawing(null, null, _barGeometries[i]));
            }
            _morphDrawing.Children.Add(new GeometryDrawing(null, null, _morphGeometry));
            _frameDrawing.Children.Add(_morphDrawing);
            _frameDrawing.Children.Add(_barsDrawing);
        }

        private void RefreshDrawingFrame()
        {
            if (_frameDrawing == null) InvalidateVisual();
            else UpdateDrawingFrame();
        }

        // The bars are snapped to device pixels. Their smoothed amplitudes can
        // advance several frames before a drawn edge moves by even one pixel.
        // Keep the current drawing until its exact bounds, opacity or color differ.
        internal bool NeedsDrawingUpdate()
        {
            if (!_hasRenderedBars || Math.Abs(_iconMix) > 1e-6 || Math.Abs(_renderedOpacity - _currentOpacity) > 1e-6 ||
                Math.Abs(_renderedWidth - ActualWidth) > 1e-6 || Math.Abs(_renderedHeight - ActualHeight) > 1e-6 || ActiveBrush == null)
                return true;
            DpiScale dpi = _cachedDpi ??= VisualTreeHelper.GetDpi(this);
            if (Math.Abs(dpi.DpiScaleX - _renderedDpi.DpiScaleX) > 1e-6 || Math.Abs(dpi.DpiScaleY - _renderedDpi.DpiScaleY) > 1e-6 ||
                (ActiveBrush is SolidColorBrush brush ? brush.Color : Colors.White) != _renderedBaseColor)
                return true;
            double barWidth = ActualWidth * BarWidthRatio;
            double spacing = ActualWidth * BarSpacingRatio + 0.2;
            double startX = (ActualWidth - (barWidth * BarCount + spacing * (BarCount - 1))) / 2;
            for (int i = 0; i < BarCount; i++)
                if (_renderedBarBounds[i] != GetBarBounds(i, ActualHeight, startX, barWidth, spacing)) return true;
            return false;
        }

        internal void RenderFrame(DrawingContext drawingContext)
        {
            UpdateDrawingFrame();
            drawingContext.DrawDrawing(_frameDrawing);
        }

        internal bool UpdateDrawingFrame()
        {
            EnsureFrameDrawing();
            _hasRenderedBars = false;
            double width = ActualWidth;
            double height = ActualHeight;

            if (width < 1 || height < 1 || ActiveBrush == null)
            {
                _frameDrawing!.Opacity = 0;
                return false;
            }

            DpiScale dpi = _cachedDpi ??= VisualTreeHelper.GetDpi(this);

            double barWidth = width * BarWidthRatio;
            double spacing = (width * BarSpacingRatio) + 0.2;
            double totalContentWidth = (barWidth * BarCount) + (spacing * (BarCount - 1));

            double startX = (width - totalContentWidth) / 2;

            double snappedW = Math.Max(1.0, Math.Round(barWidth * dpi.DpiScaleX) / dpi.DpiScaleX);

            _frameDrawing!.Opacity = _currentOpacity + (1 - _currentOpacity) * _iconMix;

            // Blend the final short section into rounded bars. Switching from
            // sharp polygons to rounded, per-bar gradients in one frame pops.
            double morphOpacity = Math.Clamp(_iconMix / 0.18, 0, 1);
            morphOpacity = morphOpacity * morphOpacity * (3 - 2 * morphOpacity);
            _morphDrawing!.Opacity = morphOpacity;
            _barsDrawing!.Opacity = 1 - morphOpacity;
            if (morphOpacity > 0)
            {
                var morphBrush = GetPlaybackMorphBrush();
                if (!ReferenceEquals(_retainedMorphBrush, morphBrush))
                    ((GeometryDrawing)_morphDrawing.Children[0]).Brush = _retainedMorphBrush = morphBrush;
                UpdatePlaybackMorph(width, height, startX, barWidth, spacing);
            }
            if (morphOpacity >= 1)
                return true;
            var gradientBrush = GetBarGradientBrush();
            bool brushChanged = !ReferenceEquals(_retainedBarBrush, gradientBrush);
            _retainedBarBrush = gradientBrush;
            var layout = (width, height, dpi.DpiScaleX, dpi.DpiScaleY);
            if (_barBoundsLayout != layout)
            {
                _barBoundsLayout = layout;
                _boxedBarBounds.Clear();
            }

            double radius = snappedW * CornerRadiusRatio;
            for (int i = 0; i < BarCount; i++)
            {
                Rect bounds = GetBarBounds(i, height, startX, barWidth, spacing);
                _renderedBarBounds[i] = bounds;
                var geometry = _barGeometries[i];
                if (geometry.Rect != bounds)
                {
                    if (!_boxedBarBounds.TryGetValue(bounds, out var boxedBounds))
                    {
                        boxedBounds = bounds;
                        _boxedBarBounds.Add(bounds, boxedBounds);
                    }
                    geometry.SetValue(RectangleGeometry.RectProperty, boxedBounds);
                }
                if (Math.Abs(geometry.RadiusX - radius) > 1e-6) geometry.RadiusX = radius;
                if (Math.Abs(geometry.RadiusY - radius) > 1e-6) geometry.RadiusY = radius;
                if (brushChanged) ((GeometryDrawing)_barsDrawing.Children[i]).Brush = gradientBrush;
            }
            _hasRenderedBars = morphOpacity == 0;
            _renderedWidth = width;
            _renderedHeight = height;
            _renderedOpacity = _currentOpacity;
            _renderedDpi = dpi;
            _renderedBaseColor = _cachedGradientBaseColor;
            return true;
        }

        private Rect GetBarBounds(int index, double height, double startX, double barWidth, double spacing)
        {
            DpiScale dpi = _cachedDpi ??= VisualTreeHelper.GetDpi(this);
            double x = startX + index * (barWidth + spacing);
            double halfHeight = _drawHeights[index] * height / 2;
            double top = Math.Round((height / 2 - halfHeight) * dpi.DpiScaleY) / dpi.DpiScaleY;
            double bottom = Math.Round((height / 2 + halfHeight) * dpi.DpiScaleY) / dpi.DpiScaleY;
            return new Rect(Math.Round(x * dpi.DpiScaleX) / dpi.DpiScaleX, top,
                Math.Max(1, Math.Round(barWidth * dpi.DpiScaleX) / dpi.DpiScaleX), Math.Max(0, bottom - top));
        }

        #region Audio Loopback Capture

        private static VNotch.Services.NativeLoopbackCapture? _capture;
        private static DateTime _captureStartedUtc;
        private static readonly object _lockObj = new object();
        private static int _captureLeaseCount;
        private static string _audioDeviceId = string.Empty;
        private const int FftLength = 512;
        private const double RmsWindowSeconds = 0.020;
        private const int FreshAudioTimeoutMs = 800;

        private static readonly float[] _fftInputBuffer = new float[FftLength];
        private static int _fftInputPos = 0;
        private static readonly VNotch.Services.SpectrumComplex[] _fftData = new VNotch.Services.SpectrumComplex[FftLength];
        private static readonly VNotch.Services.VisualizerSpectrumBands _spectrumBands = new(FftLength);

        private static readonly float[] _displayTargets = new float[BarCount];
        private static VNotch.Services.VisualizerDspState _dspState;
        private static double _rmsSumSquares;
        private static int _rmsSampleCount;
        private static int _rmsWindowSamples = 882;
        private static float _latestRmsNormalized;
        private static int _sampleRate = 44100;

        private static readonly object _outputLock = new object();
        private static readonly float[] _publishedTargets = new float[BarCount];
        private static float _publishedBeatAccent;
        private static long _publishedFrameTicks;

        static MusicVisualizer()
        {
            VNotch.Services.VisualizerDsp.ResetState(ref _dspState);
        }

        public static void ConfigureAudioDevice(string? deviceId)
        {
            deviceId ??= string.Empty;
            string previous = System.Threading.Interlocked.Exchange(ref _audioDeviceId, deviceId);
            if (!string.Equals(previous, deviceId, StringComparison.Ordinal))
                RequestCaptureUpdate(restart: true);
        }

        private static void StartAudioCapture()
        {
            lock (_lockObj)
            {
                if (_capture != null || System.Threading.Volatile.Read(ref _captureLeaseCount) == 0) return;

                try
                {
                    ResetAudioState();
                    var receiver = new NativeCaptureReceiver();
                    var capture = VNotch.Services.NativeLoopbackCapture.Start(
                        _audioDeviceId, receiver.OnData, receiver.OnStopped, out int hresult);
                    if (capture == null)
                    {
                        Debug.WriteLine($"Failed to initialize audio capture: 0x{hresult:X8}");
                        return;
                    }

                    receiver.Capture = capture;
                    _capture = capture;
                    _captureStartedUtc = DateTime.UtcNow;
                    _sampleRate = capture.SampleRate;
                    _rmsWindowSamples = Math.Max(64, (int)(_sampleRate * RmsWindowSeconds));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Failed to initialize audio capture: " + ex.Message);
                    _capture?.Dispose();
                    _capture = null;
                }
            }
        }

        // Routes native capture callbacks to the static analysis, ignoring a capture that was already replaced.
        private sealed partial class NativeCaptureReceiver
        {
            internal VNotch.Services.NativeLoopbackCapture? Capture;

            internal void OnData(byte[] buffer, int bytes)
            {
                var capture = _capture;
                if (capture == null || !ReferenceEquals(capture, Capture)) return;
                ProcessAudioBuffer(buffer, bytes, capture.Channels, capture.BitsPerSample, capture.IsFloat);
            }

            internal void OnStopped() => OnCaptureStopped(Capture);
        }

        private static void StopAudioCapture()
        {
            VNotch.Services.NativeLoopbackCapture? captureToDispose;
            lock (_lockObj)
            {
                if (_capture == null) return;
                captureToDispose = _capture;
                _capture = null;
                ResetAudioState();
            }

            try
            {
                captureToDispose.Dispose();
            }
            catch (Exception ex)
            {
                VNotch.Services.RuntimeLog.Error("MUSIC-VIS-STOP", ex.ToString());
            }
        }

        private sealed partial class NativeCaptureReceiver
        {
            private static void OnCaptureStopped(VNotch.Services.NativeLoopbackCapture? sender)
            {
                VNotch.Services.NativeLoopbackCapture? stopped = null;
                lock (_lockObj)
                {
                    if (sender != null && ReferenceEquals(_capture, sender))
                    {
                        stopped = _capture;
                        _capture = null;
                        ResetAudioState();
                    }
                }
                // Release the native capture after its thread leaves the callback, outside the shared lock.
                if (stopped != null)
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try { stopped.Dispose(); }
                        catch (Exception ex) { Debug.WriteLine(ex.Message); }
                    });
            }

        }

        private static void ResetAudioState()
        {
            Array.Clear(_fftInputBuffer, 0, _fftInputBuffer.Length);
            Array.Clear(_displayTargets, 0, _displayTargets.Length);
            VNotch.Services.VisualizerDsp.ResetState(ref _dspState);
            _fftInputPos = 0;
            _rmsSumSquares = 0;
            _rmsSampleCount = 0;
            _latestRmsNormalized = 0;

            lock (_outputLock)
            {
                Array.Clear(_publishedTargets, 0, _publishedTargets.Length);
                _publishedBeatAccent = 0;
                _publishedFrameTicks = 0;
            }
        }

        internal static void ProcessAudioBuffer(byte[] buffer, int bytesRecorded, int channelCount, int bitsPerSample, bool isFloatLike)
        {
            lock (_lockObj)
            {
                int bytesPerSample = bitsPerSample / 8;
                if (bytesPerSample <= 0) bytesPerSample = isFloatLike ? 4 : 2;
                int channels = Math.Max(1, channelCount);
                int bytesPerFrame = bytesPerSample * channels;
                if (bytesPerFrame <= 0) return;

                int framesRecorded = Math.Clamp(bytesRecorded, 0, buffer.Length) / bytesPerFrame;
                if (framesRecorded <= 0) return;

                float[]? rented = framesRecorded > 1024 ? ArrayPool<float>.Shared.Rent(framesRecorded) : null;
                Span<float> monoSpan = framesRecorded <= 1024
                    ? stackalloc float[framesRecorded]
                    : rented!.AsSpan(0, framesRecorded);

                try
                {
                    int written = VNotch.Services.VisualizerDsp.MixFramesToMono(
                        buffer, bytesRecorded, channelCount, bitsPerSample, isFloatLike, monoSpan);
                    for (int frame = 0; frame < written; frame++)
                    {
                        PushSample(monoSpan[frame]);
                    }
                }
                finally
                {
                    if (rented != null)
                        ArrayPool<float>.Shared.Return(rented);
                }

                lock (_outputLock)
                {
                    _publishedFrameTicks = DateTime.UtcNow.Ticks;
                }
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
                _latestRmsNormalized = VNotch.Services.VisualizerDsp.NormalizeAmplitude(rms);
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
            if (!VNotch.Services.VisualizerDsp.WindowedFft(_fftInputBuffer, _fftData)) return;

            var energies = _spectrumBands.Compute(_fftData, _sampleRate);
            VNotch.Services.VisualizerDsp.ComputeDisplayTargets(
                energies, _latestRmsNormalized, ref _dspState, _displayTargets);

            lock (_outputLock)
            {
                Array.Copy(_displayTargets, _publishedTargets, BarCount);
                _publishedBeatAccent = (float)_dspState.BeatAccent;
            }
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
