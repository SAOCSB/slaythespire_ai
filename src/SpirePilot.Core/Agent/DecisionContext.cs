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
    /// <summary>Requests / offers / questions this player sent (the AI, or its owner) that are still unanswered or unfulfilled.</summary>
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
    /// <summary>How many accepted requests are kept (oldest dropped first).</summary>
    public const int MaxOpenRequests = 6;
    /// <summary>How long our own requests and accepted ones stay open at most (and emotes shown for them count).</summary>
    public static readonly TimeSpan RequestMaxAge = TimeSpan.FromMinutes(5);

    private readonly object _lock = new();
    private readonly List<string> _history = new();
    private readonly List<Team.TeamMessage> _openRequests = new();
    private readonly List<Team.TeamMessage> _outgoing = new();
    /// <summary>Where each tracked request was made: fight (null = no fight running), round, made while waiting.</summary>
    private readonly Dictionary<string, (string? fight, int round, bool waiting)> _scopes = new();
    /// <summary>Teammates who answered one of our questions to everyone that stays open for the others.</summary>
    private readonly Dictionary<string, HashSet<ulong>> _answeredBy = new();
    /// <summary>Requests shown to teammates as a vanilla emote, newest last: what a thumbs up can answer.</summary>
    private readonly List<(string Id, ulong To, long Time)> _signals = new();
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
    /// <param name="at">Where it was accepted (fight, round); null: the fight last noted.</param>
    public void TrackAccepted(Team.TeamMessage request, bool whileWaiting = false, (string? Fight, int Round)? at = null)
    {
        lock (_lock)
        {
            if (_openRequests.Any(r => r.Id == request.Id)) return;
            _openRequests.Add(request);
            _scopes[request.Id] = Scope(at, whileWaiting);
            while (_openRequests.Count > MaxOpenRequests) Remove(_openRequests, _openRequests[0]);
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private (string? fight, int round, bool waiting) Scope((string? Fight, int Round)? at, bool waiting) =>
        at is { } a ? (a.Fight, a.Round, waiting) : (_fight, _round, waiting);

    private void Remove(List<Team.TeamMessage> list, Team.TeamMessage m)
    {
        list.Remove(m);
        _scopes.Remove(m.Id);
        _answeredBy.Remove(m.Id);
    }

    private void RemoveWhere(List<Team.TeamMessage> list, Func<Team.TeamMessage, bool> gone)
    {
        foreach (var m in list.Where(gone).ToList()) Remove(list, m);
    }

    /// <summary>Drops accepted requests and our own requests older than <paramref name="maxAge"/>.</summary>
    public void ExpireRequests(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)maxAge.TotalMilliseconds;
        lock (_lock)
        {
            RemoveWhere(_openRequests, r => r.Time != 0 && r.Time < cutoff);
            RemoveWhere(_outgoing, r => r.Time != 0 && r.Time < cutoff);
        }
    }

    /// <summary>
    /// Combat-bound requests end with their turn or fight: debuff / focus / block requests hold for the round they were
    /// accepted in (or the next one, when accepted while waiting); anything accepted in a fight ends with that fight.
    /// Requests accepted outside a fight (a potion on the map) carry over into the next fight, where they can be done.
    /// </summary>
    public void NoteCombat(string? fight, int round)
    {
        var scope = CombatScope(fight, round);
        lock (_lock)
        {
            if (scope == _requestsCombat) return;
            _fight = fight;
            _round = round;
            _requestsCombat = scope;
            RemoveWhere(_openRequests, m => Ended(m, fight, round));
            RemoveWhere(_outgoing, m => EndsWithFight(m) && Ended(m, fight, round));
        }
    }

    private static string? CombatScope(string? fight, int round) => fight == null ? null : fight + ":" + round;

    /// <summary>Route suggestions and chat are not about a fight.</summary>
    private static bool EndsWithFight(Team.TeamMessage m) =>
        m.Kind is not (Team.TeamMessageKind.SuggestRoute or Team.TeamMessageKind.Chat);

    /// <summary>Caller holds the lock.</summary>
    private bool Ended(Team.TeamMessage m, string? fight, int round)
    {
        if (!_scopes.TryGetValue(m.Id, out var at)) return false;
        if (at.fight == null) return false;                   // made outside a fight: valid until it expires
        if (at.fight != fight) return true;                   // that fight is over
        bool perRound = m.Kind is Team.TeamMessageKind.RequestDebuff or Team.TeamMessageKind.RequestFocus or Team.TeamMessageKind.RequestBlock;
        return perRound && round > at.round + (at.waiting ? 1 : 0);
    }

    /// <summary>
    /// Accepted requests and our own open requests as <see cref="ExpireRequests"/> with <paramref name="maxAge"/> and
    /// <see cref="NoteCombat"/> at <paramref name="combat"/> (null: not noted) would leave them; changes nothing.
    /// </summary>
    public (List<Team.TeamMessage> Open, List<Team.TeamMessage> Outgoing) LiveRequests(TimeSpan maxAge, (string? Fight, int Round)? combat)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)maxAge.TotalMilliseconds;
        lock (_lock)
        {
            bool noted = combat is { } c && CombatScope(c.Fight, c.Round) != _requestsCombat;
            bool Gone(Team.TeamMessage m, bool fightBound) =>
                m.Time != 0 && m.Time < cutoff || noted && fightBound && Ended(m, combat!.Value.Fight, combat.Value.Round);
            return (_openRequests.Where(m => !Gone(m, true)).ToList(), _outgoing.Where(m => !Gone(m, EndsWithFight(m))).ToList());
        }
    }

    /// <summary>We gave <paramref name="player"/> a potion: their potion request (and our offer to them) is done.</summary>
    public void FulfilledPotionFor(ulong player)
    {
        lock (_lock)
        {
            RemoveWhere(_openRequests, r => r.From == player && r.Kind is Team.TeamMessageKind.RequestPotion);
            RemoveWhere(_outgoing, r => (r.To == player || r.To == 0) && r.Kind is Team.TeamMessageKind.OfferPotion or Team.TeamMessageKind.AskNeedPotion);
        }
    }

    /// <summary>Remembers a request / offer / question we sent (delivered), so answers to it can be matched.</summary>
    /// <param name="signalled">It was also shown as a vanilla emote, so a thumbs up can answer it.</param>
    /// <param name="at">Where it was sent (fight, round); null: the fight last noted.</param>
    public void TrackOutgoing(Team.TeamMessage sent, bool whileWaiting = false, bool signalled = false, (string? Fight, int Round)? at = null)
    {
        lock (_lock)
        {
            _outgoing.Add(sent);
            _scopes[sent.Id] = Scope(at, whileWaiting);
            while (_outgoing.Count > 8) Remove(_outgoing, _outgoing[0]);
        }
        if (signalled) NoteSignal(sent.Id, sent.To);
    }

    /// <summary>
    /// An emote that asked teammates something was shown for <paramref name="id"/> (an untracked id when nobody can
    /// answer it, e.g. the owner's own "!" button): from now on a thumbs up answers this, never an older request.
    /// </summary>
    public void NoteSignal(string id, ulong to)
    {
        lock (_lock)
        {
            _signals.Add((id, to, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            while (_signals.Count > 16) _signals.RemoveAt(0);
        }
    }

    /// <summary>
    /// The newest request shown to <paramref name="teammate"/> (or everyone) as an emote within <see cref="RequestMaxAge"/>,
    /// and whether it is still open; null when there is none.
    /// </summary>
    public (string Id, bool Open)? LastSignalTo(ulong teammate)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)RequestMaxAge.TotalMilliseconds;
        lock (_lock)
        {
            for (int i = _signals.Count - 1; i >= 0; i--)
            {
                var s = _signals[i];
                if (s.Time < cutoff) break;
                if (s.To == teammate || s.To == 0) return (s.Id, _outgoing.Any(m => m.Id == s.Id));
            }
            return null;
        }
    }

    /// <summary>
    /// The newest of our open requests that <paramref name="teammate"/> read as text (to them or to everyone) within
    /// <see cref="RequestMaxAge"/>; null when there is none.
    /// </summary>
    public (string Id, bool Open)? LastOpenTo(ulong teammate)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (long)RequestMaxAge.TotalMilliseconds;
        lock (_lock)
            return _outgoing.LastOrDefault(m => (m.To == teammate || m.To == 0) && (m.Time == 0 || m.Time >= cutoff)) is { } last
                ? (last.Id, true)
                : null;
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
        lock (_lock) RemoveWhere(_outgoing, m => m.Id == id);
    }

    /// <summary>
    /// <paramref name="teammate"/> answered our request <paramref name="id"/>, which stays open for the others (a
    /// potion question to everyone: each "yes" is its own promise).
    /// </summary>
    public void NoteAnswer(string id, ulong teammate)
    {
        lock (_lock)
        {
            if (!_outgoing.Any(m => m.Id == id)) return;
            if (!_answeredBy.TryGetValue(id, out var who)) _answeredBy[id] = who = new HashSet<ulong>();
            who.Add(teammate);
        }
    }

    /// <summary>Teammates who already answered our open request <paramref name="id"/>.</summary>
    public IReadOnlyCollection<ulong> AnsweredBy(string id)
    {
        lock (_lock) return _answeredBy.TryGetValue(id, out var who) ? who.ToList() : Array.Empty<ulong>();
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

    /// <summary>The semantic key of the plan's next step, without taking it; null without a plan.</summary>
    public string? NextPlanStep { get { lock (_lock) return _plan.Count > 0 ? _plan[0] : null; } }

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
            _answeredBy.Clear();
            _signals.Clear();
            _requestsCombat = null;
            _fight = null;
            _round = 0;
            _plan.Clear();
            _planScope = null;
            _planGuard = null;
        }
    }
}
