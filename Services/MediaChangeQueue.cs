using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace VNotch.Services;

// The channel carries wake-ups only. Change flags live outside the bounded
// buffer, so a timeline burst cannot evict a session or metadata change.
internal sealed class MediaChangeQueue
{
    private readonly Channel<byte> _wakeups = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
    private int _pendingChanges;

    public void Enqueue(ChangeTypes changes)
    {
        if (changes == ChangeTypes.None) return;
        Interlocked.Or(ref _pendingChanges, (int)changes);
        _wakeups.Writer.TryWrite(0);
    }

    public async IAsyncEnumerable<ChangeTypes> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var _ in _wakeups.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var changes = (ChangeTypes)Interlocked.Exchange(ref _pendingChanges, 0);
            if (changes != ChangeTypes.None)
                yield return changes;
        }
    }

    public void Clear()
    {
        while (_wakeups.Reader.TryRead(out _)) { }
        Interlocked.Exchange(ref _pendingChanges, 0);
    }

    public void Complete() => _wakeups.Writer.TryComplete();
}
