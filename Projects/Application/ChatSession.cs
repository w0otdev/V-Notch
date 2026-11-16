using VNotch.Services.Spotlight;

namespace VNotch.ApplicationServices;

/// <summary>Request ownership and persistence without a dependency on the window dispatcher.</summary>
public sealed class ChatSession(IChatRepository repository)
{
    private readonly object _gate = new();
    private Task _pendingSave = Task.CompletedTask;
    private readonly Dictionary<string, SpotlightSavedChat> _nextSaves = new();
    private readonly Dictionary<string, (SpotlightSavedChat Chat, Exception Error)> _failedSaves = new();
    private bool _saving;
    private CancellationTokenSource? _request;

    public CancellationTokenSource BeginRequest()
    {
        CancelRequest();
        return _request = new CancellationTokenSource();
    }

    public void CancelRequest()
    {
        var request = _request;
        _request = null;
        request?.Cancel();
    }

    public void EndRequest(CancellationTokenSource request)
    {
        if (ReferenceEquals(_request, request)) _request = null;
    }

    public Task SaveAsync(SpotlightSavedChat chat)
    {
        var snapshot = new SpotlightSavedChat
        {
            Id = chat.Id, Provider = chat.Provider, Model = chat.Model, Draft = chat.Draft,
            UpdatedUtc = chat.UpdatedUtc, MessageOffset = chat.MessageOffset,
            Messages = chat.Messages.Select(m => m with { GeminiParts = m.GeminiParts?.Select(p => p.Clone()).ToArray() }).ToList()
        };
        lock (_gate)
        {
            if (!_nextSaves.ContainsKey(chat.Id) && !_failedSaves.ContainsKey(chat.Id) && _nextSaves.Count + _failedSaves.Count >= 64)
                return Task.FromException(new IOException("Chat persistence queue is full; retry saving."));
            _failedSaves.Remove(chat.Id);
            _nextSaves[chat.Id] = snapshot;
            return StartSaveWorker();
        }
    }

    private Task StartSaveWorker()
    {
        if (_saving) return _pendingSave;
        _saving = true;
        _pendingSave = Task.Run(async () =>
        {
            while (true)
            {
                SpotlightSavedChat next;
                lock (_gate)
                {
                    if (_nextSaves.Count == 0)
                    {
                        _saving = false;
                        if (_failedSaves.Count > 0) throw new AggregateException(_failedSaves.Values.Select(f => f.Error));
                        return;
                    }
                    var item = _nextSaves.First();
                    next = item.Value;
                    _nextSaves.Remove(item.Key);
                }
                try { await repository.SaveChatAsync(next).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lock (_gate)
                        if (!_nextSaves.ContainsKey(next.Id)) _failedSaves[next.Id] = (next, ex);
                }
            }
        });
        return _pendingSave;
    }

    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (_saving || _failedSaves.Count == 0) return _pendingSave;
            // Keep failed snapshots across navigation and retry only at an explicit flush boundary.
            foreach (var (id, failed) in _failedSaves) _nextSaves[id] = failed.Chat;
            _failedSaves.Clear();
            return StartSaveWorker();
        }
    }

    public static List<SpotlightAiMessage> RequestContext(IReadOnlyList<SpotlightAiMessage> messages)
    {
        var history = new List<SpotlightAiMessage>();
        int characters = 0;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var message = messages[i];
            if (message.IsIncomplete) { if (i > 0 && messages[i - 1].Role == "user") i--; continue; }
            if (history.Count >= 21 || (history.Count > 1 && characters + message.Content.Length > 64000)) break;
            history.Add(message);
            characters += message.Content.Length;
        }
        while (history.Count > 0 && history[^1].Role != "user") history.RemoveAt(history.Count - 1);
        history.Reverse();
        return history;
    }
}
