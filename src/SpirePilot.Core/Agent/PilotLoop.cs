using System.Text;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;
using SpirePilot.Core.Text;

namespace SpirePilot.Core.Agent;

public enum PilotState
{
    Off,
    Waiting,
    /// <summary>The current decision belongs to the human (mode restriction).</summary>
    HumanTurn,
    Thinking,
    Acting,
    Paused,
    /// <summary>Step mode: a decision is ready and waits for the user to press Step.</summary>
    StepReady,
    /// <summary>Advisor mode: a suggestion is shown.</summary>
    Advising,
    Error,
}

public sealed record PilotStatus(PilotState State, string Text);

public enum PilotEventKind
{
    Decision,
    Action,
    TeamMessage,
    Info,
    Error,
}

/// <summary>
/// One executed decision, for offline analysis and debugging: what was offered, what was chosen and by whom,
/// and what happened. Written to a JSONL file by the mod when enabled.
/// </summary>
public sealed class DecisionTrace
{
    public DateTime Time { get; init; } = DateTime.Now;
    public string Screen { get; init; } = "";
    public int? Floor { get; init; }
    public int? Round { get; init; }
    public string Source { get; init; } = "";
    public string Thought { get; init; } = "";
    public List<string> Offered { get; init; } = new();
    public string Chosen { get; init; } = "";
    public List<string> PlannedKeys { get; init; } = new();
    public bool Success { get; init; }
    public string Result { get; init; } = "";
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public double LatencySeconds { get; init; }
    public List<string> Messages { get; init; } = new();
}

public sealed class PilotEvent
{
    public DateTime Time { get; init; } = DateTime.Now;
    public PilotEventKind Kind { get; init; }
    public string Text { get; init; } = "";
    public AgentDecision? Decision { get; init; }
}

/// <summary>
/// The pilot's main loop: capture → decide → re-validate → execute, honouring the control mode, pause,
/// step and advisor modes, and run-start limits. Runs on a background task; all game access goes through
/// <see cref="IGameBridge"/>, which hops to the main thread.
/// </summary>
public sealed class PilotLoop : IDisposable
{
    private readonly IGameBridge _bridge;
    private readonly DecisionEngine _engine;
    private readonly TeamChatHub _team;
    private readonly Func<PilotSettings> _settings;
    private readonly IPilotLog _log;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _task;
    private string? _liveInstruction;
    private TaskCompletionSource<bool> _stepSignal = NewSignal();
    private int _consecutiveFailures;
    /// <summary>Recent "state fingerprint # action" pairs: the same pair three times among them means no progress.</summary>
    private readonly Queue<string> _recentSteps = new();
    private bool _runEndedSinceEnabled;
    /// <summary>The last run ended without a game over (save &amp; quit, disconnect) or was a co-op run: never restart it automatically.</summary>
    private bool _leftRunWithoutGameOver;
    private bool _wasInRun;
    private bool _lastRunMultiplayer;
    private PilotMode _lastMode = PilotMode.Off;
    private bool _wasPaused;
    private string? _runIdentity;
    private DateTime _nextReplyCheck = DateTime.MinValue;
    /// <summary>A fight the mode leaves to the human, and when it was last seen running (local unix ms).</summary>
    private (string Key, long SeenMs)? _humanFight;

    public PilotStatus Status { get; private set; } = new(PilotState.Off, "");
    public AgentDecision? Advice { get; private set; }
    public ActionOption? AdviceAction { get; private set; }
    public int RunsStarted { get; private set; }
    /// <summary>Runs started automatically after a previous one ended (what MaxAutoRuns limits); reset when switched on.</summary>
    public int AutoRunsStarted { get; private set; }
    public bool Paused { get; set; }

    /// <summary>Delays used by the loop; shortened by tests.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public event Action<PilotStatus>? StatusChanged;
    public event Action<PilotEvent>? EventLogged;
    /// <summary>Raised after every executed action (on the loop's worker thread).</summary>
    public event Action<DecisionTrace>? DecisionTraced;
    /// <summary>Raised after the pilot embarked on a new run (the mod persists the round-robin cursor).</summary>
    public event Action? RunStarted;
    /// <summary>
    /// Raised with the new run's seed when the loop sees a different run than before (started by anyone); run memory
    /// is already reset.
    /// </summary>
    public event Action<string>? NewRunDetected;

    public PilotLoop(IGameBridge bridge, DecisionEngine engine, TeamChatHub team, Func<PilotSettings> settings, IPilotLog log)
    {
        _bridge = bridge;
        _engine = engine;
        _team = team;
        _settings = settings;
        _log = log;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_task is { IsCompleted: false }) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _task = Task.Run(() => RunAsync(token));
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    /// <summary>Releases the step gate (step mode) once.</summary>
    public void Step()
    {
        lock (_lock) _stepSignal.TrySetResult(true);
    }

    /// <summary>One-off instruction for the next LLM decision (also cancels any pending plan).</summary>
    public void SetLiveInstruction(string? text)
    {
        lock (_lock) _liveInstruction = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        _engine.Memory.ClearPlan();
    }

    public string? LiveInstruction { get { lock (_lock) return _liveInstruction; } }

