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
    /// <summary>Requests / offers / questions this AI sent that are still unanswered or unfulfilled.</summary>
    public List<TeamMessage> OutgoingRequests { get; set; } = new();
    /// <summary>Our own requests that the current inbox answers (closed now; shown so answers have their question).</summary>
    public List<TeamMessage> AnsweredOutgoing { get; set; } = new();
    /// <summary>The menu is empty (turn already ended, enemy turn ...): the model may only answer teammates.</summary>
    public string? ReplyOnlyReason { get; set; }
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
        int expected = HandCount - CardsPlayed;
        if (s.Combat.Hand.Count > expected) return "new cards in hand";
        // Fewer cards than planned: someone else played (the human, between pauses) or a card left the hand.
        if (s.Combat.Hand.Count < expected) return "hand changed";
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
    private readonly List<Team.TeamMessage> _outgoing = new();
    /// <summary>Where each tracked request was made: fight (null = no fight running), round, made while waiting.</summary>
    private readonly Dictionary<string, (string? fight, int round, bool waiting)> _scopes = new();
    private string? _requestsCombat;
    private string? _fight;
    private int _round;
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

    /// <param name="whileWaiting">Accepted while the AI could not act (enemy turn, turn ended): a per-round request
    /// then still holds for the next round.</param>
    public void TrackAccepted(Team.TeamMessage request, bool whileWaiting = false)
    {
        lock (_lock)
        {
            if (_openRequests.Any(r => r.Id == request.Id)) return;
            _openRequests.Add(request);
            _scopes[request.Id] = (_fight, _round, whileWaiting);
            while (_openRequests.Count > 6) Forget(_openRequests, 0);
        }
    }

    private void Forget(List<Team.TeamMessage> list, int index)
    {
        _scopes.Remove(list[index].Id);
        list.RemoveAt(index);
    }

    /// <summary>Drops accepted requests and our own requests older than <paramref name="maxAge"/>.</summary>
    public void ExpireRequests(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)maxAge.TotalMilliseconds;
        lock (_lock)
        {
            _openRequests.RemoveAll(r => r.Time != 0 && r.Time < cutoff);
            _outgoing.RemoveAll(r => r.Time != 0 && r.Time < cutoff);
        }
    }

    /// <summary>
    /// Combat-bound requests end with their turn or fight: debuff / focus / block requests hold for the round they were
    /// accepted in (or the next one, when accepted while waiting); anything accepted in a fight ends with that fight.
    /// Requests accepted outside a fight (a potion on the map) carry over into the next fight, where they can be done.
    /// </summary>
    public void NoteCombat(string? fight, int round)
    {
        var scope = fight == null ? null : fight + ":" + round;
        lock (_lock)
        {
            if (scope == _requestsCombat) return;
            _fight = fight;
            _round = round;
            _requestsCombat = scope;
            static bool PerRound(Team.TeamMessage m) =>
                m.Kind is Team.TeamMessageKind.RequestDebuff or Team.TeamMessageKind.RequestFocus or Team.TeamMessageKind.RequestBlock;
            bool Ended(Team.TeamMessage m)
            {
                if (!_scopes.TryGetValue(m.Id, out var at)) return false;
                if (at.fight == null) return false;                   // made outside a fight: valid until it expires
                if (at.fight != fight) return true;                   // that fight is over
                return PerRound(m) && round > at.round + (at.waiting ? 1 : 0);
            }
            for (int i = _openRequests.Count - 1; i >= 0; i--)
                if (Ended(_openRequests[i])) Forget(_openRequests, i);
            for (int i = _outgoing.Count - 1; i >= 0; i--)
                if (_outgoing[i].Kind is not (Team.TeamMessageKind.SuggestRoute or Team.TeamMessageKind.Chat) && Ended(_outgoing[i]))
                    Forget(_outgoing, i);
        }
    }

    /// <summary>We gave <paramref name="player"/> a potion: their potion request (and our offer to them) is done.</summary>
    public void FulfilledPotionFor(ulong player)
    {
        lock (_lock)
        {
            _openRequests.RemoveAll(r => r.From == player && r.Kind is Team.TeamMessageKind.RequestPotion);
            _outgoing.RemoveAll(r => (r.To == player || r.To == 0) && r.Kind is Team.TeamMessageKind.OfferPotion or Team.TeamMessageKind.AskNeedPotion);
        }
    }

    /// <summary>Remembers a request / offer / question we sent (delivered), so answers to it can be matched.</summary>
    public void TrackOutgoing(Team.TeamMessage sent, bool whileWaiting = false)
    {
        lock (_lock)
        {
            _outgoing.Add(sent);
            _scopes[sent.Id] = (_fight, _round, whileWaiting);
            while (_outgoing.Count > 8) Forget(_outgoing, 0);
        }
    }

    public Team.TeamMessage? FindOutgoing(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_lock) return _outgoing.FirstOrDefault(m => m.Id == id);
    }

    /// <summary>An answer to one of our requests closes it.</summary>
    public void CloseOutgoing(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (_lock) _outgoing.RemoveAll(m => m.Id == id);
    }

    /// <summary>True when we already asked <paramref name="to"/> (or everyone) the same kind of thing and it is still open.</summary>
    public bool HasOpenOutgoing(Team.TeamMessageKind kind, ulong to)
    {
        lock (_lock) return _outgoing.Any(m => m.Kind == kind && (m.To == to || m.To == 0 || to == 0));
    }

    public List<Team.TeamMessage> OutgoingRequests { get { lock (_lock) return _outgoing.ToList(); } }

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
            _outgoing.Clear();
            _scopes.Clear();
            _requestsCombat = null;
            _fight = null;
            _round = 0;
            _plan.Clear();
            _planScope = null;
            _planGuard = null;
        }
    }
}
