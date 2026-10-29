using System.Globalization;
using Godot;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Infra;

namespace SpirePilot.Ui.Pages;

/// <summary>Tab 1: status, mode, pause / step, advisor suggestion, one-shot instruction, situation, behaviour, stats.</summary>
internal sealed class ControlPage : Page
{
    private static readonly PilotMode[] Modes =
        { PilotMode.Off, PilotMode.FullAuto, PilotMode.CombatOnly, PilotMode.OutOfCombatOnly, PilotMode.Advisor };

    private StyleBoxFlat _dotStyle = null!;
    private Label _stateLabel = null!;
    private Label _statusText = null!;
    private Label _lastEvent = null!;
    private readonly Dictionary<PilotMode, Button> _modeButtons = new();
    private Label _modeTip = null!;
    private Button _pause = null!;
    private Button _step = null!;

    private VBoxContainer _adviceCard = null!;
    private Label _adviceAction = null!;
    private Label _adviceThought = null!;
    private Button _execute = null!;
    private Label _execResult = null!;
    private bool _executing;

    private LineEdit _instruction = null!;
    private Label _pendingInstruction = null!;
    private Button _clearInstruction = null!;

    private Label _situation = null!;
    private Label _memo = null!;

    private CheckButton _stepMode = null!, _skipTrivial = null!, _turnPlans = null!, _fallback = null!, _autoNext = null!;
    private SpinBox _maxRuns = null!;
    private HSlider _delay = null!;
    private Label _delayValue = null!;

    private Label _stats = null!;
    private Label _runs = null!;
    private Label _lastError = null!;

    public override string TitleKey => "ui.tab.control";

    protected override void BuildContent()
    {
        // ---- status ----
        var status = UiKit.Card(Body, null);
        var dot = UiKit.Dot(UiTheme.Faint, out _dotStyle, 14);
        _stateLabel = UiKit.Label("", UiTheme.FontTitle, UiTheme.Text, wrap: false);
        _stateLabel.AddThemeFontOverride("font", UiTheme.Bold);
        _stateLabel.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        status.AddChild(UiKit.Row(dot, _stateLabel));
        _statusText = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text);
        status.AddChild(_statusText);
        _lastEvent = UiKit.Muted("");
        _lastEvent.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _lastEvent.AutowrapMode = TextServer.AutowrapMode.Off;
        _lastEvent.ClipText = true;
        status.AddChild(_lastEvent);

        // ---- mode ----
        var mode = UiKit.Card(Body, L10n.T("ui.ctl.mode"));
        var flow = UiKit.Flow();
        foreach (var m in Modes)
        {
            var captured = m;
            var b = UiKit.Button(L10n.T("ui.mode." + m), () => SetMode(captured), UiTheme.ButtonKind.Normal, L10n.T("ui.mode.tip." + m));
            b.ToggleMode = true;
            _modeButtons[m] = b;
            flow.AddChild(b);
        }
        mode.AddChild(flow);
        _modeTip = UiKit.Muted("");
        mode.AddChild(_modeTip);
        _pause = UiKit.Button(L10n.T("ui.ctl.pause"), TogglePause);
        _step = UiKit.Button(L10n.T("ui.ctl.step"), () => PilotRuntime.Loop.Step(), UiTheme.ButtonKind.Primary, L10n.T("ui.ctl.step_tip"));
        mode.AddChild(UiKit.Row(_pause, _step));

