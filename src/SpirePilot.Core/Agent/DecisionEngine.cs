using System.Text.Json;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Route;
using SpirePilot.Core.Team;
using SpirePilot.Core.Text;

namespace SpirePilot.Core.Agent;

public interface IPilotLog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

/// <summary>Raised when the AI settings are unusable (bad key, URL or model): the pilot must stop, not fall back.</summary>
public sealed class PilotConfigException : Exception
{
    public PilotConfigException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Turns a snapshot into a decision: pending plan steps and hard rules first (no LLM call), then the LLM with
/// validation and one repair turn, then the rule-based fallback. Runs on a worker thread; never touches
/// game objects (only the immutable snapshot).
/// </summary>
public sealed class DecisionEngine
{
    private const int MaxMessagesPerDecision = 2;
    private const int MaxInboxInPrompt = 8;
    private const int MaxInfoInPrompt = 6;

    private readonly Func<PilotSettings> _settings;
    private readonly TeamChatHub _team;
    private readonly IPilotLog _log;
    private readonly Func<LlmProfile, ILlmClient> _clientFactory;
    private readonly Func<DateTime> _clock;
    private ILlmClient? _client;
    private string? _clientKey;
    private int _consecutiveLlmFailures;
    private DateTime _llmCooldownUntil = DateTime.MinValue;
    private TimeSpan _cooldown = TimeSpan.FromSeconds(60);
    /// <summary>The breaker tripped and has not seen a success since: one more failure trips it again (half-open).</summary>
    private bool _tripped;

    /// <summary>Forget past LLM failures (settings changed, the user resumed or switched mode): try the model again now.</summary>
    public void ResetCircuit()
    {
        _consecutiveLlmFailures = 0;
        _llmCooldownUntil = DateTime.MinValue;
        _cooldown = TimeSpan.FromSeconds(60);
        _tripped = false;
    }

    private void LlmSucceeded()
    {
        _consecutiveLlmFailures = 0;
        _cooldown = TimeSpan.FromSeconds(60);
        _tripped = false;
        LastLlmError = null;
    }

    /// <summary>Counts a failure; true when the breaker trips (3 in a row, or the first one after a trip).</summary>
    private bool LlmFailed()
    {
        if (++_consecutiveLlmFailures < 3 && !_tripped) return false;
        _consecutiveLlmFailures = 0;
        _tripped = true;
        _llmCooldownUntil = _clock() + _cooldown;
        _cooldown = TimeSpan.FromTicks(Math.Min(_cooldown.Ticks * 2, TimeSpan.FromMinutes(10).Ticks));
        return true;
    }

    public AgentMemory Memory { get; } = new();
    private readonly CharacterPickMemory _picks = new();
    /// <summary>Language name used for team messages ("Simplified Chinese" / "English").</summary>
    public Func<string> MessageLanguage { get; set; } = () => L10n.IsChinese ? "Simplified Chinese" : "English";
    /// <summary>Compact strategy notes for the local character (set by the mod per run); goes into the cached system suffix.</summary>
    public Func<GameSnapshot, string?> CharacterGuide { get; set; } = s => Agent.CharacterGuide.For(s.LocalPlayer?.CharacterId);

    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }
    public long TotalCachedTokens { get; private set; }
    public int LlmCalls { get; private set; }
    /// <summary>Last problem talking to the LLM, for the UI (null after a success).</summary>
    public string? LastLlmError { get; private set; }

