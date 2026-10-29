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

/// <summary>
/// Turns a snapshot into a decision: pending plan steps and hard rules first (no LLM call), then the LLM with
/// validation and one corrective retry, then the rule-based fallback. Runs on a worker thread; never touches
/// game objects (only the immutable snapshot).
/// </summary>
public sealed class DecisionEngine
{
    private readonly Func<PilotSettings> _settings;
    private readonly TeamChatHub _team;
    private readonly IPilotLog _log;
    private readonly Func<LlmProfile, ILlmClient> _clientFactory;
    private ILlmClient? _client;
    private string? _clientKey;

    public AgentMemory Memory { get; } = new();
    /// <summary>Language name used for team messages ("Simplified Chinese" / "English").</summary>
    public Func<string> MessageLanguage { get; set; } = () => L10n.IsChinese ? "Simplified Chinese" : "English";

    /// <summary>Total tokens used this session (for the UI).</summary>
    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }
    public int LlmCalls { get; private set; }

    public DecisionEngine(Func<PilotSettings> settings, TeamChatHub team, IPilotLog log, Func<LlmProfile, ILlmClient>? clientFactory = null)
    {
        _settings = settings;
        _team = team;
        _log = log;
        _clientFactory = clientFactory ?? LlmClientFactory.Create;
    }

    /// <summary>Scope in which a multi-step plan stays valid: the same combat round.</summary>
    public static string PlanScope(GameSnapshot s) =>
        s.Screen == ScreenKind.Combat && s.Combat != null ? $"combat:{s.Run?.Floor}:{s.Combat.Round}" : $"{s.Screen}:{s.Sequence}";

    public async Task<AgentDecision?> DecideAsync(GameSnapshot snapshot, string? liveInstruction, CancellationToken ct)
    {
        if (snapshot.Actions.Count == 0) return null;
        var settings = _settings();
        Memory.MaxHistory = settings.Strategy.HistoryLength;
        _team.ExpireInbox(TimeSpan.FromMinutes(3));
        Memory.ExpireRequests(TimeSpan.FromMinutes(5));
        var inbox = settings.Team.AutoReply ? _team.PeekAiInbox() : new List<TeamMessage>();
        if (!settings.Team.AutoReply && _team.PeekAiInbox() is { Count: > 0 } ignored) _team.MarkRead(ignored.Select(m => m.Id));

        // 1) Continue a plan from the previous LLM answer (unless new team messages need attention).
        var scope = PlanScope(snapshot);
        if (inbox.Count == 0 && string.IsNullOrWhiteSpace(liveInstruction) && Memory.TakePlanStep(scope) is { } key)
        {
            var step = snapshot.Actions.FirstOrDefault(a => a.SemanticKey == key);
            if (step != null) return new AgentDecision { Source = DecisionSource.Plan, ActionIds = { step.Id }, Thought = "(plan)" };
            Memory.ClearPlan();
        }
        else if (inbox.Count > 0 || !string.IsNullOrWhiteSpace(liveInstruction))
        {
            Memory.ClearPlan();
        }

        // 2) Hard rules from settings.
        if (RuleDecision(snapshot, settings) is { } rule) return rule;

        // 3) Trivial decisions.
        if (settings.Control.SkipTrivialDecisions && inbox.Count == 0 && string.IsNullOrWhiteSpace(liveInstruction) && IsTrivial(snapshot))
            return new AgentDecision { Source = DecisionSource.Trivial, ActionIds = { snapshot.Actions[0].Id }, Thought = "(only option)" };

        // 4) Ask the LLM.
        var context = BuildContext(snapshot, settings, inbox, liveInstruction);
        try
        {
            var decision = await AskLlmAsync(snapshot, settings, context, ct).ConfigureAwait(false);
            PostProcess(decision, snapshot, settings, inbox, scope);
            return decision;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn($"LLM decision failed: {ex.Message}");
            if (!settings.Control.FallbackOnLlmFailure) throw;
            var fb = FallbackPolicy.Choose(snapshot, settings);
            if (fb == null) throw;
            return new AgentDecision { Source = DecisionSource.Fallback, ActionIds = { fb.Id }, Thought = "(fallback) " + ex.Message };
        }
    }

    private static AgentDecision? RuleDecision(GameSnapshot s, PilotSettings settings)
    {
        if (s.Screen == ScreenKind.CharacterSelect && settings.Characters.Policy != CharacterPickPolicy.AiChoice)
        {
            var pick = CharacterPicker.Choose(s, settings);
            if (pick != null) return new AgentDecision { Source = DecisionSource.Rule, ActionIds = { pick.Id }, Thought = "(character policy)" };
        }
        if (s.Screen == ScreenKind.Map && s.Map is { } map)
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

    private DecisionContext BuildContext(GameSnapshot s, PilotSettings settings, List<TeamMessage> inbox, string? liveInstruction)
    {
        var ctx = new DecisionContext
        {
            LiveInstruction = liveInstruction,
            Inbox = inbox,
            OpenRequests = Memory.OpenRequests,
            History = Memory.History,
            Memos = Memory.Memos,
        };
        if (s.Screen == ScreenKind.Map && s.Map is { } map)
        {
            var me = s.LocalPlayer;
            double hp = me is { MaxHp: > 0 } ? (double)me.Hp / me.MaxHp : 1;
            ctx.RouteSuggestions = RoutePlanner.Plan(map, settings.Route, hp, me?.Gold ?? 0, 3);
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
        var profile = settings.Active ?? throw new LlmException("No AI profile configured.");
        var problems = LlmClientFactory.Validate(profile);
        if (problems.Count > 0) throw new LlmException("AI profile incomplete: " + string.Join(", ", problems));

        var client = Client(profile);
        var formatter = new StateFormatter { VerboseCards = settings.Strategy.VerboseCards, IncludePiles = settings.Strategy.IncludePiles };
        var system = PromptBuilder.BuildSystem(settings, MessageLanguage());
        string? lastError = null;
        string? lastAnswer = null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            context.Feedback = lastError;
            var request = new LlmRequest
            {
                System = system,
                JsonSchema = DecisionParser.Schema,
                Messages = { new LlmMessage(LlmRole.User, PromptBuilder.BuildUser(snapshot, context, formatter)) },
            };
            if (settings.LogPrompts) _log.Info("PROMPT >>>\n" + request.Messages[0].Content);

            var response = await LlmHttp.WithRetries(() => client.CompleteAsync(request, ct), maxRetries: 2, ct).ConfigureAwait(false);
            LlmCalls++;
            TotalInputTokens += response.InputTokens ?? 0;
            TotalOutputTokens += response.OutputTokens ?? 0;
            lastAnswer = response.Text;
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
            lastError = parsed.Error + ". Answer again with one JSON object using only ids from the legal action list.";
            _log.Warn($"Unusable LLM answer ({parsed.Error}): {Truncate(lastAnswer, 300)}");
        }
        throw new LlmException("The model did not return a usable decision: " + lastError);
    }

    private void PostProcess(AgentDecision decision, GameSnapshot snapshot, PilotSettings settings, List<TeamMessage> inbox, string scope)
    {
        if (!string.IsNullOrWhiteSpace(decision.Memo)) Memory.AddMemo(decision.Memo!);
        decision.HandledInboxIds = inbox.Select(m => m.Id).ToList();

        foreach (var msg in decision.Messages)
        {
            if (msg.Kind == TeamMessageKind.Accept && msg.ReplyTo != null && inbox.FirstOrDefault(m => m.Id == msg.ReplyTo) is { } accepted)
                Memory.TrackAccepted(accepted);
        }
        // Team messages are only meaningful with teammates; drop them in single player.
        if (!snapshot.IsMultiplayer || !settings.Team.AutoReply) decision.Messages.Clear();
        else if (!settings.Team.ProactiveRequests)
            decision.Messages.RemoveAll(m => m.ReplyTo == null && m.Kind != TeamMessageKind.Chat);

        if (snapshot.Screen == ScreenKind.Combat && settings.Control.AllowTurnPlans && decision.PlanKeys.Count > 0)
            Memory.SetPlan(decision.PlanKeys, scope);
        else
            Memory.ClearPlan();
    }

    private static string Truncate(string? s, int max) => s == null ? "" : s.Length > max ? s[..max] + "..." : s;
}
