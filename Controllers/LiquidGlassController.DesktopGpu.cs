namespace VNotch.Controllers;

public sealed partial class LiquidGlassController
{
    private D3DImageFramePresenter.GpuFrame _gpuLens;
    private D3DImageFramePresenter.GpuFrame _lastSubmittedGpuFrame;
    private bool _hasGpuLens;
    private bool _hasLastSubmittedGpuFrame;
    private bool _lastGpuWasExactWindows;
    private double _gpuLensScreenX, _gpuLensScreenY;
    private LiquidGlassRefractionEffect? _gpuLensEffect;
    private bool _gpuLensUpdateQueued;
    internal bool UsesNativeRefraction { get; private set; }

    /// <summary>
    /// Max edge bleed (physical px) drawn around the lens while animating. The host
    /// must offset the presented frame by the bleed stored in PresentedGpuFrame.
    /// </summary>
    internal int GpuEdgeBleedPx { get; set; }

    private System.Windows.Media.MatrixTransform? _presentedFrameAlign;

    // Native refraction bakes the lens screen position into each frame at render
    // time, but WPF shows that frame 1-2 UI frames later. While the host resizes,
    // that variable lag made the backdrop jitter sideways. Shift the presented
    // frame by the drift (minus its edge bleed) so the desktop stays screen-locked.
    // screenLeft/Top: live lens origin in physical px; liveWidth/Height: live lens
    // size in physical px; dipScale: physical px per local DIP of the host image
    // (DPI times any ancestor scale). UI thread only.
    internal void AlignPresentedFrame(double screenLeft, double screenTop, double liveWidth, double liveHeight,
        double dipScaleX, double dipScaleY)
    {
        if (!UsesNativeRefraction || PresentedGpuFrame is not { } frame ||
            !(dipScaleX > 0) || !(dipScaleY > 0) || !double.IsFinite(screenLeft) || !double.IsFinite(screenTop))
        {
            ResetPresentedFrameAlignment();
            return;
        }
        // Whole physical pixels keep the D3DImage unresampled (no subpixel blur).
        double driftX = Math.Round(frame.X + frame.Get(4) - screenLeft);
        double driftY = Math.Round(frame.Y + frame.Get(5) - screenTop);
        // A frame from before a reset or monitor move must never slide the glass away.
        if (!double.IsFinite(driftX) || !double.IsFinite(driftY) || Math.Abs(driftX) > 96 || Math.Abs(driftY) > 96)
            driftX = driftY = 0;
        // Frames rendered with edge bleed draw the lens `bleed` px in from the origin.
        double bleed = Math.Clamp((double)frame.GetY(2), 0, 128);
        double width = Math.Min(frame.Width, frame.Get(2) + 2 * bleed);
        double height = Math.Min(frame.Height, frame.Get(3) + 2 * bleed);
        if (width < 1 || height < 1) return;
        // A resting frame has transparent rounded corners. During a size jump,
        // stretch its opaque interior and let the live host clip shape the corners.
        double inset = 0;
        if (bleed == 0 && (liveWidth > width + 1 || liveHeight > height + 1))
            inset = Math.Clamp(Math.Max(frame.Get(6), frame.Get(7)) + 1, 0, (width - 1) * 0.5);
        var x = FitPresentedSpan(driftX - bleed, liveWidth, width, inset);
        var y = FitPresentedSpan(driftY - bleed, liveHeight, height, 0);
        var matrix = new System.Windows.Media.Matrix(x.Scale, 0, 0, y.Scale, x.Offset / dipScaleX, y.Offset / dipScaleY);
        if (matrix.IsIdentity && _presentedFrameAlign == null) return;
        _presentedFrameAlign ??= new System.Windows.Media.MatrixTransform();
        if (!ReferenceEquals(_host.RenderTransform, _presentedFrameAlign)) _host.RenderTransform = _presentedFrameAlign;
        if (_presentedFrameAlign.Matrix != matrix) _presentedFrameAlign.Matrix = matrix;
    }

    internal static (double Scale, double Offset) FitPresentedSpan(double offset, double live, double rendered, double inset)
    {
        if (!double.IsFinite(live) || live <= 0) return (1, offset);
        double usable = rendered - 2 * inset;
        if (live > usable)
        {
            double scale = live / usable;
            return (scale, -inset * scale);
        }
        return (1, Math.Clamp(offset, live - (rendered - inset), -inset));
    }