    public DecisionEngine(Func<PilotSettings> settings, TeamChatHub team, IPilotLog log,
        Func<LlmProfile, ILlmClient>? clientFactory = null, Func<DateTime>? clock = null)
    {
        _settings = settings;
        _team = team;
        _log = log;
        _clientFactory = clientFactory ?? LlmClientFactory.Create;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Scope in which a multi-step plan stays valid: the same combat round on the same floor.</summary>
    public static string PlanScope(GameSnapshot s) =>
        s.Screen == ScreenKind.Combat && s.Combat != null ? $"combat:{s.Run?.Floor}:{s.Combat.Round}" : $"{s.Screen}:{s.Sequence}";

    /// <summary>
    /// Team messages that deserve a reaction (requests, questions, human chat, a teammate's "!" or "?"). Accept/decline
    /// answers, chat from other AIs and other emotes are context only — replying to AI chat is how two AIs end up
    /// talking forever.
    /// </summary>
    public static bool IsActionable(TeamMessage m) =>
        m.Kind is not (TeamMessageKind.Accept or TeamMessageKind.Decline or TeamMessageKind.Hello) &&
        !(m.FromAi && m.Kind == TeamMessageKind.Chat) &&
        // An AI answering with a counter-request is how two AIs keep each other busy.
        !(m.FromAi && m.ReplyTo != null) &&
        !(m.IsEmote && !TeamEmotes.AsksForReaction(m));

    /// <summary>
    /// Requests that only make sense in the fight they were made in: debuff, focus, block, a potion (none reaches a
    /// teammate outside a fight), and a teammate's "!" or "?".
    /// </summary>
    public static bool IsCombatBound(TeamMessage m) =>
        m.Kind is TeamMessageKind.RequestDebuff or TeamMessageKind.RequestFocus or TeamMessageKind.RequestBlock or TeamMessageKind.RequestPotion ||
        TeamEmotes.AsksForReaction(m);

    /// <summary>The running fight ("floor:encounter"), or null outside one.</summary>
    public static string? FightKey(GameSnapshot s) => s.Combat == null ? null : $"{s.Run?.Floor}:{s.Combat.EncounterName}";

    /// <summary>
    /// Where a capture was taken (fight, round), for a request made or accepted in it: a decision point says so, and so
    /// does a capture inside a fight while waiting; null for one that cannot tell (waiting, no fight read).
    /// </summary>
    public static (string? Fight, int Round)? FightAndRound(GameSnapshot? s) =>
        s != null && (s.CanAct || s.Combat != null) ? (FightKey(s), s.Combat?.Round ?? 0) : null;

    /// <summary>Unread team messages older than this are dropped: requests go stale quickly in combat.</summary>
    public static readonly TimeSpan InboxMaxAge = TimeSpan.FromMinutes(3);

    /// <summary>Why the model may answer without acting when "undo end turn" is all the menu holds.</summary>
    public const string UndoOnlyReason =
        "you already ended your turn; teammates are still playing (undo only to act on the instruction or a teammate's need)";

    /// <summary>
    /// The "[NO ACTION NEEDED]" reason the pilot gives the model for <paramref name="s"/> (the unfiltered capture): it
    /// cannot act, or only "undo end turn" is left; null when it has a real decision to make.
    /// </summary>
    public static string? ReplyOnlyReason(GameSnapshot s) =>
        !s.CanAct || s.Actions.Count == 0 ? s.WaitReason ?? "you cannot act right now"
        : s.Actions.All(a => a.Kind == ActionKind.UndoEndTurn) ? UndoOnlyReason
        : null;

    /// <summary>
    /// After the AI answered a teammate's "!" or "?", the same emote from them is context for this long: a teammate
    /// tapping again must not buy a paid call each time.
    /// </summary>
    private static readonly TimeSpan EmoteCooldown = TimeSpan.FromSeconds(45);
    private readonly Dictionary<(ulong From, string Emote), DateTime> _emotesHandled = new();
    /// <summary>The "!" and "?" the latest model decision was asked to react to; answered once that decision is carried out.</summary>
    private List<TeamMessage> _emotesDecided = new();

    private bool EmoteCoolingDown(TeamMessage m)
    {
        if (!TeamEmotes.AsksForReaction(m)) return false;
        lock (_emotesHandled) return _emotesHandled.TryGetValue((m.From, m.Emote!), out var at) && _clock() - at < EmoteCooldown;
    }

    private void NoteDecided(Inbox inbox)
    {
        lock (_emotesHandled) _emotesDecided = inbox.Actionable.Where(TeamEmotes.AsksForReaction).ToList();
    }

    /// <summary>
    /// A decision's inbox was dealt with (its action ran, or its replies went out): marks <paramref name="ids"/> read
    /// and starts the cooldown of the "!" and "?" among them it reacted to. A decision dropped before that leaves them
    /// unread and still asking.
    /// </summary>
    public void MarkHandled(IEnumerable<string> ids)
    {
        var handled = ids.ToHashSet();
        var now = _clock();
        lock (_emotesHandled)
        {
            foreach (var m in _emotesDecided.Where(m => handled.Contains(m.Id))) _emotesHandled[(m.From, m.Emote!)] = now;
            _emotesDecided = new();
        }
        if (handled.Count > 0) _team.MarkRead(handled);
    }

    /// <summary>
    /// Actionable for this AI: requests and questions, plus a "yes" to our own offer or question ("do you need a
    /// potion?" → "yes") — that one asks us to act.
    /// </summary>
    public bool NeedsAttention(TeamMessage m) => NeedsAttention(m, Memory.FindOutgoing);

    private bool NeedsAttention(TeamMessage m, Func<string?, TeamMessage?> ours) =>
        IsActionable(m) && !EmoteCoolingDown(m) || IsYesToOurOffer(m, ours);

    private static bool IsYesToOurOffer(TeamMessage m, Func<string?, TeamMessage?> ours) =>
        m.Kind == TeamMessageKind.Accept && ours(m.ReplyTo) is { Kind: TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion };

    /// <summary>
    /// The team inbox and our requests as one decision sees them, worked out without side effects (<see cref="PlanInbox"/>):
    /// what to show, and what reading it changes (<see cref="ReadInbox"/> applies that).
    /// </summary>
    /// <param name="Answered">Our own requests the inbox answers (shown so answers have their question).</param>
    /// <param name="Open">Accepted requests after reading.</param>
    /// <param name="Outgoing">Our requests still open after reading.</param>
    /// <param name="Ignored">Unread messages the owner's switches hide from the AI: marked read.</param>
    /// <param name="Promises">A teammate's "yes" to our potion offer or question: a promise to keep.</param>
    /// <param name="Combat">Where we are (fight, round) at a real decision point; null while waiting.</param>
    private sealed record Inbox(
        List<TeamMessage> All, List<TeamMessage> Actionable, List<TeamMessage> Info, List<TeamMessage> Answered,
        List<TeamMessage> Open, List<TeamMessage> Outgoing, List<string> Ignored, List<TeamMessage> Promises,
        List<(string Id, ulong By)> Answers, HashSet<string> Closed, (string? Fight, int Round)? Combat);

    /// <summary>
    /// What the AI reads from the team inbox under the owner's switches: everything with auto-reply on; with it off,
    /// only answers to this player's requests (the AI's or the owner's), which need no reply and may be a promise to
    /// keep. Everything else is marked read.
    /// </summary>
    private static bool Readable(TeamMessage m, PilotSettings settings, Func<string?, TeamMessage?> ours) =>
        settings.Team.AutoReply || m.Kind is TeamMessageKind.Accept or TeamMessageKind.Decline && ours(m.ReplyTo) != null;

    /// <summary>
    /// The one reading of the inbox and our requests, shared by the decision and the prompt preview so both agree:
    /// expired messages and requests (and requests whose fight or round is over) are left out, answers are matched
    /// against our requests as they stand now. Changes nothing.
    /// </summary>
    private Inbox PlanInbox(GameSnapshot snapshot, PilotSettings settings)
    {
        // Only real decision points say where we are; a half-built capture while waiting must not end a fight.
        (string? Fight, int Round)? combat = snapshot.CanAct ? (FightKey(snapshot), snapshot.Combat?.Round ?? 0) : null;
        var (open, outgoing) = Memory.LiveRequests(AgentMemory.RequestMaxAge, combat);
        var unread = _team.PeekAiInbox(InboxMaxAge);
        if (!snapshot.IsMultiplayer)
            return new(new(), new(), new(), new(), open, outgoing, unread.Select(m => m.Id).ToList(), new(), new(), new(), combat);

        TeamMessage? Ours(string? id) => id == null ? null : outgoing.FirstOrDefault(m => m.Id == id);
        var ignored = unread.Where(m => !Readable(m, settings, Ours)).Select(m => m.Id).ToList();
        // A "yes" to our offer asks for an action: a capture without one (a reply-only call) leaves it unread and its
        // offer open for the first that has one (taking the end of turn back counts), where it is shown as one to act on.
        bool cannotAct = !snapshot.CanAct || snapshot.Actions.Count == 0;
        unread = unread.Where(m => Readable(m, settings, Ours) && !(cannotAct && IsYesToOurOffer(m, Ours))).ToList();
        // A teammate's "yes" to a question they already answered (a second tap) asks nothing new.
        var repeats = new HashSet<TeamMessage>();
        var yeses = new HashSet<(string, ulong)>();
        foreach (var m in unread.Where(m => m.Kind == TeamMessageKind.Accept))
            if (Ours(m.ReplyTo) is { } asked && (Memory.AnsweredBy(asked.Id).Contains(m.From) || !yeses.Add((asked.Id, m.From))))
                repeats.Add(m);
        bool Acts(TeamMessage m) => !repeats.Contains(m) && NeedsAttention(m, Ours);
        var actionable = unread.Where(Acts).ToList();
        var info = unread.Where(m => !Acts(m)).ToList();

        var answered = new List<TeamMessage>();
        var promises = new List<TeamMessage>();
        var answers = new List<(string Id, ulong By)>();
        var closed = new HashSet<string>();
        var teammates = snapshot.Teammates.Select(p => p.NetId).ToList();
        foreach (var m in unread.Where(m => m.Kind is TeamMessageKind.Accept or TeamMessageKind.Decline))
        {
            if (Ours(m.ReplyTo) is not { } asked) continue;
            if (!answered.Contains(asked)) answered.Add(asked);
            bool potion = asked.Kind is TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion;
            // A potion question to everyone stays open until every teammate answered: each "yes" is its own promise.
            bool forAll = potion && asked.To == 0;
            var before = Memory.AnsweredBy(asked.Id);
            bool again = before.Contains(m.From) || answers.Contains((asked.Id, m.From));
            answers.Add((asked.Id, m.From));
            // "Yes" to our potion offer / question is now a promise to keep, even across the enemy turn.
            if (m.Kind == TeamMessageKind.Accept && potion && !again)
                promises.Add(new TeamMessage
                {
                    Id = m.Id, From = m.From, FromName = m.FromName, FromAi = m.FromAi, Kind = TeamMessageKind.RequestPotion,
                    Potion = asked.Potion, Target = asked.Target, Time = m.Time,
                    // A bare thumbs up says nothing about what it agreed to.
                    Text = m.IsEmote ? $"{m.Text} (yes to: {asked.Text})" : m.Text,
                });
            if (!forAll || teammates.All(t => before.Contains(t) || answers.Contains((asked.Id, t)))) closed.Add(asked.Id);
        }
        var openAfter = open.Concat(promises.Where(p => open.All(o => o.Id != p.Id))).TakeLast(AgentMemory.MaxOpenRequests).ToList();
        return new(unread, actionable, info, answered, openAfter, outgoing.Where(m => !closed.Contains(m.Id)).ToList(),
            ignored, promises, answers, closed, combat);
    }

    /// <summary><see cref="PlanInbox"/>, then makes it so (<see cref="Read"/>).</summary>
    private Inbox ReadInbox(GameSnapshot snapshot, PilotSettings settings) => Read(PlanInbox(snapshot, settings), snapshot);

    /// <summary>Makes a <see cref="PlanInbox"/> reading so: expiry, the fight noted, ignored mail read, answers applied.</summary>
    private Inbox Read(Inbox inbox, GameSnapshot snapshot)
    {
        _team.ExpireInbox(InboxMaxAge);
        Memory.ExpireRequests(AgentMemory.RequestMaxAge);
        if (inbox.Combat is { } at) Memory.NoteCombat(at.Fight, at.Round);
        if (inbox.Ignored.Count > 0) _team.MarkRead(inbox.Ignored);
        foreach (var promise in inbox.Promises) Memory.TrackAccepted(promise, whileWaiting: !snapshot.CanAct, FightAndRound(snapshot));
        foreach (var (id, by) in inbox.Answers) Memory.NoteAnswer(id, by);
        foreach (var id in inbox.Closed) Memory.CloseOutgoing(id);
        return inbox;
    }

    /// <summary>
    /// The prompt the model would get for <paramref name="snapshot"/> right now — owner rules, inbox, memory, routes —
    /// built from the same inbox reading as a decision, without any side effect (nothing is marked read, expired or
    /// closed). For GET /prompt and diagnostics.
    /// </summary>
    public (string System, string OwnerBlock, string User) PreviewPrompt(GameSnapshot snapshot, string? liveInstruction)
    {
        var settings = _settings();
        var s = OwnerRules.Filtered(snapshot, settings);
        // A capture that cannot act gets a reply-only call, which never carries the owner's instruction.
        var instruction = !snapshot.CanAct || snapshot.Actions.Count == 0 ? null : liveInstruction;
        var ctx = BuildContext(s, settings, PlanInbox(s, settings), instruction);
        ctx.ReplyOnlyReason = ReplyOnlyReason(snapshot);
        var formatter = new StateFormatter { VerboseCards = settings.Strategy.VerboseCards, IncludePiles = settings.Strategy.IncludePiles };
        return (PromptBuilder.CoreSystem, PromptBuilder.BuildOwnerBlock(settings, MessageLanguage(), CharacterGuide(s)),
            PromptBuilder.BuildUser(s, ctx, formatter));
    }

    /// <summary>True when unread team messages ask this AI for a reaction it can give now (used to wake it while it cannot act).</summary>
    public bool HasActionableInbox(GameSnapshot snapshot) => PlanInbox(snapshot, _settings()).Actionable.Count > 0;

    /// <summary>
    /// The owner's team switches applied to what the model wants to say: replies need auto-reply, the AI's own
    /// requests need "proactive requests", and with giving potions off no potion offer or promise, also not as a reply.
    /// At most two messages.
    /// </summary>
    private static void FilterMessages(AgentDecision decision, GameSnapshot snapshot, PilotSettings settings, IReadOnlyList<TeamMessage> inbox)
    {
        if (!snapshot.IsMultiplayer)
        {
            decision.Messages.Clear();
            return;
        }
        decision.Messages.RemoveAll(m =>
        {
            if (!settings.Team.AllowGivingPotions &&
                (m.Kind is TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion ||
                 m.Kind == TeamMessageKind.Accept && inbox.FirstOrDefault(i => i.Id == m.ReplyTo) is { Kind: TeamMessageKind.RequestPotion }))
                return true;
            bool reply = m.ReplyTo != null || m.Kind is TeamMessageKind.Accept or TeamMessageKind.Decline;
            if (reply) return !settings.Team.AutoReply;
            if (m.Kind == TeamMessageKind.Chat) return !settings.Team.AutoReply && !settings.Team.ProactiveRequests;
            return !settings.Team.ProactiveRequests;
        });
        if (decision.Messages.Count > MaxMessagesPerDecision)
            decision.Messages.RemoveRange(MaxMessagesPerDecision, decision.Messages.Count - MaxMessagesPerDecision);
    }

    /// <summary>
    /// The pilot cannot act right now (turn already ended, enemy turn, a teammate's choice) but a teammate asked
    /// something: one LLM call that may only answer. Returns null when there is nothing to answer or the LLM is
    /// unavailable.
    /// </summary>
    public async Task<AgentDecision?> ReplyOnlyAsync(GameSnapshot snapshot, string reason, CancellationToken ct)
    {
        var settings = _settings();
        Memory.MaxHistory = settings.Strategy.HistoryLength;
        // Nothing is read unless the call is made: reading closes what the answers in it answer, and an answer left
        // unread would lose its question.
        var plan = PlanInbox(snapshot, settings);
        if (plan.Actionable.Count == 0 || CoolingDown(settings)) return null;
        var inbox = Read(plan, snapshot);
        // The menu as GET /prompt shows it: owner rules applied, on a copy (the caller's capture stays as it was).
        var s = OwnerRules.Filtered(snapshot, settings);
        var context = BuildContext(s, settings, inbox, null);
        context.ReplyOnlyReason = reason;
        try
        {
            var decision = await AskLlmAsync(s, settings, context, ct, allowNoAction: true).ConfigureAwait(false);
            LlmSucceeded();
            NoteDecided(inbox);
            Memory.SetMemo(decision.Memo);
            decision.ActionIds.Clear();
            decision.PlanKeys.Clear();
            decision.HandledInboxIds = inbox.All.Select(m => m.Id).ToList();
            FilterMessages(decision, snapshot, settings, inbox.All);
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (LlmException ex) when (ex.IsConfigError)
        {
            LastLlmError = ex.Message;
            throw new PilotConfigException(ex.Message, ex);
        }
        catch (PilotConfigException) { throw; }
        catch (Exception ex)
        {
            LastLlmError = ex.Message;
            _log.Warn("LLM reply failed: " + ex.Message);
            LlmFailed();
            return null;
        }
    }

    /// <param name="replyOnlyReason">
    /// Set when the menu holds nothing the AI should do unprompted (only "undo end turn"): the model may then answer
    /// teammates without acting ("actions": []).
    /// </param>
    public async Task<AgentDecision?> DecideAsync(GameSnapshot snapshot, string? liveInstruction, CancellationToken ct, string? replyOnlyReason = null)
    {
        if (snapshot.Actions.Count == 0) return null;
        var settings = _settings();
        Memory.MaxHistory = settings.Strategy.HistoryLength;
        // The owner's hard rules first (the same filter the HTTP API applies), also for plan steps: a rule switched on
        // since the plan was made stops it.
        OwnerRules.Filter(snapshot, settings);
        var inbox = ReadInbox(snapshot, settings);
        var actionable = inbox.Actionable;
        bool hasInstruction = !string.IsNullOrWhiteSpace(liveInstruction);

        // 1) Continue a plan from the previous LLM answer (unless something needs fresh attention). A bare "!" or "?"
        //    waits for the next model call instead of throwing the turn's plan away, but not past the plan's last
        //    step: ending the turn or drinking a potion is for a model that has seen it.
        var scope = PlanScope(snapshot);
        bool boundaryNext = Memory.NextPlanStep is { } next &&
                            snapshot.Actions.FirstOrDefault(a => a.SemanticKey == next) is { } nextStep && PlanSafety.IsBoundary(nextStep, snapshot);
        if (hasInstruction || actionable.Any(m => !TeamEmotes.AsksForReaction(m)) || actionable.Count > 0 && boundaryNext)
        {
            Memory.ClearPlan();
        }
        else if (Memory.TakePlanStep(scope, snapshot, out var abort) is { } key)
        {
            var step = snapshot.Actions.FirstOrDefault(a => a.SemanticKey == key);
            if (step != null)
                return new AgentDecision { Source = DecisionSource.Plan, ActionIds = { step.Id }, Thought = "(plan)" };
            Memory.ClearPlan();
            Memory.AddHistory("(plan stopped: planned action no longer available)");
        }
        else if (abort != null)
        {
            Memory.AddHistory($"(plan stopped: {abort})");
        }

        // 2) Rules from settings. Soft rules (pinned route / planner routing) yield to a live instruction or a
        //    teammate request, which the LLM must see; hard rules (character allow-list) always apply.
        // Informational messages (answers, AI chat) are not marked read on these non-LLM paths: they are context
        // for the next prompt, which is the only place they can matter.
        if (RuleDecision(snapshot, settings, softRules: !hasInstruction && actionable.Count == 0) is { } rule)
            return rule;
        // Owner rules may have filtered the menu (hard restrictions); with nothing left there is nothing to do.
        if (snapshot.Actions.Count == 0) return null;

        // 3) Trivial decisions.
        if (settings.Control.SkipTrivialDecisions && actionable.Count == 0 && !hasInstruction && TrivialAction(snapshot) is { } only)
            return new AgentDecision { Source = DecisionSource.Trivial, ActionIds = { only.Id }, Thought = "(only option)" };

        // 4) Ask the LLM (unless it keeps failing and is cooling down).
        var context = BuildContext(snapshot, settings, inbox, liveInstruction);
        context.ReplyOnlyReason = replyOnlyReason;
        if (CoolingDown(settings) && settings.Control.FallbackOnLlmFailure)
            return Fallback(snapshot, settings, "AI temporarily unavailable: " + LastLlmError);

        try
        {
            var decision = await AskLlmAsync(snapshot, settings, context, ct, allowNoAction: replyOnlyReason != null).ConfigureAwait(false);
            LlmSucceeded();
            PostProcess(decision, snapshot, settings, inbox, scope);
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (LlmException ex) when (ex.IsConfigError)
        {
            LastLlmError = ex.Message;
            throw new PilotConfigException(ex.Message, ex);
        }
        catch (PilotConfigException ex)
        {
            LastLlmError = ex.Message;
            throw;
        }
        catch (Exception ex)
        {
            LastLlmError = ex.Message;
            _log.Warn($"LLM decision failed: {ex.Message}");
            // Circuit breaker: stop paying a full timeout for every decision while the endpoint is down.
            // Without the fallback player there is nothing useful to do: stop instead of retrying (and paying) forever.
            if (LlmFailed() && !settings.Control.FallbackOnLlmFailure)
                throw new PilotConfigException(L10n.T("status.llm_unavailable", ex.Message), ex);
            if (!settings.Control.FallbackOnLlmFailure) throw;
            // Teammate requests stay unread: the rule-based fallback cannot answer them, the LLM can once it is back.
            return Fallback(snapshot, settings, ex.Message);
        }
    }

    private AgentDecision? Fallback(GameSnapshot snapshot, PilotSettings settings, string why)
    {
        Memory.ClearPlan();
        var fb = FallbackPolicy.Choose(snapshot, settings, _picks);
        if (fb == null) return null; // nothing the rules would do here (e.g. only "undo end turn"): wait
        return new AgentDecision
        {
            Source = DecisionSource.Fallback,
            ActionIds = { fb.Id },
            Thought = "(fallback) " + why,
        };
    }

    private AgentDecision? RuleDecision(GameSnapshot s, PilotSettings settings, bool softRules)
    {
        if (s.Screen != ScreenKind.CharacterSelect) _picks.Reset();
        if (s.Screen == ScreenKind.CharacterSelect && s.CharacterSelect is { Characters.Count: > 0 } cs0 &&
            settings.Characters.Allowed.Count > 0 && CharacterPicker.AllowedIds(cs0, settings).Count == 0)
            throw new PilotConfigException(L10n.T("status.no_allowed_character", string.Join(", ", settings.Characters.Allowed)));
        // The menu already went through the owner's hard rules (the same filter the HTTP API applies); now the rules
        // that pick for the model.
        if (s.Screen == ScreenKind.CharacterSelect && CharacterRules(s, settings, _picks) is { } characterRule)
            return characterRule;
        if (softRules && s.Screen == ScreenKind.Map && s.Map is { } map)
        {
            var me = s.LocalPlayer;
            double hp = me is { MaxHp: > 0 } ? (double)me.Hp / me.MaxHp : 1;
            var target = RoutePlanner.PinnedNext(map, settings.Route, s.Run?.Act ?? 0, s.Run?.Seed);
            string why = "(pinned route)";
            if (target == null && !settings.Route.LetAiChooseNodes)
            {
                target = RoutePlanner.BestNext(map, settings.Route, hp, me?.Gold ?? 0);
                why = "(route planner)";
            }
            var action = target == null ? null : s.Actions.FirstOrDefault(a => a.Kind == ActionKind.ChooseMapNode && a.Arg(ActionArgs.Node) == target);
            if (action != null) return new AgentDecision { Source = DecisionSource.Rule, ActionIds = { action.Id }, Thought = why };
        }
        return null;
    }

    /// <summary>Character select decisions the owner's settings make: the ascension step, then the pick policy.</summary>
    private static AgentDecision? CharacterRules(GameSnapshot s, PilotSettings settings, CharacterPickMemory picks)
    {
        AgentDecision Rule(ActionOption a, string why) => new() { Source = DecisionSource.Rule, ActionIds = { a.Id }, Thought = why };
        // After OwnerRules: the only ascension action left is the step to the owner's level.
        if (s.Actions.FirstOrDefault(a => a.Kind == ActionKind.SetAscension) is { } asc)
            return Rule(asc, "(ascension setting)");
        if (settings.Characters.Policy != CharacterPickPolicy.AiChoice && CharacterPicker.Choose(s, settings, picks) is { } pick)
            return Rule(pick, "(character policy)");
        return null;
    }

    /// <summary>An action aimed at another player: its semantic key names the target as "…@P:&lt;netId&gt;".</summary>
    public static bool TargetsTeammate(ActionOption a, GameSnapshot s)
    {
        var key = a.SemanticKey;
        int at = key.IndexOf("@P:", StringComparison.Ordinal);
        if (at < 0) return false;
        var idText = new string(key[(at + 3)..].TakeWhile(char.IsDigit).ToArray());
        return ulong.TryParse(idText, out var id) && id != s.LocalPlayerId;
    }

    /// <summary>
    /// The action to take without asking when the menu has exactly one choice (never discarding a potion). Potions
    /// offered outside combat do not count: they sit next to a screen's own actions and are never what moves it on.
    /// </summary>
    public static ActionOption? TrivialAction(GameSnapshot s)
    {
        var real = s.Actions.Where(a => !s.IsOutOfCombatPotion(a)).ToList();
        return real.Count == 1 && real[0].Kind is not (ActionKind.DiscardPotion or ActionKind.UndoEndTurn) ? real[0] : null;
    }

    private DecisionContext BuildContext(GameSnapshot s, PilotSettings settings, Inbox inbox, string? liveInstruction)
    {
        // Newest first matters; a flood of messages must not blow up the prompt.
        var actionable = inbox.Actionable.TakeLast(MaxInboxInPrompt).ToList();
        // A "yes" is shown once: in the inbox while it asks to act, else as the promise it became (still unread when the
        // decision that read it was not carried out, with its offer closed, it would only be "no reply needed").
        var open = inbox.Open.Where(r => actionable.All(m => m.Id != r.Id)).ToList();
        var ctx = new DecisionContext
        {
            LiveInstruction = liveInstruction,
            Inbox = actionable,
            InboxInfo = inbox.Info.Where(m => open.All(r => r.Id != m.Id)).TakeLast(MaxInfoInPrompt).ToList(),
            OpenRequests = open,
            OutgoingRequests = inbox.Outgoing,
            AnsweredOutgoing = inbox.Answered,
            History = Memory.History,
            Memo = Memory.Memo,
        };
        (ctx.RouteSuggestions, ctx.PinnedNext) = Routes(s, settings);
        return ctx;
    }

    /// <summary>
    /// What the prompt's [ROUTES] block shows on the map screen: the planner's best paths from here and the pinned
    /// route's next node. Nothing on other screens.
    /// </summary>
    public static (List<RoutePath> Routes, string? PinnedNext) Routes(GameSnapshot s, PilotSettings settings)
    {
        if (s.Screen != ScreenKind.Map || s.Map is not { } map) return (new List<RoutePath>(), null);
        var me = s.LocalPlayer;
        double hp = me is { MaxHp: > 0 } ? (double)me.Hp / me.MaxHp : 1;
        return (RoutePlanner.Plan(map, settings.Route, hp, me?.Gold ?? 0, 3),
            RoutePlanner.PinnedNext(map, settings.Route, s.Run?.Act ?? 0, s.Run?.Seed));
    }

    /// <summary>True while the breaker is open. Changed LLM settings close it at once: the old failures were about the old ones.</summary>
    private bool CoolingDown(PilotSettings settings)
    {
        if (_client != null && settings.Active is { } profile && JsonSerializer.Serialize(profile) != _clientKey) ResetCircuit();
        return _clock() < _llmCooldownUntil;
    }

    private ILlmClient Client(LlmProfile profile)
    {
        var key = JsonSerializer.Serialize(profile);
        if (_client == null || _clientKey != key)
        {
            // New settings deserve a fresh chance: the old failures were about the old ones.
            if (_client != null) ResetCircuit();
            _client = _clientFactory(profile);
            _clientKey = key;
        }
        return _client;
    }

    private async Task<AgentDecision> AskLlmAsync(GameSnapshot snapshot, PilotSettings settings, DecisionContext context, CancellationToken ct,
        bool allowNoAction = false)
    {
        var profile = settings.Active ?? throw new PilotConfigException("No AI profile configured.");
        var problems = LlmClientFactory.Validate(profile);
        if (problems.Count > 0) throw new PilotConfigException("AI profile incomplete: " + string.Join(", ", problems));

        var client = Client(profile);
        var formatter = new StateFormatter { VerboseCards = settings.Strategy.VerboseCards, IncludePiles = settings.Strategy.IncludePiles };
        var request = new LlmRequest
        {
            System = PromptBuilder.CoreSystem,
            SystemSuffix = PromptBuilder.BuildOwnerBlock(settings, MessageLanguage(), CharacterGuide(snapshot)),
            JsonSchema = DecisionParser.Schema,
            Effort = snapshot.Screen is ScreenKind.Combat or ScreenKind.HandSelect && !string.IsNullOrWhiteSpace(profile.CombatReasoningEffort)
                ? profile.CombatReasoningEffort
                : null,
            Messages = { new LlmMessage(LlmRole.User, PromptBuilder.BuildUser(snapshot, context, formatter)) },
        };

        string? lastError = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (settings.LogPrompts) _log.Info("PROMPT >>>\n" + request.Messages[^1].Content);
            var response = await LlmHttp.WithRetries(() => client.CompleteAsync(request, ct), maxRetries: 2, ct).ConfigureAwait(false);
            LlmCalls++;
            TotalInputTokens += response.InputTokens ?? 0;
            TotalOutputTokens += response.OutputTokens ?? 0;
            TotalCachedTokens += response.CachedInputTokens ?? 0;
            if (settings.LogPrompts) _log.Info("ANSWER <<<\n" + response.Text);

            var parsed = DecisionParser.Parse(response.Text, snapshot, allowNoAction);
            if (parsed.Ok)
            {
                var d = parsed.Decision!;
                d.InputTokens = response.InputTokens;
                d.OutputTokens = response.OutputTokens;
                d.Elapsed = response.Elapsed;
                return d;
            }
            lastError = parsed.Error;
            _log.Warn($"Unusable LLM answer ({parsed.Error}): {Truncate(response.Text, 300)}");
            // Repair turn: keep the original prompt (cache-friendly) and append the bad answer plus the problem.
            request.Messages.Add(new LlmMessage(LlmRole.Assistant, Truncate(response.Text, 2000)));
            request.Messages.Add(new LlmMessage(LlmRole.User, allowNoAction
                ? $"Invalid answer: {parsed.Error}. Use \"actions\": [] and put your replies in \"messages\". Reply with the JSON object only."
                : $"Invalid answer: {parsed.Error}. Valid ids are {string.Join(", ", snapshot.Actions.Select(a => a.Id))}. Reply with the JSON object only."));
        }
        throw new LlmException("The model did not return a usable decision: " + lastError);
    }

    private void PostProcess(AgentDecision decision, GameSnapshot snapshot, PilotSettings settings, Inbox inbox, string scope)
    {
        Memory.SetMemo(decision.Memo);
        decision.HandledInboxIds = inbox.All.Select(m => m.Id).ToList();
        NoteDecided(inbox);

        // Accepted requests are tracked by the loop once the reply was actually delivered (never in advisor mode).
        FilterMessages(decision, snapshot, settings, inbox.All);

        if (snapshot.Screen == ScreenKind.Combat && settings.Control.AllowTurnPlans && decision.PlanKeys.Count > 0)
            Memory.SetPlan(decision.PlanKeys, scope, PlanGuard.From(snapshot));
        else
            Memory.ClearPlan();
    }

    private static string Truncate(string? s, int max) => s == null ? "" : s.Length > max ? s[..max] + "..." : s;
}
