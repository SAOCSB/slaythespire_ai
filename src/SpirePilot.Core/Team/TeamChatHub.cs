namespace SpirePilot.Core.Team;

/// <summary>Transport that carries team messages between players' games. Implemented by the mod.</summary>
public interface ITeamTransport
{
    /// <summary>True when at least one other player can receive our messages.</summary>
    bool IsAvailable { get; }
    /// <summary>Net ids of other players known to run SpirePilot.</summary>
    IReadOnlyCollection<ulong> LinkedPeers { get; }
    /// <summary>Sends to <see cref="TeamMessage.To"/> (0 = everyone). Returns false when nothing could be sent.</summary>
    bool Send(TeamMessage message);
}

public sealed class ChatEntry
{
    public TeamMessage Message { get; init; } = new();
    public bool Incoming { get; init; }
    /// <summary>False when the message could not be delivered to anyone (shown locally only).</summary>
    public bool Delivered { get; init; } = true;
    public DateTime LocalTime { get; init; } = DateTime.Now;
}

/// <summary>
/// Central place for team chat: keeps the visible log, the AI's unread inbox and open requests, enforces
/// the AI message rate limit, and de-duplicates relayed messages. Thread-safe: messages arrive on the game
/// thread while the agent reads its inbox from a worker thread.
/// </summary>
public sealed class TeamChatHub
{
    private const int MaxLog = 300;
    private readonly object _lock = new();
    private readonly List<ChatEntry> _log = new();
    private readonly HashSet<string> _seenIds = new();
    private readonly Queue<string> _seenOrder = new();
    private readonly List<TeamMessage> _aiInbox = new();
    private readonly Queue<DateTime> _aiSendTimes = new();
    private readonly Queue<(DateTime time, string key)> _recentAiTexts = new();
    private readonly Func<DateTime> _clock;

    public ITeamTransport? Transport { get; set; }
    public ulong LocalNetId { get; set; }
    public string LocalName { get; set; } = "";
    /// <summary>AI message budget per minute (from settings).</summary>
    public int MaxAiMessagesPerMinute { get; set; } = 4;

    /// <summary>Raised (on the calling thread) whenever the log changes.</summary>
    public event Action<ChatEntry>? EntryAdded;

    public TeamChatHub(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    public IReadOnlyList<ChatEntry> Log
    {
        get { lock (_lock) return _log.ToList(); }
    }

    /// <summary>
    /// Sends a message from the local player. <paramref name="fromAi"/> marks AI-written messages, which are
    /// rate-limited. Returns the log entry, or null when the AI budget is exhausted.
    /// </summary>
    public ChatEntry? Send(TeamMessage message, bool fromAi)
    {
        message.From = LocalNetId;
        message.FromName = string.IsNullOrEmpty(message.FromName) ? LocalName : message.FromName;
        message.FromAi = fromAi;
        message.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        message = message.Sanitized();

        lock (_lock)
        {
            if (fromAi && IsRecentAiDuplicate(message)) return null;
            if (fromAi && !TryConsumeAiBudget()) return null;
            Remember(message.Id);
            if (fromAi) _recentAiTexts.Enqueue((_clock(), message.Kind + "|" + message.Text.Trim()));
        }

        bool delivered = false;
        try { delivered = Transport is { IsAvailable: true } t && t.Send(message); }
        catch (Exception) { delivered = false; }

        var entry = new ChatEntry { Message = message, Incoming = false, Delivered = delivered };
        Append(entry);
        return entry;
    }

    /// <summary>Called by the transport for each received message. Duplicates and self-echoes are ignored.</summary>
    public bool Receive(TeamMessage message)
    {
        message = message.Sanitized();
        lock (_lock)
        {
            if (message.From == LocalNetId && LocalNetId != 0) return false;
            if (!Remember(message.Id)) return false;
            bool forUs = message.To == 0 || message.To == LocalNetId;
            if (!forUs) return false;
            // Hello is link bookkeeping (handled by the transport), not something to show or answer.
            if (message.Kind == TeamMessageKind.Hello) return true;
            _aiInbox.Add(message);
        }
        Append(new ChatEntry { Message = message, Incoming = true });
        return true;
    }

    /// <summary>Messages the AI has not processed yet (oldest first). They stay unread until <see cref="MarkRead"/>.</summary>
    public List<TeamMessage> PeekAiInbox()
    {
        lock (_lock) return _aiInbox.ToList();
    }

    public void MarkRead(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        lock (_lock) _aiInbox.RemoveAll(m => set.Contains(m.Id));
    }

    /// <summary>Drops inbox messages older than <paramref name="maxAge"/> (requests go stale quickly in combat).</summary>
    public void ExpireInbox(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)maxAge.TotalMilliseconds;
        lock (_lock) _aiInbox.RemoveAll(m => m.Time != 0 && m.Time < cutoff);
    }

    /// <summary>Last <paramref name="count"/> log entries, for prompts.</summary>
    public List<ChatEntry> Recent(int count)
    {
        lock (_lock) return _log.Skip(Math.Max(0, _log.Count - count)).ToList();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _log.Clear();
            _aiInbox.Clear();
        }
    }

    /// <summary>Adds a local-only system line to the visible log (e.g. "team link unavailable").</summary>
    public void AddSystemNote(string text) =>
        Append(new ChatEntry { Message = new TeamMessage { Kind = TeamMessageKind.Chat, Text = text, FromName = "SpirePilot" }, Incoming = true, Delivered = false });

    /// <summary>The AI repeating the same message within a few minutes is noise.</summary>
    private bool IsRecentAiDuplicate(TeamMessage message)
    {
        var now = _clock();
        while (_recentAiTexts.Count > 0 && now - _recentAiTexts.Peek().time > TimeSpan.FromMinutes(3)) _recentAiTexts.Dequeue();
        var key = message.Kind + "|" + message.Text.Trim();
        return _recentAiTexts.Any(t => t.key == key);
    }

    private bool TryConsumeAiBudget()
    {
        var now = _clock();
        while (_aiSendTimes.Count > 0 && now - _aiSendTimes.Peek() > TimeSpan.FromMinutes(1)) _aiSendTimes.Dequeue();
        if (_aiSendTimes.Count >= MaxAiMessagesPerMinute) return false;
        _aiSendTimes.Enqueue(now);
        return true;
    }

    /// <summary>Records an id; returns false if it was already seen.</summary>
    private bool Remember(string id)
    {
        if (!_seenIds.Add(id)) return false;
        _seenOrder.Enqueue(id);
        while (_seenOrder.Count > 2000) _seenIds.Remove(_seenOrder.Dequeue());
        return true;
    }

    private void Append(ChatEntry entry)
    {
        lock (_lock)
        {
            _log.Add(entry);
            if (_log.Count > MaxLog) _log.RemoveRange(0, _log.Count - MaxLog);
        }
        EntryAdded?.Invoke(entry);
    }
}
