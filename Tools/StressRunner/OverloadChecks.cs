using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using VNotch;
using VNotch.Models;
using VNotch.Services;

// Focused checks for the queue's loss/ordering contract, without starting the app.
internal static class OverloadChecks
{
    public static void Run()
    {
        var callbacks = new ConcurrentQueue<Action>();
        var applied = new List<MediaInfo>();
        var type = typeof(MainWindow).Assembly.GetType("VNotch.Services.MediaUpdateQueue", true)!;
        Action<MediaInfo>? enqueue = null;
        bool reenter = false;
        Action<Action> schedule = callbacks.Enqueue;
        Action<MediaInfo> apply = info =>
        {
            applied.Add(info);
            if (reenter) { reenter = false; enqueue!(Info("reentrant")); }
        };
        var queue = (IDisposable)Activator.CreateInstance(type, schedule, apply)!;
        enqueue = type.GetMethod("Enqueue")!.CreateDelegate<Action<MediaInfo>>(queue);
        void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
        void Drain()
        {
            int count = 0;
            while (callbacks.TryDequeue(out var callback))
            {
                Require(++count < 10, "Queue failed to settle");
                callback();
            }
        }

        Parallel.For(0, 100_000, n => enqueue(Info(n.ToString())));
        var latest = Info("last");
        enqueue(latest);
        Require(callbacks.Count == 1, "More than one callback queued during flood");
        Drain();
        Require(applied.Count == 1 && ReferenceEquals(applied[0], latest), "Latest metadata lost");

        applied.Clear();
        enqueue(Info("same"));
        var artwork = Info("same"); artwork.IsThumbnailOnlyUpdate = true;
        enqueue(artwork);
        Drain();
        Require(applied.Count == 2 && !applied[0].IsThumbnailOnlyUpdate && applied[1].IsThumbnailOnlyUpdate,
            "Artwork displaced metadata or was applied out of order");

        applied.Clear();
        enqueue(artwork);
        enqueue(Info("different"));
        Drain();
        Require(applied.Count == 1 && applied[0].CurrentTrack == "different", "Stale artwork survived track change");

        applied.Clear();
        reenter = true;
        enqueue(Info("first"));
        Drain();
        Require(applied.Count == 2 && applied[1].CurrentTrack == "reentrant", "Reentrant update lost");

        applied.Clear();
        enqueue(Info("pending-at-close"));
        queue.Dispose();
        Drain();
        enqueue(Info("after-close"));
        Require(applied.Count == 0 && callbacks.IsEmpty, "Closed consumer received updates");

        var state = new NotchStateManager();
        int changes = 0;
        state.StateChanged += (_, _) => changes++;
        Require(state.TryTransitionTo(NotchState.Expanding), "Expand rejected");
        Require(state.TryTransitionTo(NotchState.Collapsing), "Reverse to collapse rejected");
        Require(state.TryTransitionTo(NotchState.Expanding), "Reverse to expand rejected");
        Require(state.TryTransitionTo(NotchState.Expanded), "Expand completion rejected");
        int beforeRepeat = changes;
        Require(state.TryTransitionTo(NotchState.Expanded) && changes == beforeRepeat, "Repeated state emitted event");
        Require(!state.CanTransitionTo(NotchState.CameraExpanded), "Unrelated invalid transition was enabled");
        Console.WriteLine(JsonSerializer.Serialize(new { passed = true, concurrentUpdates = 100000,
            checks = new[] { "bounded queue", "latest metadata", "artwork ordering", "stale artwork", "reentrant update", "dispose", "animation reversal", "idempotent state", "invalid edge remains rejected" } }));
    }

    private static MediaInfo Info(string track) => new()
    {
        CurrentTrack = track, CurrentArtist = "check", MediaSource = "Browser", SessionInstanceKey = "check"
    };
}
