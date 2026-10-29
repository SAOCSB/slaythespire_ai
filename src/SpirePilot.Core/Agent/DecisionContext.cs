using SpirePilot.Core.Model;
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
    public string? Memo { get; set; }
    public List<RoutePath> RouteSuggestions { get; set; } = new();
    public string? PinnedNext { get; set; }
    /// <summary>Team messages already answered or informational (accept / decline / AI chat): context only.</summary>
    public List<TeamMessage> InboxInfo { get; set; } = new();
}

/// <summary>
/// What the state looked like when a plan was made. A plan step is only executed while reality still matches:
/// no HP lost, no cards drawn beyond the plan, no new enemies.
/// </summary>
public sealed class PlanGuard
{
    public int Hp { get; init; }
    public int HandCount { get; init; }
    public int AliveEnemies { get; init; }
    public int CardsPlayed { get; set; }

    public static PlanGuard? From(GameSnapshot s) => s.Combat == null ? null : new PlanGuard
    {
        Hp = s.LocalPlayer?.Hp ?? 0,
        HandCount = s.Combat.Hand.Count,
        AliveEnemies = s.Combat.Enemies.Count(e => e.IsAlive),
    };

    /// <summary>Null when the plan may continue, otherwise the reason it must stop.</summary>
    public string? Violation(GameSnapshot s)
    {
        if (s.Combat == null) return "combat ended";
        if ((s.LocalPlayer?.Hp ?? 0) < Hp) return "took damage";
        if (s.Combat.Hand.Count > HandCount - CardsPlayed) return "new cards in hand";
        if (s.Combat.Enemies.Count(e => e.IsAlive) > AliveEnemies) return "new enemy";
        return null;
    }
}

/// <summary>
/// Run-scoped memory of the agent: recent action history, the model's memo, accepted team requests,
/// and the pending multi-step plan. Thread-safe.
/// </summary>
public sealed class AgentMemory
{
    public const int MaxMemoLength = 400;

    private readonly object _lock = new();
    private readonly List<string> _history = new();
    private readonly List<Team.TeamMessage> _openRequests = new();
    private readonly List<string> _plan = new();
    private string? _planScope;
    private PlanGuard? _planGuard;
    private string? _memo;

    public int MaxHistory { get; set; } = 12;

    public void AddHistory(string line)
    {
        lock (_lock)
        {
            _history.Add(line);
            while (_history.Count > Math.Max(0, MaxHistory)) _history.RemoveAt(0);
        }
    }

    /// <summary>
    /// Replaces the memo (the model sends its whole memo each time; null/empty keeps the current one).
    /// Menu ids like "a3" are stripped: they are only valid for one snapshot.
    /// </summary>
    public void SetMemo(string? memo)
    {
        if (string.IsNullOrWhiteSpace(memo)) return;
        memo = System.Text.RegularExpressions.Regex.Replace(memo.Trim(), @"\ba\d{1,3}\b", "").Trim();
        if (memo.Length > MaxMemoLength) memo = memo[..MaxMemoLength];
        lock (_lock) _memo = memo;
    }

    public string? Memo { get { lock (_lock) return _memo; } }

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
    public List<Team.TeamMessage> OpenRequests { get { lock (_lock) return _openRequests.ToList(); } }

    /// <summary>Stores the remaining plan steps; <paramref name="scope"/> identifies when the plan is valid (e.g. "combat:7:3").</summary>
    public void SetPlan(IEnumerable<string> semanticKeys, string scope, PlanGuard? guard)
    {
        lock (_lock)
        {
            _plan.Clear();
            _plan.AddRange(semanticKeys);
            _planScope = scope;
            _planGuard = guard;
            if (_planGuard != null) _planGuard.CardsPlayed = 1; // the first action (executed now) is a card play or ends the plan
        }
    }

    /// <summary>
    /// Takes the next plan step if the plan is still valid for <paramref name="scope"/> and the current state.
    /// Returns null (and clears the plan) otherwise; <paramref name="abortReason"/> says why.
    /// </summary>
    public string? TakePlanStep(string scope, GameSnapshot snapshot, out string? abortReason)
    {
        abortReason = null;
        lock (_lock)
        {
            if (_plan.Count == 0) return null;
            abortReason = _planScope != scope ? "situation changed" : _planGuard?.Violation(snapshot);
            if (abortReason != null)
            {
                _plan.Clear();
                return null;
            }
            var next = _plan[0];
            _plan.RemoveAt(0);
            if (_planGuard != null && next.StartsWith("play", StringComparison.Ordinal)) _planGuard.CardsPlayed++;
            return next;
        }
    }

    public void ClearPlan()
    {
        lock (_lock)
        {
            _plan.Clear();
            _planScope = null;
            _planGuard = null;
        }
    }

    public bool HasPlan { get { lock (_lock) return _plan.Count > 0; } }

    /// <summary>Forget everything (new run).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _history.Clear();
            _memo = null;
            _openRequests.Clear();
            _plan.Clear();
            _planScope = null;
            _planGuard = null;
        }
    }
}