    internal void ResetPresentedFrameAlignment()
    {
        // Only undo our own transform; the CPU presenter owns its own one.
        if (_presentedFrameAlign == null) return;
        if (ReferenceEquals(_host.RenderTransform, _presentedFrameAlign)) _host.RenderTransform = null;
        _presentedFrameAlign.Matrix = System.Windows.Media.Matrix.Identity;
    }

    internal void EnableNativeRefraction() => UsesNativeRefraction = true;

    // Snapshot dependency properties on the UI thread; the capture worker never
    // touches a DispatcherObject. Screen coordinates survive capture-origin changes.
    internal bool UpdateGpuLens(LiquidGlassRefractionEffect fx, double screenX, double screenY)
    {
        if (!ReferenceEquals(_gpuLensEffect, fx))
        {
            ReleaseGpuLens();
            _gpuLensEffect = fx;
            fx.Changed += OnGpuLensChanged;
        }
        var frame = new D3DImageFramePresenter.GpuFrame();
        frame.Set(2, fx.NotchW); frame.Set(3, fx.NotchH);
        frame.Set(6, fx.BottomCornerR); frame.Set(7, fx.TopCornerR);
        frame.Set(22, fx.PointerX); frame.Set(23, fx.PointerY);
        frame.Set(24, fx.PointerActive); frame.Set(25, fx.PressAmount);
        frame.Set(26, fx.HighlightStrength); frame.Set(27, fx.FlexStrength);
        frame.Set(28, fx.LightX); frame.Set(29, fx.LightY);
        lock (_sync)
        {
            bool unchanged = _hasGpuLens &&
                Math.Abs(_gpuLensScreenX - screenX) < 1e-4 &&
                Math.Abs(_gpuLensScreenY - screenY) < 1e-4 &&
                _gpuLens.LensEquals(in frame);
            _gpuLens = frame;
            _gpuLensScreenX = screenX; _gpuLensScreenY = screenY;
            _hasGpuLens = true;
            return !unchanged;
        }
    }