    /// <summary>The engine's run memory (history, memo, team requests), for the UI and tests.</summary>
    public AgentMemory Memory => _engine.Memory;

    /// <summary>Forget run memory (called by the mod when a new run starts or the user asks).</summary>
    public void ResetMemory() => _engine.Memory.Reset();

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (PilotConfigException ex)
            {
                // Bad key / URL / model: retrying cannot help. Pause and tell the user what to fix.
                Paused = true;
                _log.Warn("AI settings problem: " + ex.Message);
                SetStatus(PilotState.Error, L10n.T("status.config_error", ex.Message));
                Emit(PilotEventKind.Error, L10n.T("status.config_error", ex.Message));
            }
            catch (Exception ex)
            {
                _log.Error("Pilot loop error: " + ex);
                SetStatus(PilotState.Error, L10n.T("status.error", ex.Message));
                Emit(PilotEventKind.Error, ex.Message);
                await SafeDelay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        SetStatus(PilotState.Off, L10n.T("status.off"));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var settings = _settings();
        var mode = settings.Control.Mode;
        if (mode != _lastMode)
        {
            // Turning the pilot on (again) re-arms the "start one run" allowance.
            if (_lastMode == PilotMode.Off)
            {
                _runEndedSinceEnabled = false;
                _leftRunWithoutGameOver = false;
                _wasInRun = false;
                _lastRunMultiplayer = false;
                _runIdentity = null; // a run started while the pilot was off is not "new" to it
                AutoRunsStarted = 0;
            }
            _lastMode = mode;
            Advice = null;
            AdviceAction = null;
            // A plan made under another mode may be stale (the human may have played in between).
            _engine.Memory.ClearPlan();
            _engine.ResetCircuit();
        }
        if (mode == PilotMode.Off)
        {
            _engine.Memory.ClearPlan();
            SetStatus(PilotState.Off, L10n.T("status.off"));
            await SafeDelay(IdleDelay * 2, ct).ConfigureAwait(false);
            return;
        }
        if (Paused)
        {
            if (!_wasPaused)
            {
                // Resume starts fresh: no stale plan, no failure counts carried over.
                _wasPaused = true;
                _engine.Memory.ClearPlan();
                _consecutiveFailures = 0;
                _recentSteps.Clear();
            }
            // Keep showing why we stopped (error / stuck) until the user resumes.
            if (Status.State != PilotState.Error) SetStatus(PilotState.Paused, L10n.T("status.paused"));
            await SafeDelay(IdleDelay, ct).ConfigureAwait(false);
            return;
        }
        if (_wasPaused) _engine.ResetCircuit(); // resumed: the user may have fixed what made the model fail
        _wasPaused = false;

        var snap = await _bridge.CaptureAsync(ct).ConfigureAwait(false);
        if (snap != null)
        {
            TrackRunEnd(snap);
            NoteHumanFight(snap, mode);
        }
        if (snap == null || !snap.CanAct || snap.Actions.Count == 0)
        {
            SetStatus(PilotState.Waiting, L10n.T("status.waiting", snap?.WaitReason ?? "…"));
            // Nothing to play right now, but a teammate may be asking something: answer them.
            if (snap != null && await TryReplyOnlyAsync(snap, mode, settings, ct).ConfigureAwait(false)) return;
            await SafeDelay(IdleDelay, ct).ConfigureAwait(false);
            return;
        }
        // Turn already ended in co-op: "undo end turn" is only worth considering when a teammate needs us.
        string? replyOnly = null;
        if (snap.Actions.All(a => a.Kind == ActionKind.UndoEndTurn))
        {
            if (mode == PilotMode.Advisor || LiveInstruction == null && !_engine.HasActionableInbox(snap))
            {
                SetStatus(PilotState.Waiting, L10n.T("status.waiting", snap.WaitReason ?? "teammates"));
                await SafeDelay(IdleDelay, ct).ConfigureAwait(false);
                return;
            }
            replyOnly = DecisionEngine.UndoOnlyReason;
        }
        DetectNewRun(snap);
        if (!ModeAllows(mode, snap) || !MenuAllowed(snap, settings))
        {
            // The human acts now; whatever the AI planned is no longer trustworthy.
            _engine.Memory.ClearPlan();
            SetStatus(PilotState.HumanTurn, L10n.T("status.waiting", snap.Screen.ToString()));
            await SafeDelay(IdleDelay * 2, ct).ConfigureAwait(false);
            return;
        }

        SetStatus(PilotState.Thinking, L10n.T("status.thinking"));
        var instruction = LiveInstruction;
        // Taken before the engine applies owner rules to this snapshot's menu: fresh captures are unfiltered, so a
        // fingerprint of the filtered snapshot would never match and advisor / step waits would end at once.
        var stateFingerprint = Fingerprint(snap);
        // The pin as it was when the model started; a pin the owner sets while it thinks takes over the move.
        var pinnedBefore = snap.Map is { } mapBefore
            ? Route.RoutePlanner.PinnedNext(mapBefore, settings.Route, snap.Run?.Act ?? 0, snap.Run?.Seed)
            : null;
        AgentDecision? decision;
        // The engine filters the menu by owner rules: give it its own lists, and take the action from them.
        var menu = snap.WithOwnMenu();
        // Pausing or switching modes while the model thinks cancels the call: the human may already be playing.
        using (var decideCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var watcher = WatchForInterruptAsync(mode, decideCts);
            try
            {
                decision = await _engine.DecideAsync(menu, instruction, decideCts.Token, replyOnly).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _engine.Memory.ClearPlan();
                Emit(PilotEventKind.Info, "decision cancelled (paused or mode changed)");
                return;
            }
            finally
            {
                decideCts.Cancel();
                try { await watcher.ConfigureAwait(false); } catch (Exception) { /* watcher only */ }
            }
        }
        if (Interrupted(mode))
        {
            _engine.Memory.ClearPlan();
            return;
        }
        if (decision is { ActionIds.Count: 0 } talk)
        {
            // An answer without an action (allowed only when nothing needs doing).
            if (mode != PilotMode.Advisor) SendMessages(talk, snap, settings);
            _team.MarkRead(talk.HandledInboxIds);
            Emit(PilotEventKind.Decision, "💬 " + talk.Thought, talk);
            await SafeDelay(IdleDelay, ct).ConfigureAwait(false);
            return;
        }
        var action = decision?.FirstActionId is { } id ? menu.FindAction(id) : null;
        if (decision == null || action == null)
        {
            // Nothing the owner's rules allow here (e.g. no allowed character can be selected): wait for a change.
            SetStatus(PilotState.Waiting, L10n.T("status.waiting", snap.Screen.ToString()));
            await SafeDelay(IdleDelay * 2, ct).ConfigureAwait(false);
            return;
        }
        if (decision.Source is DecisionSource.Llm or DecisionSource.Fallback)
            Emit(PilotEventKind.Decision, DescribeDecision(decision, action), decision);

        if (mode == PilotMode.Advisor)
        {
            // The human plays in advisor mode: never speak for them; show suggested messages as part of the advice.
            Advice = decision;
            AdviceAction = action;
            var advice = action.Label + (decision.Messages.Count > 0 ? "  💬 " + string.Join(" / ", decision.Messages.Select(m => m.Text)) : "");
            SetStatus(PilotState.Advising, L10n.T("status.advice", advice));
            await WaitForChangeAsync(stateFingerprint, waitForStep: false, ct).ConfigureAwait(false);
            // The advice was for that state only: never leave it executable in the next one.
            Advice = null;
            AdviceAction = null;
            _engine.Memory.ClearPlan();
            return;
        }

        if (settings.Control.StepMode)
        {
            SetStatus(PilotState.StepReady, L10n.T("status.step_ready", action.Label));
            bool stepped = await WaitForChangeAsync(stateFingerprint, waitForStep: true, ct).ConfigureAwait(false);
            if (!stepped)
            {
                _engine.Memory.ClearPlan();
                return;
            }
        }

        // The LLM may have taken a while: make sure the action still applies to the current state. A teammate's
        // animation or a settling screen is a short pause, not a reason to throw the decision away.
        var fresh = await _bridge.CaptureAsync(ct).ConfigureAwait(false);
        var settleUntil = DateTime.UtcNow + TransientWait;
        while (fresh is { CanAct: false } && IsTransient(fresh.WaitReason) && DateTime.UtcNow < settleUntil && !Interrupted(mode))
        {
            await SafeDelay(TimeSpan.FromMilliseconds(150), ct).ConfigureAwait(false);
            fresh = await _bridge.CaptureAsync(ct).ConfigureAwait(false);
        }
        var target = fresh is { CanAct: true } ? Revalidate(action, snap, fresh) : null;
        if (fresh == null || target == null)
        {
            _engine.Memory.ClearPlan();
            Emit(PilotEventKind.Info, "state changed before acting; deciding again");
            return;
        }
        // Last check before anything leaves the machine: the user may have paused or handed this screen back.
        settings = _settings();
        if (Interrupted(mode) || !ModeAllows(mode, fresh) || !MenuAllowed(fresh, settings))
        {
            _engine.Memory.ClearPlan();
            return;
        }
        // The owner's hard rules as they are now: one switched on while the model thought takes the action away.
        if (OwnerRules.Filtered(fresh, settings).Actions.All(a => a.Id != target.Id))
        {
            _engine.Memory.ClearPlan();
            Emit(PilotEventKind.Info, "owner rules changed; deciding again");
            return;
        }

        // A route pinned while the model was thinking wins — but never over a live instruction, which the pin
        // (a soft rule) always yields to; an unchanged pin was already weighed by the engine.
        if (target.Kind == ActionKind.ChooseMapNode && instruction == null && fresh.Map is { } map &&
            Route.RoutePlanner.PinnedNext(map, settings.Route, fresh.Run?.Act ?? 0, fresh.Run?.Seed) is { } pinned &&
            pinned != pinnedBefore && pinned != target.Arg(ActionArgs.Node) &&
            fresh.Actions.FirstOrDefault(a => a.Kind == ActionKind.ChooseMapNode && a.Arg(ActionArgs.Node) == pinned) is { } pinnedMove)
        {
            Emit(PilotEventKind.Info, "pinned route overrides the model's map choice");
            target = pinnedMove;
        }
        var sentMessages = decision.Messages.Select(m => $"{DecisionParser.KindToWire(m.Kind)}→{m.To}: {m.Text}").ToList();
        SendMessages(decision, fresh, settings);
        if (target.Kind == ActionKind.ChooseMapNode && fresh.IsMultiplayer && settings.Route.PingChoiceToTeammates &&
            target.Arg(ActionArgs.Node) is { } node)
        {
            try { _bridge.PingMapNode(node); } catch (Exception ex) { _log.Warn("map ping failed: " + ex.Message); }
        }

        // No-progress guard: the same action on the same state a third time within the last few steps means we are
        // stuck, whether it repeats directly (A, A, A) or in a cycle (A, B, A, B, A).
        var repeatKey = Fingerprint(fresh) + "#" + (target.SemanticKey.Length > 0 ? target.SemanticKey : target.Label);
        int seen = _recentSteps.Count(k => k == repeatKey);
        _recentSteps.Enqueue(repeatKey);
        while (_recentSteps.Count > 8) _recentSteps.Dequeue();
        if (seen >= 2)
        {
            Paused = true;
            _recentSteps.Clear();
            _engine.Memory.ClearPlan();
            SetStatus(PilotState.Error, L10n.T("status.stuck", target.Label));
            Emit(PilotEventKind.Error, L10n.T("status.stuck", target.Label));
            return;
        }

        SetStatus(PilotState.Acting, L10n.T("status.acting", target.Label));
        var result = await _bridge.ExecuteAsync(target, ct).ConfigureAwait(false);
        _engine.Memory.AddHistory(HistoryLine(fresh, target, result, decision.Source));
        // Taking the turn back is only the first step of helping: the requests it was taken for become promises the
        // next decision (with the full menu) sees as "accepted earlier" — not unread mail that could trigger another undo.
        bool undid = target.Kind == ActionKind.UndoEndTurn && result.Success;
        if (undid)
        {
            var handled = decision.HandledInboxIds.ToHashSet();
            foreach (var m in _team.PeekAiInbox().Where(m => handled.Contains(m.Id) && DecisionEngine.IsActionable(m) && IsPromiseForUs(m)))
                _engine.Memory.TrackAccepted(m);
        }
        _team.MarkRead(decision.HandledInboxIds);
        if (instruction != null && decision.Source == DecisionSource.Llm && result.Success && !undid)
        {
            // The model saw the instruction and its decision got executed: consume it.
            lock (_lock) if (_liveInstruction == instruction) _liveInstruction = null;
        }
        if (result.Success)
        {
            // Bookkeeping first, so observers of the events below see a consistent memory.
            if (target.Kind == ActionKind.UsePotion && DecisionEngine.TargetsTeammate(target, fresh) &&
                TargetPlayer(target) is { } helpedPlayer)
                _engine.Memory.FulfilledPotionFor(helpedPlayer);
        }
        Emit(PilotEventKind.Action, (result.Success ? "✔ " : "✘ ") + target.Label + (result.Message.Length > 0 ? " — " + result.Message : ""), decision);
        Trace(fresh, target, decision, result, sentMessages);

        if (!result.Success)
        {
            _engine.Memory.ClearPlan();
            if (++_consecutiveFailures >= 3)
            {
                Paused = true;
                SetStatus(PilotState.Error, L10n.T("status.error", result.Message));
                _log.Warn("Pausing after repeated action failures: " + result.Message);
            }
        }
        else
        {
            _consecutiveFailures = 0;
            if (target.Kind == ActionKind.Embark)
            {
                RunsStarted++;
                if (_runEndedSinceEnabled) AutoRunsStarted++;
                _runEndedSinceEnabled = false;
                _engine.Memory.Reset();
                try { RunStarted?.Invoke(); } catch (Exception ex) { _log.Warn("run-start handler failed: " + ex.Message); }
            }
        }
        await SafeDelay(TimeSpan.FromMilliseconds(settings.Control.ActionDelayMs), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// While the pilot cannot act (turn ended, enemy turn, loading), answers teammates' requests with a talk-only
    /// LLM call. Never in advisor mode (the human speaks for themself) or on screens the mode leaves to the human.
    /// </summary>
    private async Task<bool> TryReplyOnlyAsync(GameSnapshot snap, PilotMode mode, PilotSettings settings, CancellationToken ct)
    {
        if (mode is PilotMode.Off or PilotMode.Advisor || Paused || !snap.IsMultiplayer || !settings.Team.AutoReply) return false;
        // Short waits (animations, settling) end in a real decision that will see the message anyway.
        if (!ModeAllows(mode, snap) || IsTransient(snap.WaitReason) || DateTime.UtcNow < _nextReplyCheck) return false;
        _nextReplyCheck = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        if (!_engine.HasActionableInbox(snap)) return false;

        SetStatus(PilotState.Thinking, L10n.T("status.thinking"));
        var reply = await _engine.ReplyOnlyAsync(snap, DecisionEngine.ReplyOnlyReason(snap) ?? "you cannot act right now", ct).ConfigureAwait(false);
        if (reply == null || Interrupted(mode)) return false;
        SendMessages(reply, snap, settings);
        _team.MarkRead(reply.HandledInboxIds);
        Emit(PilotEventKind.Decision, "💬 " + reply.Thought, reply);
        return true;
    }

    private static ulong? TargetPlayer(ActionOption a)
    {
        int at = a.SemanticKey.IndexOf("@P:", StringComparison.Ordinal);
        if (at < 0) return null;
        var digits = new string(a.SemanticKey[(at + 3)..].TakeWhile(char.IsDigit).ToArray());
        return ulong.TryParse(digits, out var id) ? id : null;
    }

    /// <summary>How long a finished decision waits for a transient busy state (animations, settling) to pass.</summary>
    public TimeSpan TransientWait { get; set; } = TimeSpan.FromSeconds(3);

    private static readonly string[] TransientReasons = { "settling", "animating", "card in play", "busy", "transition", "hand busy" };

    private static bool IsTransient(string? reason) =>
        reason != null && TransientReasons.Any(r => reason.StartsWith(r, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the user paused the pilot or changed the mode since <paramref name="mode"/> was read.</summary>
    private bool Interrupted(PilotMode mode) => Paused || _settings().Control.Mode != mode;

    private async Task WatchForInterruptAsync(PilotMode mode, CancellationTokenSource decide)
    {
        var token = decide.Token;
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(IdleDelay, token).ConfigureAwait(false);
            if (Interrupted(mode))
            {
                try { decide.Cancel(); } catch (ObjectDisposedException) { }
                return;
            }
        }
    }

    /// <summary>
    /// Teammates' combat requests that arrived during a fight the mode leaves to the human were the human's to answer,
    /// and are stale once that fight is over: the AI never answers them afterwards. Answers to our own requests,
    /// potion questions, route ideas and chat stay.
    /// </summary>
    private void NoteHumanFight(GameSnapshot snap, PilotMode mode)
    {
        var fight = DecisionEngine.FightKey(snap);
        if (fight != null && !ModeAllows(mode, snap))
        {
            _humanFight = (fight, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return;
        }
        if (_humanFight is not { } human || fight == human.Key) return;
        _humanFight = null;
        var stale = _team.PeekAiInbox().Where(m => m.Time <= human.SeenMs && DecisionEngine.IsCombatBound(m)).Select(m => m.Id).ToList();
        if (stale.Count > 0) _team.MarkRead(stale);
    }

    /// <summary>
    /// A different run (new seed, or the floor counter went back) means the memo, history and accepted
    /// requests belong to another run: forget them, whoever started it.
    /// </summary>
    private void DetectNewRun(GameSnapshot snap)
    {
        if (!snap.InRun || snap.Run is not { } run) return;
        var identity = !string.IsNullOrEmpty(run.Seed) ? "seed:" + run.Seed : null;
        if (identity == null) return;
        if (_runIdentity != null && _runIdentity != identity)
        {
            _engine.Memory.Reset();
            _log.Info("new run detected; run memory cleared");
            try { NewRunDetected?.Invoke(run.Seed!); } catch (Exception ex) { _log.Warn("new-run handler failed: " + ex.Message); }
        }
        _runIdentity = identity;
    }

    /// <summary>Which screens the current mode lets the AI handle.</summary>
    public static bool ModeAllows(PilotMode mode, GameSnapshot s)
    {
        // A finished combat room (only its Proceed button left) is out of combat: no fight is running.
        bool combatDecision = s.Screen is ScreenKind.Combat && s.Combat != null || s.Screen is ScreenKind.HandSelect ||
                              s.Combat != null && s.Screen is ScreenKind.ChooseOne or ScreenKind.CardGridSelect or ScreenKind.Popup or ScreenKind.Map;
        return mode switch
        {
            PilotMode.FullAuto or PilotMode.Advisor => true,
            PilotMode.CombatOnly => combatDecision,
            PilotMode.OutOfCombatOnly => !combatDecision && s.Screen is not (ScreenKind.MainMenu or ScreenKind.CharacterSelect),
            _ => false,
        };
    }

    /// <summary>
    /// Menus (start / continue a run) only in full auto. After a run ended, a new one is only started when
    /// auto-restart is on and the run limit is not reached.
    /// </summary>
    private bool MenuAllowed(GameSnapshot s, PilotSettings settings)
    {
        // Popups outside a run (unlock reveals, tutorial questions at character select) belong to the menus too.
        bool menu = s.Screen is ScreenKind.MainMenu or ScreenKind.CharacterSelect || s.Screen == ScreenKind.Popup && !s.InRun;
        if (!menu) return true;
        if (settings.Control.Mode is not (PilotMode.FullAuto or PilotMode.Advisor)) return false;
        if (!_runEndedSinceEnabled) return true;
        // The human quit or disconnected (or the run was co-op): starting or continuing a run is theirs to decide.
        if (_leftRunWithoutGameOver) return false;
        if (!settings.Control.AutoStartNextRun) return false;
        return settings.Control.MaxAutoRuns == 0 || AutoRunsStarted < settings.Control.MaxAutoRuns;
    }

    /// <summary>Notices the end of a run on every capture, even while paused or when the screen offers nothing.</summary>
    private void TrackRunEnd(GameSnapshot s)
    {
        if (s.InRun)
        {
            // A new stretch of play (continued or started by anyone): the last exit no longer blocks auto-restart.
            if (!_wasInRun) _leftRunWithoutGameOver = false;
            _wasInRun = true;
            _lastRunMultiplayer = s.IsMultiplayer;
        }
        if (s.Screen == ScreenKind.GameOver || s.GameOver != null)
        {
            _runEndedSinceEnabled = true;
            if (_lastRunMultiplayer || s.IsMultiplayer) _leftRunWithoutGameOver = true; // never follow a co-op run with a solo one
        }
        else if (_wasInRun && !s.InRun && s.Screen is ScreenKind.MainMenu or ScreenKind.CharacterSelect)
        {
            // Back at the menus without a game over screen: save & quit, a disconnect, or an abandoned run.
            if (!_runEndedSinceEnabled) _leftRunWithoutGameOver = true;
            _runEndedSinceEnabled = true;
        }
        if (!s.InRun && s.Screen is ScreenKind.MainMenu or ScreenKind.CharacterSelect) _wasInRun = false;
    }

    /// <summary>Finds the fresh-snapshot action that corresponds to <paramref name="action"/> from the old one.</summary>
    public static ActionOption? Revalidate(ActionOption action, GameSnapshot old, GameSnapshot fresh)
    {
        if (fresh.Sequence == old.Sequence) return fresh.FindAction(action.Id) ?? action;
        // Another screen opened while the model was thinking: whatever was chosen belongs to the old one.
        if (fresh.Screen != old.Screen || !string.Equals(fresh.ScreenName, old.ScreenName, StringComparison.Ordinal)) return null;
        if (!string.IsNullOrEmpty(action.SemanticKey))
            return fresh.Actions.FirstOrDefault(a => a.SemanticKey == action.SemanticKey);
        return fresh.Actions.FirstOrDefault(a => a.Kind == action.Kind && a.Label == action.Label &&
                                                 a.Args.Count == action.Args.Count && a.Args.All(kv => action.Arg(kv.Key) == kv.Value));
    }

    /// <summary>Fingerprint of a decision point, ignoring the sequence counter.</summary>
    public static string Fingerprint(GameSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Screen).Append('|').Append(s.ScreenName).Append('|').Append(s.CanAct).Append('|');
        // Screen content too: consecutive pages / popups with the same buttons are different states.
        if (s.Event is { } ev) sb.Append(ev.Title).Append('/').Append(ev.Body?.Length ?? 0).Append('/').Append(ev.Body?.GetHashCode() ?? 0).Append('|');
        if (s.Selection is { } sel) sb.Append(sel.Prompt).Append('/').Append(sel.SelectedKeys.Count).Append('|');
        foreach (var n in s.Notes) sb.Append(n.GetHashCode()).Append(',');
        if (s.LocalPlayer is { } me) sb.Append(me.Hp).Append(',').Append(me.Energy).Append(',').Append(me.Block).Append('|');
        if (s.Combat is { } c) sb.Append(c.Round).Append(',').Append(c.Hand.Count).Append('|');
        foreach (var a in s.Actions) sb.Append(a.SemanticKey.Length > 0 ? a.SemanticKey : a.Label).Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Waits until the situation changes (returns false) or, with <paramref name="waitForStep"/>, until the user
    /// presses Step (returns true).
    /// </summary>
    private async Task<bool> WaitForChangeAsync(string fingerprint, bool waitForStep, CancellationToken ct)
    {
        Task<bool> step;
        lock (_lock)
        {
            if (_stepSignal.Task.IsCompleted) _stepSignal = NewSignal();
            step = _stepSignal.Task;
        }
        while (!ct.IsCancellationRequested)
        {
            if (waitForStep && step.IsCompleted) return true;
            var settings = _settings();
            if (settings.Control.Mode == PilotMode.Off || Paused) return false;
            if (waitForStep && !settings.Control.StepMode) return true;
            if (!waitForStep && settings.Control.Mode != PilotMode.Advisor) return false;
            await Task.WhenAny(step, Task.Delay(IdleDelay, ct)).ConfigureAwait(false);
            var now = await _bridge.CaptureAsync(ct).ConfigureAwait(false);
            if (now == null || Fingerprint(now) != fingerprint) return false;
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }

    private static bool IsRequestKind(TeamMessageKind k) =>
        k is TeamMessageKind.RequestPotion or TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion or
            TeamMessageKind.RequestDebuff or TeamMessageKind.RequestFocus or TeamMessageKind.RequestBlock or TeamMessageKind.SuggestRoute;

    /// <summary>
    /// Remembered as our open request, so a teammate's answer finds its question: anything new we ask, and a potion offer
    /// or question also when it answers someone (a "yes" to it asks us to act).
    /// </summary>
    private static bool Tracked(TeamMessage m) =>
        IsRequestKind(m.Kind) && m.ReplyTo == null || m.Kind is TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion;

    /// <summary>A teammate's message whose acceptance is a promise for us to keep: not their offer to us, not an answer.</summary>
    private static bool IsPromiseForUs(TeamMessage m) =>
        m.Kind is not (TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion or TeamMessageKind.Accept or
            TeamMessageKind.Decline or TeamMessageKind.Hello);

    /// <summary>The player cannot act on a promise right now (turn ended, enemy turn): a per-round one then holds a round longer.</summary>
    private static bool Waiting(GameSnapshot s) => !s.CanAct || s.Actions.All(a => a.Kind == ActionKind.UndoEndTurn);

    private void SendMessages(AgentDecision decision, GameSnapshot snap, PilotSettings settings)
    {
        var inbox = _team.PeekAiInbox();
        bool waiting = Waiting(snap);
        foreach (var intent in decision.Messages)
        {
            var answered = intent.ReplyTo == null ? null : inbox.FirstOrDefault(m => m.Id == intent.ReplyTo);
            ulong to = answered != null ? TeamDelivery.ReplyRecipient(answered) : ResolveRecipient(intent.To, snap);
            if (IsRequestKind(intent.Kind) && intent.ReplyTo == null && _engine.Memory.HasOpenOutgoing(intent.Kind, to))
            {
                _log.Info("AI request not repeated (still open): " + intent.Text);
                continue;
            }
            var msg = new TeamMessage
            {
                Kind = intent.Kind,
                Text = intent.Text,
                Potion = intent.Potion,
                Debuff = intent.Debuff,
                Target = NormalizeTarget(intent.Target, snap),
                ReplyTo = intent.ReplyTo,
                To = to,
            };
            var sent = TeamDelivery.Send(_team, snap, msg, fromAi: true, _bridge.TrySignal);
            if (sent.RateLimited)
            {
                _log.Info("AI team message dropped by rate limit: " + intent.Text);
                _engine.Memory.AddHistory("(team message not sent: rate limit) " + intent.Text);
                continue;
            }
            if (sent.NoReader)
                _engine.Memory.AddHistory(sent.Emote != null ? "(teammate cannot read messages; showed an emote) " + intent.Text
                    : sent.EmoteLimited ? "(teammate cannot read messages; emote not shown: rate limit or shown recently) " + intent.Text
                    : "(teammate cannot read messages; not sent) " + intent.Text);
            else if (sent.Entry is { Delivered: false })
                _engine.Memory.AddHistory("(team message not delivered) " + intent.Text);
            if (sent.Reached) Remember(sent, answered, waiting);
            Emit(PilotEventKind.TeamMessage, (sent.Entry is { Delivered: true } ? "💬 " : "💬✘ ") + intent.Text);
            if (sent.Entry != null && settings.Team.SpeechBubbles)
            {
                try { _bridge.ShowSpeech(snap.LocalPlayerId, intent.Text); } catch (Exception) { /* visual only */ }
            }
        }
        decision.Messages.Clear();
    }

    /// <summary>What a message someone saw commits us to: an accepted request is a promise, our request waits for its answer.</summary>
    private void Remember(Delivery sent, TeamMessage? answered, bool waiting)
    {
        var m = sent.Message;
        if (m.Kind == TeamMessageKind.Accept && answered != null)
        {
            if (IsPromiseForUs(answered)) _engine.Memory.TrackAccepted(answered, waiting);
            else if (answered.Kind is TeamMessageKind.OfferPotion or TeamMessageKind.AskNeedPotion)
                _engine.Memory.AddHistory($"(you said yes to {answered.FromName}'s potion offer: they use it on you, not you on them)");
        }
        if (Tracked(m)) _engine.Memory.TrackOutgoing(m, waiting, signalled: sent.Emote != null);
    }

    /// <summary>
    /// A team message for this player that the pilot did not write: the owner's Team tab, or an external agent's POST
    /// /chat. It goes out by the same rule as the AI's own (text to teammates with SpirePilot, the vanilla emote to the
    /// others) and the AI remembers it like its own: a request stays open so the teammate's "yes" reaches the AI, an
    /// answer is not given a second time, and the history says what was said.
    /// </summary>
    /// <param name="snap">A recent capture (co-op flag, roster, whether the player can act).</param>
    /// <param name="reads">Who runs SpirePilot by the live roster, which may be newer than the capture.</param>
    public Delivery SendForPlayer(TeamMessage msg, GameSnapshot? snap, bool fromAi, Func<ulong, bool>? reads = null)
    {
        var answered = _team.FindReceived(msg.ReplyTo);
        var sent = TeamDelivery.Send(_team, snap, msg, fromAi, _bridge.TrySignal, reads);
        if (!sent.Reached) return sent;
        var m = sent.Message;
        Remember(sent, answered, snap != null && Waiting(snap));
        if (m.ReplyTo != null) _team.MarkRead(new[] { m.ReplyTo });
        var to = m.To == 0 ? "everyone"
            : snap?.Players.FirstOrDefault(p => p.NetId == m.To) is { } mate ? (mate.Name.Length > 0 ? mate.Name : mate.CharacterName)
            : m.To.ToString();
        _engine.Memory.AddHistory($"({(fromAi ? "sent via the API" : "owner")} → {PromptBuilder.Safe(to)}) {DecisionParser.KindToWire(m.Kind)}: {m.Text}");
        return sent;
    }

    /// <summary>
    /// Team messages name enemies by the game's combat id, which every client shares ("Cultist (cid:12)"); the
    /// model writes this client's key ("e1") or a name.
    /// </summary>
    public static string? NormalizeTarget(string? target, GameSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(target) || snap.Combat == null) return target;
        var t = target.Trim();
        if (t.Contains("cid:", StringComparison.Ordinal)) return t;
        var enemies = snap.Combat.Enemies.Where(e => e.IsAlive).ToList();
        var enemy = enemies.FirstOrDefault(e => e.Key.Equals(t, StringComparison.OrdinalIgnoreCase))
                    ?? enemies.FirstOrDefault(e => t.StartsWith(e.Key + " ", StringComparison.OrdinalIgnoreCase))
                    ?? enemies.FirstOrDefault(e => e.Name.Equals(t, StringComparison.OrdinalIgnoreCase));
        if (enemy == null) return t;
        return enemy.CombatId is { } cid ? $"{enemy.Name} (cid:{cid})" : enemy.Name;
    }

    /// <summary>"all" → 0; a teammate's name (case-insensitive, prefix allowed) or net id → that id.</summary>
    public static ulong ResolveRecipient(string? to, GameSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(to)) return 0;
        to = to.Trim();
        if (to.Equals("all", StringComparison.OrdinalIgnoreCase) || to is "everyone" or "所有人" or "全体") return 0;
        if (ulong.TryParse(to, out var id) && snap.Players.Any(p => p.NetId == id)) return id;
        var mates = snap.Teammates.ToList();
        // The prompt shows names through PromptBuilder.Safe ("[CN]Bob" → "(CN)Bob"): compare in that form too.
        var wanted = PromptBuilder.Safe(to);
        string Norm(PlayerInfo p) => PromptBuilder.Safe(p.Name);
        var exact = mates.FirstOrDefault(p => Norm(p).Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.NetId;
        var partial = mates.FirstOrDefault(p => Norm(p).Length > 0 &&
                                                (Norm(p).StartsWith(wanted, StringComparison.OrdinalIgnoreCase) ||
                                                 wanted.Contains(Norm(p), StringComparison.OrdinalIgnoreCase)));
        if (partial != null) return partial.NetId;
        var byCharacter = mates.FirstOrDefault(p => p.CharacterName.Equals(to, StringComparison.OrdinalIgnoreCase) ||
                                                    p.CharacterId.Equals(to, StringComparison.OrdinalIgnoreCase));
        return byCharacter?.NetId ?? 0;
    }

    private static string HistoryLine(GameSnapshot s, ActionOption a, ActionResult r, DecisionSource source)
    {
        var where = s.Combat != null ? $"F{s.Run?.Floor} R{s.Combat.Round}" : $"F{s.Run?.Floor} {s.Screen}";
        var who = source switch
        {
            DecisionSource.Fallback => " [fallback]",
            DecisionSource.Rule => " [owner rule]",
            DecisionSource.Trivial => " [auto]",
            _ => "",
        };
        return $"{where}: {a.Label}{who}" + (r.Success ? "" : $" (FAILED: {r.Message})");
    }

    private static string DescribeDecision(AgentDecision d, ActionOption a)
    {
        var src = d.Source == DecisionSource.Fallback ? "[fallback] " : "";
        var plan = d.ActionIds.Count > 1 ? $" (+{d.ActionIds.Count - 1} planned)" : "";
        var tokens = d.InputTokens is { } i ? $" [{i}/{d.OutputTokens} tok, {d.Elapsed.TotalSeconds:0.0}s]" : "";
        return $"{src}{a.Label}{plan} — {d.Thought}{tokens}";
    }

    private void Trace(GameSnapshot snap, ActionOption chosen, AgentDecision decision, ActionResult result, List<string> messages)
    {
        if (DecisionTraced == null) return;
        try
        {
            DecisionTraced.Invoke(new DecisionTrace
            {
                Screen = snap.Screen.ToString(),
                Floor = snap.Run?.Floor,
                Round = snap.Combat?.Round,
                Source = decision.Source.ToString(),
                Thought = decision.Thought,
                Offered = snap.Actions.Select(a => $"{a.Id} | {a.Label} | {a.SemanticKey}").ToList(),
                Chosen = $"{chosen.Label} | {chosen.SemanticKey}",
                PlannedKeys = decision.PlanKeys.ToList(),
                Success = result.Success,
                Result = result.Message,
                InputTokens = decision.InputTokens,
                OutputTokens = decision.OutputTokens,
                LatencySeconds = Math.Round(decision.Elapsed.TotalSeconds, 2),
                Messages = messages,
            });
        }
        catch (Exception ex) { _log.Warn("trace handler failed: " + ex.Message); }
    }

    private void SetStatus(PilotState state, string text)
    {
        var status = new PilotStatus(state, text);
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(status);
    }

    private void Emit(PilotEventKind kind, string text, AgentDecision? decision = null)
    {
        try { EventLogged?.Invoke(new PilotEvent { Kind = kind, Text = text, Decision = decision }); }
        catch (Exception ex) { _log.Warn("event handler failed: " + ex.Message); }
    }

    private static async Task SafeDelay(TimeSpan delay, CancellationToken ct)
    {
        if (delay <= TimeSpan.Zero) return;
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
