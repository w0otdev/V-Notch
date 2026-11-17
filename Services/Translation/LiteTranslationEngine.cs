using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VNotch.Services.Translation;

// Lifetime/marshalling facade only. Tokenization, chunking and INT8 inference live in C++.
internal sealed partial class LiteTranslationEngine : ILocalTranslationEngine
{
    private readonly TranslationModelStore _store;
    private readonly Task _previousDisposal;
    private TranslationOptions _options = new();
    private long _handle;
    private int _loadedThreads;
    private volatile bool _disposed, _enabled = true;
    private volatile TranslationStage _stage;
    public Task Disposal { get; private set; } = Task.CompletedTask;
    public TranslationStage Stage => _stage;
    public bool InstantPresentation => true;

    internal LiteTranslationEngine(TranslationModelStore store, Task? previousDisposal = null)
    {
        _store = store;
        _previousDisposal = previousDisposal ?? Task.CompletedTask;
        _store.Replacing += UnloadUnderGate;
    }

    public void Configure(TranslationOptions options) => Volatile.Write(ref _options, options);
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled) _ = ReleaseDisabledAsync();
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await _previousDisposal.WaitAsync(cancellationToken).ConfigureAwait(false);
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
            if (!_enabled) UnloadUnderGate();
        }
        finally { _store.Gate.Release(); }
    }

    private async Task LoadUnderGateAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        int threads = Volatile.Read(ref _options).Threads;
        if (_handle != 0 && threads == _loadedThreads) return;
        UnloadUnderGate();
        if (!_store.IsInstalled) throw new TranslationException("translation.modelMissing");
        _stage = TranslationStage.CheckingModel;
        await _store.VerifyAsync(ct).ConfigureAwait(false);
        _stage = TranslationStage.LoadingModel;
        Check(await Task.Run(() => vn_translation_lite_open(_store.Root, _store.Root.Length, threads, out _handle), ct).ConfigureAwait(false), ct);
        _loadedThreads = threads;
        if (_disposed) UnloadUnderGate();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
    }

    public async Task<string> TranslateAsync(string text, string sourceLanguage, string targetLanguage, CancellationToken cancellationToken)
    {
        await _previousDisposal.WaitAsync(cancellationToken).ConfigureAwait(false);
        _stage = TranslationStage.Waiting;
        await _store.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadUnderGateAsync(cancellationToken).ConfigureAwait(false);
            _stage = TranslationStage.Translating;
            return await Task.Run(() => TranslateNative(text, targetLanguage, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (!_enabled) UnloadUnderGate();
            _store.Gate.Release();
        }
    }

    private unsafe string TranslateNative(string text, string target, CancellationToken ct)
    {
        var state = GCHandle.Alloc(ct);
        char[] output = ArrayPool<char>.Shared.Rent(65536);
        try
        {
            Check(vn_translation_lite_set_natural(_handle, Volatile.Read(ref _options).EnableHumanizer ? (byte)1 : (byte)0), ct);
            // Bounded native output; an oversized result is an error, never a partial replacement.
            int rc = vn_translation_lite_translate(_handle, text, text.Length, target, target.Length,
                &IsCancelled, GCHandle.ToIntPtr(state), output, 65536, out int written);
            Check(rc, ct);
            ct.ThrowIfCancellationRequested();
            return new string(output, 0, written);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(output, clearArray: true);
            state.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static byte IsCancelled(nint state)
    {
        try { return GCHandle.FromIntPtr(state).Target is CancellationToken ct && ct.IsCancellationRequested ? (byte)1 : (byte)0; }
        catch { return 1; }
    }

    private static void Check(int status, CancellationToken ct)
    {
        if (status == 0) return;
        if (status == -20) throw new OperationCanceledException(ct);
        throw new TranslationException(status switch
        {
            -21 => "translation.languageUnsupported",
            -22 => "translation.liteRuntimeMissing",
            -23 => "translation.modelInvalid",
            -24 or -2 => "translation.textTooLong",
            _ => "translation.failed"
        });
    }

    // Keep warm across popups, but honor the existing idle-retention setting.
    public void ReleaseIdleResources()
    {
        if (_disposed || !_store.Gate.Wait(0)) return;
        try
        {
            if (_handle != 0 && vn_translation_lite_release_idle(_handle,
                Volatile.Read(ref _options).ModelIdleMinutes, out byte released) == 0 && released != 0)
                _handle = 0;
        }
        finally { _store.Gate.Release(); }
    }
    private async Task ReleaseDisabledAsync()
    {
        await _previousDisposal.ConfigureAwait(false);
        await _store.Gate.WaitAsync().ConfigureAwait(false);
        try { if (!_enabled || _disposed) UnloadUnderGate(); }
        finally { _store.Gate.Release(); }
    }
    private void UnloadUnderGate()
    {
        long handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0) vn_translation_lite_close(handle);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.Replacing -= UnloadUnderGate;
        Disposal = Task.Run(ReleaseDisabledAsync);
    }

    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int vn_translation_lite_open(string root, int length, int threads, out long handle);
    [LibraryImport("VNotch.Native.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int vn_translation_lite_translate(long handle, string text, int length,
        string target, int targetLength, delegate* unmanaged[Stdcall]<nint, byte> cancelled, nint state,
        Span<char> output, int capacity, out int written);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_translation_lite_close(long handle);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_translation_lite_release_idle(long handle, int idleMinutes, out byte released);
    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_translation_lite_set_natural(long handle, byte enabled);
}
