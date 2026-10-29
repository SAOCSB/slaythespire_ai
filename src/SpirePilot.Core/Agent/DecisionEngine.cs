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
        !(m.FromAi && m.Kind == TeamMessageKind.Chat);

    public async Task<AgentDecision?> DecideAsync(GameSnapshot snapshot, string? liveInstruction, CancellationToken ct)
    {
        if (snapshot.Actions.Count == 0) return null;
        var settings = _settings();
        Memory.MaxHistory = settings.Strategy.HistoryLength;
        _team.ExpireInbox(TimeSpan.FromMinutes(3));
        Memory.ExpireRequests(TimeSpan.FromMinutes(5));

        var unread = _team.PeekAiInbox();
        if (!settings.Team.AutoReply || !snapshot.IsMultiplayer)
        {
            if (unread.Count > 0) _team.MarkRead(unread.Select(m => m.Id));
            unread = new List<TeamMessage>();
        }
        var actionable = unread.Where(IsActionable).ToList();
        var info = unread.Where(m => !IsActionable(m)).ToList();
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
                return new AgentDecision { Source = DecisionSource.Plan, ActionIds = { step.Id }, Thought = "(plan)", HandledInboxIds = info.Select(m => m.Id).ToList() };
            Memory.ClearPlan();
            Memory.AddHistory("(plan stopped: planned action no longer available)");
        }
        else if (abort != null)
        {
            Memory.AddHistory($"(plan stopped: {abort})");
        }

        // 2) Rules from settings. Soft rules (pinned route / planner routing) yield to a live instruction or a
        //    teammate request, which the LLM must see; hard rules (character allow-list) always apply.
        if (RuleDecision(snapshot, settings, softRules: !hasInstruction && actionable.Count == 0) is { } rule)
        {
            rule.HandledInboxIds = info.Select(m => m.Id).ToList();
            return rule;
        }

        // 3) Trivial decisions.
        if (settings.Control.SkipTrivialDecisions && actionable.Count == 0 && !hasInstruction && IsTrivial(snapshot))
            return new AgentDecision { Source = DecisionSource.Trivial, ActionIds = { snapshot.Actions[0].Id }, Thought = "(only option)", HandledInboxIds = info.Select(m => m.Id).ToList() };

        // 4) Ask the LLM (unless it keeps failing and is cooling down).
        var context = BuildContext(snapshot, settings, actionable, info, liveInstruction);
        if (_clock() < _llmCooldownUntil && settings.Control.FallbackOnLlmFailure)
            return Fallback(snapshot, settings, "AI temporarily unavailable: " + LastLlmError, info);

        try
        {
            var decision = await AskLlmAsync(snapshot, settings, context, ct).ConfigureAwait(false);
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
                // Circuit breaker: stop paying a full timeout for every decision while the endpoint is down.
                _llmCooldownUntil = _clock() + _cooldown;
                _cooldown = TimeSpan.FromTicks(Math.Min(_cooldown.Ticks * 2, TimeSpan.FromMinutes(10).Ticks));
                _consecutiveLlmFailures = 0;
            }
            if (!settings.Control.FallbackOnLlmFailure) throw;
            // Teammate requests stay unread: the rule-based fallback cannot answer them, the LLM can once it is back.
            return Fallback(snapshot, settings, ex.Message, info);
        }
    }

    private AgentDecision Fallback(GameSnapshot snapshot, PilotSettings settings, string why, IEnumerable<TeamMessage> seen)
    {
        var fb = FallbackPolicy.Choose(snapshot, settings) ?? throw new LlmException("no fallback action: " + why);
        Memory.ClearPlan();
        return new AgentDecision
        {
            Source = DecisionSource.Fallback,
            ActionIds = { fb.Id },
            Thought = "(fallback) " + why,
            HandledInboxIds = seen.Select(m => m.Id).ToList(),
        };
    }

    private static AgentDecision? RuleDecision(GameSnapshot s, PilotSettings settings, bool softRules)
    {
        if (s.Screen == ScreenKind.CharacterSelect && s.CharacterSelect is { Characters.Count: > 0 } info &&
            settings.Characters.Allowed.Count > 0 && CharacterPicker.AllowedIds(info, settings).Count == 0)
            throw new PilotConfigException(L10n.T("status.no_allowed_character", string.Join(", ", settings.Characters.Allowed)));
        if (s.Screen == ScreenKind.CharacterSelect && settings.Characters.Policy != CharacterPickPolicy.AiChoice)
        {
            var pick = CharacterPicker.Choose(s, settings);
            if (pick != null) return new AgentDecision { Source = DecisionSource.Rule, ActionIds = { pick.Id }, Thought = "(character policy)" };
        }
        if (s.Screen == ScreenKind.CharacterSelect && settings.Characters.Policy == CharacterPickPolicy.AiChoice && s.CharacterSelect is { } cs)
        {
            // Hard rule as a menu filter: the model may only pick allowed characters.
            var allowed = CharacterPicker.AllowedIds(cs, settings);
            s.Actions.RemoveAll(a => a.Kind == ActionKind.SelectCharacter && !allowed.Contains(a.Arg(ActionArgs.Character) ?? "", StringComparer.OrdinalIgnoreCase));
            var current = cs.Characters.FirstOrDefault(c => c.Selected);
            if (current == null || !allowed.Contains(current.Id, StringComparer.OrdinalIgnoreCase))
                s.Actions.RemoveAll(a => a.Kind == ActionKind.Embark);
        }
        if (softRules && s.Screen == ScreenKind.Map && s.Map is { } map)
        {
            var me = s.LocalPlayer;
            double hp = me is { MaxHp: > 0 } ? (double)me.Hp / me.MaxHp : 1;
            var target = RoutePlanner.PinnedNext(map, settings.Route, s.Run?.Act ?? 0);
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

    /// <summary>One action, or only "proceed"/"dismiss" style actions.</summary>
    public static bool IsTrivial(GameSnapshot s)
    {
        if (s.Actions.Count == 1) return s.Actions[0].Kind != ActionKind.DiscardPotion;
        return s.Actions.All(a => a.Kind is ActionKind.Proceed or ActionKind.DismissPopup) ||
               s.Screen == ScreenKind.Popup && s.Actions.Count(a => a.Kind == ActionKind.DismissPopup) == 1;
    }

    private DecisionContext BuildContext(GameSnapshot s, PilotSettings settings, List<TeamMessage> inbox, List<TeamMessage> info, string? liveInstruction)
    {
        var ctx = new DecisionContext
        {
            LiveInstruction = liveInstruction,
            Inbox = inbox,
            InboxInfo = info,
            OpenRequests = Memory.OpenRequests,
            History = Memory.History,
            Memo = Memory.Memo,
        };
        if (s.Screen == ScreenKind.Map && s.Map is { } map)
        {
            var me = s.LocalPlayer;
            double hp = me is { MaxHp: > 0 } ? (double)me.Hp / me.MaxHp : 1;
            ctx.RouteSuggestions = RoutePlanner.Plan(map, settings.Route, hp, me?.Gold ?? 0, 3);
            ctx.PinnedNext = RoutePlanner.PinnedNext(map, settings.Route, s.Run?.Act ?? 0);
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

    private async Task<AgentDecision> AskLlmAsync(GameSnapshot snapshot, PilotSettings settings, DecisionContext context, CancellationToken ct)
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

            var parsed = DecisionParser.Parse(response.Text, snapshot);
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
            request.Messages.Add(new LlmMessage(LlmRole.User,
                $"Invalid answer: {parsed.Error}. Valid ids are {string.Join(", ", snapshot.Actions.Select(a => a.Id))}. Reply with the JSON object only."));
        }
        throw new LlmException("The model did not return a usable decision: " + lastError);
    }

    private void PostProcess(AgentDecision decision, GameSnapshot snapshot, PilotSettings settings, List<TeamMessage> inbox, string scope)
    {
        Memory.SetMemo(decision.Memo);
        decision.HandledInboxIds = inbox.Select(m => m.Id).ToList();

        foreach (var msg in decision.Messages)
        {
            if (msg.Kind == TeamMessageKind.Accept && msg.ReplyTo != null && inbox.FirstOrDefault(m => m.Id == msg.ReplyTo) is { } accepted)
                Memory.TrackAccepted(accepted);
        }
        // Team messages are only meaningful with teammates; respect the team settings.
        if (!snapshot.IsMultiplayer || !settings.Team.AutoReply) decision.Messages.Clear();
        else if (!settings.Team.ProactiveRequests)
            decision.Messages.RemoveAll(m => m.ReplyTo == null && m.Kind != TeamMessageKind.Chat);
        if (decision.Messages.Count > MaxMessagesPerDecision)
            decision.Messages.RemoveRange(MaxMessagesPerDecision, decision.Messages.Count - MaxMessagesPerDecision);

        if (snapshot.Screen == ScreenKind.Combat && settings.Control.AllowTurnPlans && decision.PlanKeys.Count > 0)
            Memory.SetPlan(decision.PlanKeys, scope, PlanGuard.From(snapshot));
        else
            Memory.ClearPlan();
    }

    private static string Truncate(string? s, int max) => s == null ? "" : s.Length > max ? s[..max] + "..." : s;
}
