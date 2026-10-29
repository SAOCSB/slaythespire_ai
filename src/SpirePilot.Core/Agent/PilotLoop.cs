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
    private bool _runEndedSinceEnabled;
    private PilotMode _lastMode = PilotMode.Off;

    public PilotStatus Status { get; private set; } = new(PilotState.Off, "");
    public AgentDecision? Advice { get; private set; }
    public ActionOption? AdviceAction { get; private set; }
    public int RunsStarted { get; private set; }
    public bool Paused { get; set; }

    /// <summary>Delays used by the loop; shortened by tests.</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    public event Action<PilotStatus>? StatusChanged;
    public event Action<PilotEvent>? EventLogged;
    /// <summary>Raised after the pilot embarked on a new run (the mod persists the round-robin cursor).</summary>
    public event Action? RunStarted;

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
            if (_lastMode == PilotMode.Off) _runEndedSinceEnabled = false;
            _lastMode = mode;
            Advice = null;
            AdviceAction = null;
        }
        if (mode == PilotMode.Off)
        {
            SetStatus(PilotState.Off, L10n.T("status.off"));
            await SafeDelay(IdleDelay * 2, ct).ConfigureAwait(false);
            return;
        }
        if (Paused)
        {
            SetStatus(PilotState.Paused, L10n.T("status.paused"));
            await SafeDelay(IdleDelay, ct).ConfigureAwait(false);
            return;
        }

        var snap = await _bridge.CaptureAsync(ct).ConfigureAwait(false);
        if (snap == null || !snap.CanAct || snap.Actions.Count == 0)
        {
            SetStatus(PilotState.Waiting, L10n.T("status.waiting", snap?.WaitReason ?? "…"));
            await SafeDelay(IdleDelay, ct).ConfigureAwait(false);
            return;
        }
        if (snap.Screen == ScreenKind.GameOver) _runEndedSinceEnabled = true;
        if (!ModeAllows(mode, snap) || !MenuAllowed(snap, settings))
        {
            SetStatus(PilotState.HumanTurn, L10n.T("status.waiting", snap.Screen.ToString()));
            await SafeDelay(IdleDelay * 2, ct).ConfigureAwait(false);
            return;
        }

        SetStatus(PilotState.Thinking, L10n.T("status.thinking"));
        var instruction = LiveInstruction;
        var decision = await _engine.DecideAsync(snap, instruction, ct).ConfigureAwait(false);
        var action = decision?.FirstActionId is { } id ? snap.FindAction(id) : null;
        if (decision == null || action == null)
        {
            await SafeDelay(IdleDelay, ct).ConfigureAwait(false);
            return;
        }
        if (decision.Source is DecisionSource.Llm or DecisionSource.Fallback)
            Emit(PilotEventKind.Decision, DescribeDecision(decision, action), decision);

        if (mode == PilotMode.Advisor)
        {
            Advice = decision;
            AdviceAction = action;
            SetStatus(PilotState.Advising, L10n.T("status.advice", action.Label));
            if (decision.Messages.Count > 0) SendMessages(decision, snap, settings);
            await WaitForChangeAsync(snap, waitForStep: false, ct).ConfigureAwait(false);
            _engine.Memory.ClearPlan();
            return;
        }

        if (settings.Control.StepMode)
        {
            SetStatus(PilotState.StepReady, L10n.T("status.step_ready", action.Label));
            bool stepped = await WaitForChangeAsync(snap, waitForStep: true, ct).ConfigureAwait(false);
            if (!stepped)
            {
                _engine.Memory.ClearPlan();
                return;
            }
        }

        // The LLM may have taken a while: make sure the action still applies to the current state.
        var fresh = await _bridge.CaptureAsync(ct).ConfigureAwait(false);
        var target = fresh is { CanAct: true } ? Revalidate(action, snap, fresh) : null;
        if (fresh == null || target == null)
        {
            _engine.Memory.ClearPlan();
            Emit(PilotEventKind.Info, "state changed before acting; deciding again");
            return;
        }

        SendMessages(decision, fresh, settings);
        if (target.Kind == ActionKind.ChooseMapNode && fresh.IsMultiplayer && settings.Route.PingChoiceToTeammates &&
            target.Arg(ActionArgs.Node) is { } node)
        {
            try { _bridge.PingMapNode(node); } catch (Exception ex) { _log.Warn("map ping failed: " + ex.Message); }
        }

        SetStatus(PilotState.Acting, L10n.T("status.acting", target.Label));
        var result = await _bridge.ExecuteAsync(target, ct).ConfigureAwait(false);
        _engine.Memory.AddHistory(HistoryLine(fresh, target, result));
        _team.MarkRead(decision.HandledInboxIds);
        if (instruction != null)
        {
            // The instruction was used for a decision that got executed: consume it.
            lock (_lock) if (_liveInstruction == instruction) _liveInstruction = null;
        }
        Emit(PilotEventKind.Action, (result.Success ? "✔ " : "✘ ") + target.Label + (result.Message.Length > 0 ? " — " + result.Message : ""), decision);

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
                _runEndedSinceEnabled = false;
                _engine.Memory.Reset();
                RunStarted?.Invoke();
            }
        }
        await SafeDelay(TimeSpan.FromMilliseconds(settings.Control.ActionDelayMs), ct).ConfigureAwait(false);
    }

    /// <summary>Which screens the current mode lets the AI handle.</summary>
    public static bool ModeAllows(PilotMode mode, GameSnapshot s)
    {
        bool combatDecision = s.Screen is ScreenKind.Combat or ScreenKind.HandSelect ||
                              s.Combat != null && s.Screen is ScreenKind.ChooseOne or ScreenKind.CardGridSelect;
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
        if (s.Screen is not (ScreenKind.MainMenu or ScreenKind.CharacterSelect)) return true;
        if (settings.Control.Mode is not (PilotMode.FullAuto or PilotMode.Advisor)) return false;
        if (!_runEndedSinceEnabled) return true;
        if (!settings.Control.AutoStartNextRun) return false;
        return settings.Control.MaxAutoRuns == 0 || RunsStarted < settings.Control.MaxAutoRuns;
    }

    /// <summary>Finds the fresh-snapshot action that corresponds to <paramref name="action"/> from the old one.</summary>
    public static ActionOption? Revalidate(ActionOption action, GameSnapshot old, GameSnapshot fresh)
    {
        if (fresh.Sequence == old.Sequence) return fresh.FindAction(action.Id) ?? action;
        if (!string.IsNullOrEmpty(action.SemanticKey))
            return fresh.Actions.FirstOrDefault(a => a.SemanticKey == action.SemanticKey);
        return fresh.Actions.FirstOrDefault(a => a.Kind == action.Kind && a.Label == action.Label &&
                                                 a.Args.Count == action.Args.Count && a.Args.All(kv => action.Arg(kv.Key) == kv.Value));
    }

    /// <summary>Fingerprint of a decision point, ignoring the sequence counter.</summary>
    public static string Fingerprint(GameSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Screen).Append('|').Append(s.CanAct).Append('|');
        if (s.LocalPlayer is { } me) sb.Append(me.Hp).Append(',').Append(me.Energy).Append(',').Append(me.Block).Append('|');
        if (s.Combat is { } c) sb.Append(c.Round).Append(',').Append(c.Hand.Count).Append('|');
        foreach (var a in s.Actions) sb.Append(a.SemanticKey.Length > 0 ? a.SemanticKey : a.Label).Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Waits until the situation changes (returns false) or, with <paramref name="waitForStep"/>, until the user
    /// presses Step (returns true).
    /// </summary>
    private async Task<bool> WaitForChangeAsync(GameSnapshot snap, bool waitForStep, CancellationToken ct)
    {
        var fingerprint = Fingerprint(snap);
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

    private void SendMessages(AgentDecision decision, GameSnapshot snap, PilotSettings settings)
    {
        foreach (var intent in decision.Messages)
        {
            var msg = new TeamMessage
            {
                Kind = intent.Kind,
                Text = intent.Text,
                Potion = intent.Potion,
                Debuff = intent.Debuff,
                Target = intent.Target,
                ReplyTo = intent.ReplyTo,
                To = ResolveRecipient(intent.To, snap),
            };
            var entry = _team.Send(msg, fromAi: true);
            if (entry == null)
            {
                _log.Info("AI team message dropped by rate limit: " + intent.Text);
                continue;
            }
            Emit(PilotEventKind.TeamMessage, "💬 " + intent.Text);
            if (settings.Team.SpeechBubbles)
            {
                try { _bridge.ShowSpeech(snap.LocalPlayerId, intent.Text); } catch (Exception) { /* visual only */ }
            }
        }
        decision.Messages.Clear();
    }

    /// <summary>"all" → 0; a teammate's name (case-insensitive, prefix allowed) or net id → that id.</summary>
    public static ulong ResolveRecipient(string? to, GameSnapshot snap)
    {
        if (string.IsNullOrWhiteSpace(to)) return 0;
        to = to.Trim();
        if (to.Equals("all", StringComparison.OrdinalIgnoreCase) || to is "everyone" or "所有人" or "全体") return 0;
        if (ulong.TryParse(to, out var id) && snap.Players.Any(p => p.NetId == id)) return id;
        var mates = snap.Teammates.ToList();
        var exact = mates.FirstOrDefault(p => p.Name.Equals(to, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact.NetId;
        var partial = mates.FirstOrDefault(p => p.Name.StartsWith(to, StringComparison.OrdinalIgnoreCase) ||
                                                to.Contains(p.Name, StringComparison.OrdinalIgnoreCase) && p.Name.Length > 0);
        if (partial != null) return partial.NetId;
        var byCharacter = mates.FirstOrDefault(p => p.CharacterName.Equals(to, StringComparison.OrdinalIgnoreCase) ||
                                                    p.CharacterId.Equals(to, StringComparison.OrdinalIgnoreCase));
        return byCharacter?.NetId ?? 0;
    }

    private static string HistoryLine(GameSnapshot s, ActionOption a, ActionResult r)
    {
        var where = s.Combat != null ? $"F{s.Run?.Floor} R{s.Combat.Round}" : $"F{s.Run?.Floor} {s.Screen}";
        return $"{where}: {a.Label}" + (r.Success ? "" : $" (FAILED: {r.Message})");
    }

    private static string DescribeDecision(AgentDecision d, ActionOption a)
    {
        var src = d.Source == DecisionSource.Fallback ? "[fallback] " : "";
        var plan = d.ActionIds.Count > 1 ? $" (+{d.ActionIds.Count - 1} planned)" : "";
        var tokens = d.InputTokens is { } i ? $" [{i}/{d.OutputTokens} tok, {d.Elapsed.TotalSeconds:0.0}s]" : "";
        return $"{src}{a.Label}{plan} — {d.Thought}{tokens}";
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
