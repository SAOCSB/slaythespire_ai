using SpirePilot.Core.Route;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Agent;

/// <summary>Everything besides the snapshot that goes into one decision.</summary>
public sealed class DecisionContext
{
    /// <summary>One-off instruction typed by the human in the overlay ("use the potion now", "go left").</summary>
    public string? LiveInstruction { get; set; }
    public List<TeamMessage> Inbox { get; set; } = new();
    public List<TeamMessage> OpenRequests { get; set; } = new();
    public List<string> History { get; set; } = new();
    public List<string> Memos { get; set; } = new();
    public List<RoutePath> RouteSuggestions { get; set; } = new();
    public string? PinnedNext { get; set; }
    /// <summary>Set when re-asking after an invalid answer.</summary>
    public string? Feedback { get; set; }
}

/// <summary>
/// Run-scoped memory of the agent: recent action history, memos the model asked to keep, accepted requests,
/// and the pending multi-step plan. Thread-safe.
/// </summary>
public sealed class AgentMemory
{
    private readonly object _lock = new();
    private readonly List<string> _history = new();
    private readonly List<string> _memos = new();
    private readonly List<Team.TeamMessage> _openRequests = new();
    private readonly List<string> _plan = new();
    private string? _planScope;

    public int MaxHistory { get; set; } = 12;
    public int MaxMemos { get; set; } = 8;

    public void AddHistory(string line)
    {
        lock (_lock)
        {
            _history.Add(line);
            while (_history.Count > Math.Max(0, MaxHistory)) _history.RemoveAt(0);
        }
    }

    public void AddMemo(string memo)
    {
        memo = memo.Trim();
        if (memo.Length == 0) return;
        if (memo.Length > 300) memo = memo[..300];
        lock (_lock)
        {
            _memos.Remove(memo);
            _memos.Add(memo);
            while (_memos.Count > MaxMemos) _memos.RemoveAt(0);
        }
    }

    public void TrackAccepted(Team.TeamMessage request)
    {
        lock (_lock)
        {
            if (_openRequests.Any(r => r.Id == request.Id)) return;
            _openRequests.Add(request);
            while (_openRequests.Count > 6) _openRequests.RemoveAt(0);
        }
    }

    /// <summary>Drops accepted requests older than <paramref name="maxAge"/>.</summary>
    public void ExpireRequests(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)maxAge.TotalMilliseconds;
        lock (_lock) _openRequests.RemoveAll(r => r.Time != 0 && r.Time < cutoff);
    }

    public List<string> History { get { lock (_lock) return _history.ToList(); } }
    public List<string> Memos { get { lock (_lock) return _memos.ToList(); } }
    public List<Team.TeamMessage> OpenRequests { get { lock (_lock) return _openRequests.ToList(); } }

    /// <summary>Stores the remaining plan steps; <paramref name="scope"/> identifies when the plan is valid (e.g. "combat:r3").</summary>
    public void SetPlan(IEnumerable<string> semanticKeys, string scope)
    {
        lock (_lock)
        {
            _plan.Clear();
            _plan.AddRange(semanticKeys);
            _planScope = scope;
        }
    }

    /// <summary>Takes the next plan step if the plan is still valid for <paramref name="scope"/>.</summary>
    public string? TakePlanStep(string scope)
    {
        lock (_lock)
        {
            if (_plan.Count == 0) return null;
            if (_planScope != scope)
            {
                _plan.Clear();
                return null;
            }
            var next = _plan[0];
            _plan.RemoveAt(0);
            return next;
        }
    }

    public void ClearPlan()
    {
        lock (_lock)
        {
            _plan.Clear();
            _planScope = null;
        }
    }

    public bool HasPlan { get { lock (_lock) return _plan.Count > 0; } }

    /// <summary>Forget everything (new run).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _history.Clear();
            _memos.Clear();
            _openRequests.Clear();
            _plan.Clear();
            _planScope = null;
        }
    }
}