    private void OnGpuLensChanged(object? sender, EventArgs e)
    {
        // Geometry changes several dependency properties in one UI pass. Publish
        // one complete snapshot, never the intermediate width/height/offsets.
        if (_gpuLensUpdateQueued || _dispatcher.HasShutdownStarted) return;
        _gpuLensUpdateQueued = true;
        _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, (Action)(() =>
        {
            _gpuLensUpdateQueued = false;
            if (_gpuLensEffect is not { } fx) return;
            if (UpdateGpuLens(fx, _gpuLensScreenX, _gpuLensScreenY) && _isActive && _gpuMode)
                ForceRefresh();
        }));
    }

    private void ReleaseGpuLens()
    {
        if (!_dispatcher.CheckAccess())
        {
            if (!_dispatcher.HasShutdownStarted) _dispatcher.Invoke(ReleaseGpuLens);
            return;
        }
        if (_gpuLensEffect != null) _gpuLensEffect.Changed -= OnGpuLensChanged;
        _gpuLensEffect = null;
        _hasGpuLens = false;
    }

    private bool ProcessDesktopGpuFrame(GlassParams p, CaptureRegion region, FrameDimensions dims, int generation, double blurSigma, long refreshVersion,
        IntPtr backdropPixels = default, bool exactWindows = false)
    {
        if (_presentationPaused || !_isActive || !_gpuMode || generation != Volatile.Read(ref _renderGeneration)) return false;
        var presenter = _d3dPresenter;
        if (presenter == null) return false;
        var g = CreateGpuGeometry(p, region, dims);
        D3DImageFramePresenter.GpuFrame frame;
        bool hasLens;
        double screenX, screenY;
        lock (_sync)
        {
            frame = _gpuLens; hasLens = _hasGpuLens;
            screenX = _gpuLensScreenX; screenY = _gpuLensScreenY;
        }
        frame.X = dims.SrcX; frame.Y = dims.SrcY;
        frame.Width = dims.SrcW; frame.Height = dims.SrcH;
        frame.Blur = (float)blurSigma;
        frame.Refract = (UsesNativeRefraction ? 1 : 0) | (refreshVersion != Volatile.Read(ref _renderedRefreshVersion) ? 2 : 0);
        frame.Set(0, g.SrcW); frame.Set(1, g.SrcH);
        if (!hasLens)
        {
            frame.Set(2, g.NotchW); frame.Set(3, g.NotchH);
            frame.Set(6, g.BottomCornerR); frame.Set(7, g.TopCornerR);
            frame.Set(26, 0.9); frame.Set(27, 1.1);
            frame.Set(28, 0.15); frame.Set(29, -0.15);
        }
        frame.Set(4, hasLens ? screenX - dims.SrcX : g.OffX);
        frame.Set(5, hasLens ? screenY - dims.SrcY : g.OffY);
        // The capture and the UI lens are sampled on different threads. A new
        // tray-sized capture must not be masked by the previous, smaller lens.
        if (hasLens && CropGpuPresentationToCapture &&
            (Math.Abs(frame.Get(2) - g.NotchW) > 1 || Math.Abs(frame.Get(3) - g.NotchH) > 1))
        {
            frame.Set(2, g.NotchW); frame.Set(3, g.NotchH);
            frame.Set(4, g.OffX); frame.Set(5, g.OffY);
            frame.Set(6, g.BottomCornerR); frame.Set(7, g.TopCornerR);
        }
        frame.Set(8, g.PowerFactor); frame.Set(9, g.A); frame.Set(10, g.B);
        frame.Set(11, g.C); frame.Set(12, g.D); frame.Set(13, g.FPower);
        frame.Set(14, g.Noise); frame.Set(15, g.GlowWeight);
        frame.Set(16, g.GlowBias); frame.Set(17, g.GlowEdge0);
        frame.Set(18, 0); // Dual-Kawase already blurred the source.
        frame.Set(19, g.Chroma); frame.Set(20, g.SatFactor); frame.Set(21, g.BrightAdd);
        frame.Set(30, g.EdgeBend); frame.Set(31, g.BevelMode);
        // Edge bleed only while the notch moves, and only for hosts that shift the
        // presented frame back (GpuEdgeBleedPx > 0). At rest the output is unchanged.
        int bleed = GpuEdgeBleedPx > 0 && (_captureFullSurface || _animating)
            ? Math.Clamp(Math.Min(GpuEdgeBleedPx, dims.Margin), 0, 128)
            : 0;
        frame.SetY(2, bleed);
        // Same rim width as the CPU renderer: ZRadius in physical px, at most the
        // notch half-size, so expanded panels keep a thin edge lens.
        frame.SetY(3, ComputeRimWidth(p.ZRadius, dims.OutScale, dims.MinHalf));
        bool forceRefresh = (frame.Refract & 2) != 0;
        bool hasPixelBuffer = backdropPixels != IntPtr.Zero;
        if (hasPixelBuffer)
        {
            bool sourceUnchanged = _frameHistory.IsUnchanged(backdropPixels, dims.SrcW, dims.SrcH);
            if (_hasUploadedGpuFrame && !_lastGpuWasExactWindows && !forceRefresh && sourceUnchanged &&
                _hasLastSubmittedGpuFrame && _lastSubmittedGpuFrame.FrameEquals(in frame))
            {
                return false;
            }
            // Same pixels as the last submitted pixel frame: native skips its own
            // full-frame rehash (it still verifies its texture holds that frame).
            if (sourceUnchanged && _hasUploadedGpuFrame) frame.Refract |= 4;
        }
        bool accepted = presenter.CaptureGpuFrame(in frame, g, generation, out bool submitted, backdropPixels,
                checked(dims.SrcW * 4), checked(dims.SrcW * dims.SrcH * 4), exactWindows);
        if (accepted && submitted &&
            ShouldContinueWorker(generation) && ReferenceEquals(presenter, _d3dPresenter))
        {
            _hasUploadedGpuFrame = true;
            _lastGpuWasExactWindows = exactWindows;
            if (hasPixelBuffer) _frameHistory.CommitCheckedFrame();
            _lastSubmittedGpuFrame = frame;
            _hasLastSubmittedGpuFrame = true;
            CompleteRefresh(refreshVersion);
            _lastPresentTicks = Environment.TickCount64;
            OnGpuFrameUploaded?.Invoke();
        }
        return exactWindows && accepted;
    }
}