        // ---- advisor ----
        _adviceCard = UiKit.Card(Body, L10n.T("ui.ctl.advice"));
        _adviceAction = UiKit.Label("", UiTheme.FontBody, UiTheme.Gold);
        _adviceThought = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text);
        _execute = UiKit.Button(L10n.T("ui.ctl.execute"), ExecuteAdvice, UiTheme.ButtonKind.Primary);
        _execResult = UiKit.Muted("");
        _adviceCard.AddChild(_adviceAction);
        _adviceCard.AddChild(_adviceThought);
        _adviceCard.AddChild(UiKit.Row(_execute));
        _adviceCard.AddChild(_execResult);

        // ---- one-shot instruction ----
        var instr = UiKit.Card(Body, L10n.T("ui.ctl.instruction"));
        _instruction = UiKit.Line(L10n.T("ui.ctl.instruction_ph"), onSubmit: _ => SendInstruction(), where: "instruction");
        var send = UiKit.Button(L10n.T("ui.ctl.instruction_send"), SendInstruction, UiTheme.ButtonKind.Primary);
        instr.AddChild(UiKit.Row(_instruction, send));
        _pendingInstruction = UiKit.Muted("");
        _clearInstruction = UiKit.Button(L10n.T("ui.clear"), () => PilotRuntime.Loop.SetLiveInstruction(null), UiTheme.ButtonKind.Small);
        instr.AddChild(UiKit.Row(_pendingInstruction, _clearInstruction));

        // ---- situation ----
        var sit = UiKit.Card(Body, L10n.T("ui.ctl.situation"));
        _situation = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text);
        sit.AddChild(_situation);
        _memo = UiKit.Muted("");
        sit.AddChild(_memo);

        // ---- behaviour ----
        var opt = UiKit.Card(Body, L10n.T("ui.ctl.options"));
        _stepMode = UiKit.Switch(L10n.T("ui.ctl.step_mode"), on => Edit(s => s.Control.StepMode = on, now: true));
        _skipTrivial = UiKit.Switch(L10n.T("ui.ctl.skip_trivial"), on => Edit(s => s.Control.SkipTrivialDecisions = on));
        _turnPlans = UiKit.Switch(L10n.T("ui.ctl.turn_plans"), on => Edit(s => s.Control.AllowTurnPlans = on));
        _fallback = UiKit.Switch(L10n.T("ui.ctl.fallback"), on => Edit(s => s.Control.FallbackOnLlmFailure = on));
        _autoNext = UiKit.Switch(L10n.T("ui.ctl.auto_next"), on => Edit(s => s.Control.AutoStartNextRun = on));
        foreach (var c in new Control[] { _stepMode, _skipTrivial, _turnPlans, _fallback, _autoNext }) opt.AddChild(c);
        _maxRuns = UiKit.Spin(0, 999, 1, v => Edit(s => s.Control.MaxAutoRuns = (int)v), "max_runs");
        UiKit.Field(opt, L10n.T("ui.ctl.max_runs"), _maxRuns);
        _delay = UiKit.Slider(0, 5000, 50, v =>
        {
            UiKit.SetText(_delayValue, FormatMs(v));
            Edit(s => s.Control.ActionDelayMs = (int)v);
        }, "delay");
        _delayValue = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text, wrap: false);
        _delayValue.CustomMinimumSize = new Vector2(64, 0);
        _delayValue.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _delayValue.HorizontalAlignment = HorizontalAlignment.Right;
        var delayRow = UiKit.Row(_delay, _delayValue);
        UiKit.Field(opt, L10n.T("ui.ctl.delay"), delayRow);

        // ---- stats ----
        var stats = UiKit.Card(Body, L10n.T("ui.ctl.stats"));
        _stats = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text);
        _runs = UiKit.Muted("");
        _lastError = UiKit.Label("", UiTheme.FontSmall, UiTheme.Bad);
        stats.AddChild(_stats);
        stats.AddChild(_runs);
        stats.AddChild(_lastError);
        stats.AddChild(UiKit.Row(UiKit.Button(L10n.T("ui.ctl.reset_memory"), () =>
        {
            PilotRuntime.Loop.ResetMemory();
            UiKit.SetText(_memo, L10n.T("ui.ctl.memory_reset"));
        }, UiTheme.ButtonKind.Small)));
    }

    private static string FormatMs(double v) => ((int)v).ToString(CultureInfo.InvariantCulture) + " ms";

    public override void Sync(PilotSettings s)
    {
        UiKit.SetPressed(_stepMode, s.Control.StepMode);
        UiKit.SetPressed(_skipTrivial, s.Control.SkipTrivialDecisions);
        UiKit.SetPressed(_turnPlans, s.Control.AllowTurnPlans);
        UiKit.SetPressed(_fallback, s.Control.FallbackOnLlmFailure);
        UiKit.SetPressed(_autoNext, s.Control.AutoStartNextRun);
        UiKit.SetValue(_maxRuns, s.Control.MaxAutoRuns);
        _maxRuns.Editable = s.Control.AutoStartNextRun;
        UiKit.SetValue(_delay, s.Control.ActionDelayMs);
        UiKit.SetText(_delayValue, FormatMs(s.Control.ActionDelayMs));
    }

    public override void Refresh(bool slow)
    {
        var loop = PilotRuntime.Loop;
        var status = loop.Status;
        var mode = PilotRuntime.Settings.Control.Mode;

        var color = UiTheme.StateColor(status.State);
        if (_dotStyle.BgColor != color) _dotStyle.BgColor = color;
        UiKit.SetText(_stateLabel, L10n.T("ui.state." + status.State) + " · " + L10n.T("ui.mode." + mode));
        UiKit.SetColor(_stateLabel, color);
        UiKit.SetText(_statusText, status.Text);
        var last = Feeds.LastOf(PilotEventKind.Action, PilotEventKind.Decision, PilotEventKind.Error);
        UiKit.SetText(_lastEvent, last == null ? "" : L10n.T("ui.ctl.last_event", $"{last.Time:HH:mm:ss} {OneLine(last.Text)}"));

        foreach (var (m, b) in _modeButtons) UiKit.SetPressed(b, m == mode);
        UiKit.SetText(_modeTip, L10n.T("ui.mode.tip." + mode));
        UiKit.SetText(_pause, L10n.T(loop.Paused ? "ui.ctl.resume" : "ui.ctl.pause"));
        _pause.Disabled = mode == PilotMode.Off;
        var stepMode = PilotRuntime.Settings.Control.StepMode;
        _step.Visible = stepMode;
        _step.Disabled = status.State != PilotState.StepReady;

        // Advisor suggestion.
        var advisor = mode == PilotMode.Advisor;
        _adviceCard.GetParent<Control>().Visible = advisor;
        if (advisor)
        {
            var advice = loop.Advice;
            var action = loop.AdviceAction;
            if (action != null)
            {
                UiKit.SetText(_adviceAction, action.Label);
                UiKit.SetText(_adviceThought, advice?.Thought ?? "");
            }
            else
            {
                UiKit.SetText(_adviceAction, "");
                UiKit.SetText(_adviceThought, L10n.T("ui.ctl.advice_none"));
            }
            _execute.Disabled = action == null || _executing;
        }

        // Pending one-shot instruction.
        var pending = loop.LiveInstruction;
        UiKit.SetText(_pendingInstruction, L10n.T("ui.ctl.instruction_pending", pending ?? L10n.T("ui.none")));
        _clearInstruction.Visible = pending != null;

        // Stats.
        var engine = PilotRuntime.Engine;
        UiKit.SetText(_stats, L10n.T("ui.ctl.stats_line", engine.LlmCalls, Num(engine.TotalInputTokens), Num(engine.TotalOutputTokens), Num(engine.TotalCachedTokens)));
        UiKit.SetText(_runs, L10n.T("ui.ctl.runs_started", loop.RunsStarted));
        var err = engine.LastLlmError;
        UiKit.SetText(_lastError, string.IsNullOrEmpty(err) ? "" : L10n.T("ui.ctl.last_error", err));
        _lastError.Visible = !string.IsNullOrEmpty(err);

        if (slow)
        {
            UiKit.SetText(_situation, DescribeSituation(Snap));
            var memo = engine.Memory.Memo;
            if (!string.IsNullOrEmpty(memo)) UiKit.SetText(_memo, L10n.T("ui.ctl.memo", memo));
            else if (_memo.Text != L10n.T("ui.ctl.memory_reset")) UiKit.SetText(_memo, "");
        }
    }

    private static string Num(long n) => n >= 10_000 ? (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k" : n.ToString(CultureInfo.InvariantCulture);

    private static string OneLine(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 160 ? s[..160] + "…" : s;
    }

    private static void SetMode(PilotMode mode)
    {
        SettingsDraft.Flush();
        PilotRuntime.SetMode(mode);
    }

    private static void TogglePause()
    {
        var loop = PilotRuntime.Loop;
        if (loop.Paused) PilotRuntime.Resume();
        else loop.Paused = true;
    }

    private void SendInstruction()
    {
        var text = _instruction.Text.Trim();
        if (text.Length == 0) return;
        PilotRuntime.Loop.SetLiveInstruction(text);
        _instruction.Text = "";
        _instruction.ReleaseFocus();
    }

    private void ExecuteAdvice()
    {
        var action = PilotRuntime.Loop.AdviceAction;
        if (action == null || _executing) return;
        _executing = true;
        _execute.Disabled = true;
        UiKit.SetText(_execResult, L10n.T("ui.ctl.executing"));
        UiKit.SetColor(_execResult, UiTheme.Muted);
        var label = _execResult;
        Task.Run(async () =>
        {
            ActionResult result;
            try { result = await PilotRuntime.Bridge.ExecuteAsync(action, CancellationToken.None); }
            catch (Exception ex) { result = ActionResult.Fail(ex.Message); }
            MainThread.Post(() => UiKit.Guard("advice.result", () =>
            {
                _executing = false;
                if (!SceneUtil.Valid(label)) return;
                UiKit.SetText(label, result.Success
                    ? L10n.T("ui.ctl.exec_ok", action.Label)
                    : L10n.T("ui.ctl.exec_fail", result.Message));
                UiKit.SetColor(label, result.Success ? UiTheme.Good : UiTheme.Bad);
            }));
        });
    }

    private static string DescribeSituation(GameSnapshot? s)
    {
        if (s == null) return L10n.T("ui.ctl.no_snapshot");
        var lines = new List<string> { L10n.T("ui.ctl.screen", L10n.T("ui.screen." + s.Screen)) };
        if (s.Run is { } run) lines.Add(L10n.T("ui.ctl.run", run.Act, run.Floor, run.AscensionLevel));
        if (s.LocalPlayer is { } me && s.InRun)
        {
            lines.Add(L10n.T("ui.ctl.hp", me.Hp, me.MaxHp, me.Block, me.Gold));
            if (s.Combat is { } c)
            {
                lines.Add(L10n.T("ui.ctl.energy", me.Energy, me.MaxEnergy, c.Round, c.Hand.Count));
                var enemies = c.Enemies.Where(e => e.IsAlive).Select(e => $"{e.Name} {e.Hp}/{e.MaxHp}").ToList();
                if (enemies.Count > 0) lines.Add(L10n.T("ui.ctl.enemies", string.Join(", ", enemies)));
            }
        }
        if (s.IsMultiplayer) lines.Add(L10n.T("ui.ctl.multiplayer", s.Teammates.Count()));
        lines.Add(s.CanAct ? L10n.T("ui.ctl.can_act", s.Actions.Count) : L10n.T("ui.ctl.wait", s.WaitReason ?? "…"));
        return string.Join("\n", lines);
    }
}
