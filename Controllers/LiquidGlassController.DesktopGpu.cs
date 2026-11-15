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
        frame.Set(8, g.PowerFactor); frame.Set(9, g.A); frame.Set(10, g.B);
        frame.Set(11, g.C); frame.Set(12, g.D); frame.Set(13, g.FPower);
        frame.Set(14, g.Noise); frame.Set(15, g.GlowWeight);
        frame.Set(16, g.GlowBias); frame.Set(17, g.GlowEdge0);
        frame.Set(18, 0); // Dual-Kawase already blurred the source.
        frame.Set(19, g.Chroma); frame.Set(20, g.SatFactor); frame.Set(21, g.BrightAdd);
        frame.Set(30, g.EdgeBend); frame.Set(31, g.BevelMode);
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
