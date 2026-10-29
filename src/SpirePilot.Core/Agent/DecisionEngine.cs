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
    /// Team messages that deserve a reaction (requests, questions, human chat). Accept/decline answers and chat
    /// from other AIs are context only — replying to them is how two AIs end up talking forever.
    /// </summary>
    public static bool IsActionable(TeamMessage m) =>
        m.Kind is not (TeamMessageKind.Accept or TeamMessageKind.Decline or TeamMessageKind.Hello) &&
        !(m.FromAi && m.Kind == TeamMessageKind.Chat) &&
        // An AI answering with a counter-request is how two AIs keep each other busy.
        !(m.FromAi && m.ReplyTo != null);

    /// <summary>
    /// Actionable for this AI: requests and questions, plus a "yes" to our own offer or question ("do you need a
    /// potion?" → "yes") — that one asks us to act.
    /// </summary>
    public bool NeedsAttention(TeamMessage m) =>
        IsActionable(m) ||
        m.Kind == TeamMessageKind.Accept && Memory.FindOutgoing(m.ReplyTo) is { Kind: TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion };

    /// <summary>Unread team messages split into those that need a reaction and context-only ones.</summary>
    private sealed record Inbox(List<TeamMessage> All, List<TeamMessage> Actionable, List<TeamMessage> Info, List<TeamMessage> Answered);

    /// <summary>Answers (accept / decline) to one of our own requests or questions.</summary>
    private bool AnswersOurs(TeamMessage m) =>
        m.Kind is TeamMessageKind.Accept or TeamMessageKind.Decline && Memory.FindOutgoing(m.ReplyTo) != null;

    /// <summary>
    /// What the AI reads from the team inbox under the owner's switches: everything with auto-reply on; with it off,
    /// only answers to the AI's own requests (when it may make requests). Everything else is marked read.
    /// </summary>
    private bool Readable(TeamMessage m, PilotSettings settings) =>
        settings.Team.AutoReply || settings.Team.ProactiveRequests && AnswersOurs(m);

    private Inbox ReadInbox(GameSnapshot snapshot, PilotSettings settings)
    {
        _team.ExpireInbox(TimeSpan.FromMinutes(3));
        Memory.ExpireRequests(TimeSpan.FromMinutes(5));
        // Only real decision points say where we are; a half-built capture while waiting must not end a fight.
        if (snapshot.CanAct)
            Memory.NoteCombat(snapshot.Combat == null ? null : $"{snapshot.Run?.Floor}:{snapshot.Combat.EncounterName}", snapshot.Combat?.Round ?? 0);
        var unread = _team.PeekAiInbox();
        if (!snapshot.IsMultiplayer)
        {
            if (unread.Count > 0) _team.MarkRead(unread.Select(m => m.Id));
            return new(new(), new(), new(), new());
        }
        var ignored = unread.Where(m => !Readable(m, settings)).Select(m => m.Id).ToList();
        if (ignored.Count > 0)
        {
            _team.MarkRead(ignored);
            unread = unread.Where(m => Readable(m, settings)).ToList();
        }
        // Sort first: whether a "yes" needs us to act depends on the question it answers, which closing removes.
        var actionable = unread.Where(NeedsAttention).ToList();
        var info = unread.Where(m => !NeedsAttention(m)).ToList();
        var answered = new List<TeamMessage>();
        foreach (var m in unread.Where(m => m.Kind is TeamMessageKind.Accept or TeamMessageKind.Decline))
        {
            if (Memory.FindOutgoing(m.ReplyTo) is not { } mine) continue;
            answered.Add(mine);
            // "Yes" to our potion offer / question is now a promise to keep, even across the enemy turn.
            if (m.Kind == TeamMessageKind.Accept && mine.Kind is TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion)
                Memory.TrackAccepted(new TeamMessage
                {
                    Id = m.Id, From = m.From, FromName = m.FromName, FromAi = m.FromAi, Kind = TeamMessageKind.RequestPotion,
                    Potion = mine.Potion, Text = m.Text, Time = m.Time,
                }, whileWaiting: !snapshot.CanAct);
            Memory.CloseOutgoing(m.ReplyTo);
        }
        return new(unread, actionable, info, answered);
    }

    /// <summary>True when unread team messages ask this AI for a reaction (used to wake it while it cannot act).</summary>
    public bool HasActionableInbox(GameSnapshot snapshot)
    {
        var settings = _settings();
        if (!snapshot.IsMultiplayer) return false;
        return _team.PeekAiInbox().Any(m => Readable(m, settings) && NeedsAttention(m));
    }

    /// <summary>
    /// The owner's team switches applied to what the model wants to say: replies need auto-reply, the AI's own
    /// requests need "proactive requests", and no potion offers when giving potions is off. At most two messages.
    /// </summary>
    private static void FilterMessages(AgentDecision decision, GameSnapshot snapshot, PilotSettings settings)
    {
        if (!snapshot.IsMultiplayer)
        {
            decision.Messages.Clear();
            return;
        }
        decision.Messages.RemoveAll(m =>
        {
            bool reply = m.ReplyTo != null || m.Kind is TeamMessageKind.Accept or TeamMessageKind.Decline;
            if (reply) return !settings.Team.AutoReply;
            if (m.Kind == TeamMessageKind.Chat) return !settings.Team.AutoReply && !settings.Team.ProactiveRequests;
            if (!settings.Team.ProactiveRequests) return true;
            return !settings.Team.AllowGivingPotions && m.Kind is TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion;
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
        var inbox = ReadInbox(snapshot, settings);
        if (inbox.Actionable.Count == 0 || _clock() < _llmCooldownUntil) return null;
        var context = BuildContext(snapshot, settings, inbox.Actionable, inbox.Info, null);
        context.AnsweredOutgoing = inbox.Answered;
        context.ReplyOnlyReason = reason;
        try
        {
            var decision = await AskLlmAsync(snapshot, settings, context, ct, allowNoAction: true).ConfigureAwait(false);
            _consecutiveLlmFailures = 0;
            LastLlmError = null;
            Memory.SetMemo(decision.Memo);
            decision.ActionIds.Clear();
            decision.PlanKeys.Clear();
            decision.HandledInboxIds = inbox.All.Select(m => m.Id).ToList();
            FilterMessages(decision, snapshot, settings);
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
            if (++_consecutiveLlmFailures >= 3)
            {
                _consecutiveLlmFailures = 0;
                _llmCooldownUntil = _clock() + _cooldown;
                _cooldown = TimeSpan.FromTicks(Math.Min(_cooldown.Ticks * 2, TimeSpan.FromMinutes(10).Ticks));
            }
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
        var inbox = ReadInbox(snapshot, settings);
        var (unread, actionable, info) = (inbox.All, inbox.Actionable, inbox.Info);
        bool hasInstruction = !string.IsNullOrWhiteSpace(liveInstruction);

        // 1) Continue a plan from the previous LLM answer (unless something needs fresh attention).
        var scope = PlanScope(snapshot);
        if (actionable.Count > 0 || hasInstruction)
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
        var context = BuildContext(snapshot, settings, actionable, info, liveInstruction);
        context.AnsweredOutgoing = inbox.Answered;
        context.ReplyOnlyReason = replyOnlyReason;
        if (_clock() < _llmCooldownUntil && settings.Control.FallbackOnLlmFailure)
            return Fallback(snapshot, settings, "AI temporarily unavailable: " + LastLlmError);

        try
        {
            var decision = await AskLlmAsync(snapshot, settings, context, ct, allowNoAction: replyOnlyReason != null).ConfigureAwait(false);
            _consecutiveLlmFailures = 0;
            _cooldown = TimeSpan.FromSeconds(60);
            LastLlmError = null;
            PostProcess(decision, snapshot, settings, unread, scope);
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
            if (++_consecutiveLlmFailures >= 3)
            {
                _consecutiveLlmFailures = 0;
                // Without the fallback player there is nothing useful to do: stop instead of retrying (and paying) forever.
                if (!settings.Control.FallbackOnLlmFailure)
                    throw new PilotConfigException(L10n.T("status.llm_unavailable", ex.Message), ex);
                // Circuit breaker: stop paying a full timeout for every decision while the endpoint is down.
                _llmCooldownUntil = _clock() + _cooldown;
                _cooldown = TimeSpan.FromTicks(Math.Min(_cooldown.Ticks * 2, TimeSpan.FromMinutes(10).Ticks));
            }
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
        if (s.Screen == ScreenKind.CharacterSelect && CharacterRules(s, settings, _picks) is { } characterRule)
            return characterRule;
        if (s.Screen == ScreenKind.MainMenu && settings.Characters.Allowed.Count > 0 && s.Actions.Any(a => a.Kind == ActionKind.ContinueRun) &&
            (s.SavedRun is not { CharacterId.Length: > 0 } saved || !settings.Characters.Allowed.Contains(saved.CharacterId, StringComparer.OrdinalIgnoreCase)))
        {
            // Continuing would (or might: the save could not be read) play a character the owner did not allow;
            // abandoning the save is the human's call. The restriction is never silently widened.
            s.Actions.RemoveAll(a => a.Kind == ActionKind.ContinueRun);
            s.Notes.Add(L10n.T("status.saved_run_not_allowed", s.SavedRun?.CharacterName ?? s.SavedRun?.CharacterId ?? "?"));
        }
        if (!settings.Team.AllowGivingPotions)
        {
            // Hard rule: no potion (or throw) aimed at a teammate when the owner said so.
            s.Actions.RemoveAll(a => a.Kind == ActionKind.UsePotion && TargetsTeammate(a, s));
        }
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

    /// <summary>
    /// Character select as hard owner rules, whatever the pick policy: only allowed characters can be selected,
    /// embarking needs an allowed character, and the ascension setting is applied before embarking. Returns the
    /// rule's action, or null with the menu filtered.
    /// </summary>
    private static AgentDecision? CharacterRules(GameSnapshot s, PilotSettings settings, CharacterPickMemory picks)
    {
        AgentDecision Rule(ActionOption a, string why) => new() { Source = DecisionSource.Rule, ActionIds = { a.Id }, Thought = why };
        if (s.CharacterSelect is not { Characters.Count: > 0 } cs)
        {
            // Without the character list the restriction cannot be checked: never select or embark blindly.
            s.Actions.RemoveAll(a => a.Kind is ActionKind.SelectCharacter or ActionKind.Embark or ActionKind.SetAscension);
            return null;
        }
        var allowed = CharacterPicker.AllowedIds(cs, settings);
        if (settings.Characters.Allowed.Count > 0 && allowed.Count == 0)
            throw new PilotConfigException(L10n.T("status.no_allowed_character", string.Join(", ", settings.Characters.Allowed)));

        s.Actions.RemoveAll(a => a.Kind == ActionKind.SelectCharacter && !allowed.Contains(a.Arg(ActionArgs.Character) ?? "", StringComparer.OrdinalIgnoreCase));
        var current = cs.Characters.FirstOrDefault(c => c.Selected);
        bool currentAllowed = current != null && allowed.Contains(current.Id, StringComparer.OrdinalIgnoreCase);
        if (!currentAllowed)
        {
            s.Actions.RemoveAll(a => a.Kind is ActionKind.Embark or ActionKind.SetAscension);
        }
        else
        {
            int wanted = CharacterPicker.DesiredAscension(cs, settings);
            if (cs.Ascension != wanted &&
                s.Actions.FirstOrDefault(a => a.Kind == ActionKind.SetAscension && a.IntArg(ActionArgs.Ascension) == wanted) is { } asc)
                return Rule(asc, "(ascension setting)");
            // The owner sets the ascension; the model does not change it.
            s.Actions.RemoveAll(a => a.Kind == ActionKind.SetAscension);
        }
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

    /// <summary>The action to take without asking when the menu has exactly one choice (never discarding a potion).</summary>
    public static ActionOption? TrivialAction(GameSnapshot s) =>
        s.Actions.Count == 1 && s.Actions[0].Kind is not (ActionKind.DiscardPotion or ActionKind.UndoEndTurn) ? s.Actions[0] : null;

    private DecisionContext BuildContext(GameSnapshot s, PilotSettings settings, List<TeamMessage> inbox, List<TeamMessage> info, string? liveInstruction)
    {
        var ctx = new DecisionContext
        {
            LiveInstruction = liveInstruction,
            // Newest first matters; a flood of messages must not blow up the prompt.
            Inbox = inbox.TakeLast(MaxInboxInPrompt).ToList(),
            InboxInfo = info.TakeLast(MaxInfoInPrompt).ToList(),
            OpenRequests = Memory.OpenRequests,
            OutgoingRequests = Memory.OutgoingRequests,
            History = Memory.History,
            Memo = Memory.Memo,
        };
        if (s.Screen == ScreenKind.Map && s.Map is { } map)
        {
            var me = s.LocalPlayer;
            double hp = me is { MaxHp: > 0 } ? (double)me.Hp / me.MaxHp : 1;
            ctx.RouteSuggestions = RoutePlanner.Plan(map, settings.Route, hp, me?.Gold ?? 0, 3);
            ctx.PinnedNext = RoutePlanner.PinnedNext(map, settings.Route, s.Run?.Act ?? 0, s.Run?.Seed);
        }
        return ctx;
    }

    private ILlmClient Client(LlmProfile profile)
    {
        var key = JsonSerializer.Serialize(profile);
        if (_client == null || _clientKey != key)
        {
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

    private void PostProcess(AgentDecision decision, GameSnapshot snapshot, PilotSettings settings, List<TeamMessage> inbox, string scope)
    {
        Memory.SetMemo(decision.Memo);
        decision.HandledInboxIds = inbox.Select(m => m.Id).ToList();

        // Accepted requests are tracked by the loop once the reply was actually delivered (never in advisor mode).
        FilterMessages(decision, snapshot, settings);

        if (snapshot.Screen == ScreenKind.Combat && settings.Control.AllowTurnPlans && decision.PlanKeys.Count > 0)
            Memory.SetPlan(decision.PlanKeys, scope, PlanGuard.From(snapshot));
        else
            Memory.ClearPlan();
    }

    private static string Truncate(string? s, int max) => s == null ? "" : s.Length > max ? s[..max] + "..." : s;
}
