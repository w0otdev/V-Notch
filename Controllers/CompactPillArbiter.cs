using System;
using System.Runtime.InteropServices;

namespace VNotch.Controllers;

public enum CompactPillSlot
{
    None = 0,
    Clipboard = 1,
    Volume = 2,
    Bluetooth = 3,
    Charging = 4,
    Greeting = 5
}

public sealed partial class CompactPillArbiter
{
    private readonly object _gate = new();
    private CompactPillSlot _activeSlot = CompactPillSlot.None;
    private int _activeToken = 0;
    private int _nextToken = 1;
    private long _revision;

    [LibraryImport("VNotch.Native.dll")]
    private static partial int vn_compact_pill_try_acquire(
        int requestedSlot,
        ref int ioActiveSlot,
        ref int ioActiveToken,
        ref int ioNextToken,
        ref long ioRevision,
        out byte outWon,
        out int outPreempted,
        out int outToken);

    public long Revision { get { lock (_gate) return _revision; } }

    public bool CanRestoreMedia(long revision)
    {
        lock (_gate) return revision == _revision && _activeSlot == CompactPillSlot.None;
    }

    public CompactPillSlot ActiveSlot
    {
        get { lock (_gate) return _activeSlot; }
    }

    public int ActiveToken
    {
        get { lock (_gate) return _activeToken; }
    }

    public bool IsActive(CompactPillSlot slot)
    {
        lock (_gate) return _activeSlot == slot;
    }

    public bool CanAcquire(CompactPillSlot slot)
    {
        if (slot == CompactPillSlot.None) return false;
        lock (_gate)
        {
            return _activeSlot == CompactPillSlot.None
                || (int)slot >= (int)_activeSlot;
        }
    }

    public AcquireResult TryAcquire(CompactPillSlot slot)
    {
        lock (_gate)
        {
            int activeSlot = (int)_activeSlot;
            _ = vn_compact_pill_try_acquire(
                (int)slot,
                ref activeSlot,
                ref _activeToken,
                ref _nextToken,
                ref _revision,
                out byte won,
                out int preempted,
                out int token);
            _activeSlot = (CompactPillSlot)activeSlot;
            return new AcquireResult(won != 0, (CompactPillSlot)preempted, token);
        }
    }

    public void Release(int token)
    {
        lock (_gate)
        {
            if (token == 0 || token != _activeToken) return;
            _activeSlot = CompactPillSlot.None;
            _activeToken = 0;
            _revision++;
        }
    }

    public void ForceClear()
    {
        lock (_gate)
        {
            _activeSlot = CompactPillSlot.None;
            _activeToken = 0;
            _revision++;
        }
    }

    public bool IsTokenCurrent(int token)
    {
        if (token == 0) return false;
        lock (_gate) return token == _activeToken;
    }

    public readonly struct AcquireResult
    {
        public AcquireResult(bool won, CompactPillSlot preempted, int token)
        {
            Won = won;
            Preempted = preempted;
            Token = token;
        }

        public bool Won { get; }

        public CompactPillSlot Preempted { get; }

        public int Token { get; }
    }
}
