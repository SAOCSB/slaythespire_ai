using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

public class LifecycleRegressionTests
{
    /// <summary>Like the real GameBridge: every capture is a NEW snapshot object.</summary>
    private sealed class FreshBridge : IGameBridge
    {
        private readonly object _lock = new();
        public Func<GameSnapshot>? Factory;
        private long _seq = 100;
        public List<ActionOption> Executed { get; } = new();
        public int Captures;
        public Task<GameSnapshot?> CaptureAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                Captures++;
                var s = Factory?.Invoke();
                if (s != null) s.Sequence = ++_seq;
                return Task.FromResult(s);
            }
        }
        public Func<ActionOption, ActionResult> OnExecute = _ => ActionResult.Ok();
        public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken ct)
        {
            lock (_lock) Executed.Add(action);
            return Task.FromResult(OnExecute(action));
        }
        public void ShowSpeech(ulong playerNetId, string text) { }
        public void PingMapNode(string nodeKey) { }
    }

    private sealed class FixedClient : ILlmClient
    {
        public string Answer = "{\"thought\":\"t\",\"actions\":[\"a0\"]}";
        public int Calls;
        public string Describe => "fixed";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new LlmResponse { Text = Answer });
        }
    }

    private sealed class NullLog : IPilotLog
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m) { }
    }

    private static (PilotLoop, FreshBridge, FixedClient, PilotSettings) Make(PilotMode mode)
    {
        var settings = new PilotSettings();
        settings.Profiles[0].ApiKey = "k";
        settings.Control.Mode = mode;
        settings.Control.ActionDelayMs = 0;
        settings.Normalize();
        var bridge = new FreshBridge();
        var client = new FixedClient();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        return (loop, bridge, client, settings);
    }

    private static GameSnapshot CoopCombatWithTeammatePotion()
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.Combat, InRun = true, CanAct = true, IsMultiplayer = true, LocalPlayerId = 1,
            Run = new RunInfo { Act = 1, Floor = 3, Seed = "S" },
            Players =
            {
                new PlayerInfo { NetId = 1, Name = "Me", IsLocal = true, Hp = 50, MaxHp = 80 },
                new PlayerInfo { NetId = 2, Name = "Bob", Hp = 20, MaxHp = 80 },
            },
            Combat = new CombatInfo { Round = 1, IsPlayerTurn = true },
        };
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.EndTurn, Label = "End turn", SemanticKey = "end_turn" });
        s.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.UsePotion, Label = "Blood potion -> Bob", SemanticKey = "potion:BLOOD@P:2" });
        s.Actions.Add(new ActionOption { Id = "a2", Kind = ActionKind.UsePotion, Label = "Blood potion -> me", SemanticKey = "potion:BLOOD@P:1" });
        return s;
    }

    [Fact]
    public async Task Advisor_with_filtered_menu_calls_once()
    {
        var (loop, bridge, client, settings) = Make(PilotMode.Advisor);
        settings.Team.AllowGivingPotions = false;
        bridge.Factory = CoopCombatWithTeammatePotion;
        loop.Start();
        await Task.Delay(1500);
        loop.Stop();
        // A stable state should cost one call.
        Assert.True(client.Calls <= 1, "LLM calls on an unchanged state: " + client.Calls);
    }

    [Fact]
    public async Task Advisor_with_unfiltered_menu_calls_once()
    {
        var (loop, bridge, client, settings) = Make(PilotMode.Advisor);
        settings.Team.AllowGivingPotions = true;
        bridge.Factory = CoopCombatWithTeammatePotion;
        loop.Start();
        await Task.Delay(1500);
        loop.Stop();
        Assert.True(client.Calls <= 1, "LLM calls on an unchanged state: " + client.Calls);
    }

    private static GameSnapshot CharSelect()
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.CharacterSelect, InRun = false, CanAct = true,
            CharacterSelect = new CharacterSelectInfo
            {
                Ascension = 0, MaxAscension = 3,
                Characters = { new CharacterOption { Id = "IRONCLAD", Name = "Ironclad", Selected = true }, new CharacterOption { Id = "SILENT", Name = "Silent" } },
            },
        };
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.SelectCharacter, Label = "Silent", SemanticKey = "char:SILENT", Args = { [ActionArgs.Character] = "SILENT" } });
        for (int n = 1; n <= 3; n++)
            s.Actions.Add(new ActionOption { Id = "a" + n, Kind = ActionKind.SetAscension, Label = "Asc " + n, SemanticKey = "ascension:" + n, Args = { [ActionArgs.Ascension] = n.ToString() } });
        s.Actions.Add(new ActionOption { Id = "a4", Kind = ActionKind.Embark, Label = "Embark", SemanticKey = "embark" });
        return s;
    }

    [Fact]
    public async Task Step_mode_at_character_select_can_be_stepped()
    {
        var (loop, bridge, client, settings) = Make(PilotMode.FullAuto);
        settings.Control.StepMode = true;
        settings.Characters.Policy = CharacterPickPolicy.First;
        settings.Characters.Allowed = new List<string> { "IRONCLAD" };
        settings.Characters.Ascension = 0;
        bridge.Factory = CharSelect;
        var states = new List<PilotState>();
        loop.StatusChanged += st => { lock (states) states.Add(st.State); };
        loop.Start();
        await Task.Delay(300);
        loop.Step();
        await Task.Delay(500);
        loop.Stop();
        int ready;
        lock (states) ready = states.Count(x => x == PilotState.StepReady);
        Assert.True(bridge.Executed.Count == 1, $"executed {bridge.Executed.Count}, StepReady entered {ready} times");
    }

    [Fact]
    public async Task Pin_made_while_pilot_off_survives_switching_on()
    {
        var (loop, bridge, client, settings) = Make(PilotMode.FullAuto);
        int fired = 0;
        loop.NewRunDetected += _ => fired++;
        string seed = "RUN_A";
        bridge.Factory = () =>
        {
            var s = CoopCombatWithTeammatePotion();
            s.IsMultiplayer = false;
            s.Run!.Seed = seed;
            return s;
        };
        bridge.OnExecute = _ => ActionResult.Ok();
        loop.Start();
        await Task.Delay(200);
        settings.Control.Mode = PilotMode.Off;
        await Task.Delay(100);
        loop.Paused = false; // PilotRuntime unpauses when the mode is switched on
        seed = "RUN_B"; // human starts run B, pins a route there (PinnedSeed = RUN_B)
        settings.Route.PinnedPath = new List<string> { "0,0" };
        settings.Route.PinnedSeed = "RUN_B";
        settings.Control.Mode = PilotMode.FullAuto;
        await Task.Delay(300);
        loop.Stop();
        Assert.Equal(0, fired); // a run started while the pilot was off is not "new" to it
    }

    [Fact]
    public void Crystal_sphere_fallback_does_not_alternate_tools()
    {
        var settings = new PilotSettings();
        settings.Normalize();
        GameSnapshot Sphere(string current)
        {
            var s = new GameSnapshot { Screen = ScreenKind.Event, InRun = true, CanAct = true };
            if (current != "big") s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.ChooseOption, Label = "tool big", SemanticKey = "crystal:tool:big" });
            if (current != "small") s.Actions.Add(new ActionOption { Id = "a" + s.Actions.Count, Kind = ActionKind.ChooseOption, Label = "tool small", SemanticKey = "crystal:tool:small" });
            s.Actions.Add(new ActionOption { Id = "a" + s.Actions.Count, Kind = ActionKind.ChooseOption, Label = "cell 0,0", SemanticKey = "crystal:cell:0,0" });
            return s;
        }
        var first = FallbackPolicy.Choose(Sphere("small"), settings);
        var second = FallbackPolicy.Choose(Sphere("big"), settings);
        Assert.False(first!.SemanticKey.StartsWith("crystal:tool") && second!.SemanticKey.StartsWith("crystal:tool"),
            $"{first.SemanticKey} then {second!.SemanticKey}");
    }
}



public class LifecycleRegressionTests2
{
    private sealed class Bridge : IGameBridge
    {
        public volatile Func<GameSnapshot>? Factory;
        private long _seq;
        public List<ActionOption> Executed { get; } = new();
        public Task<GameSnapshot?> CaptureAsync(CancellationToken ct)
        {
            var s = Factory?.Invoke();
            if (s != null) s.Sequence = Interlocked.Increment(ref _seq);
            return Task.FromResult(s);
        }
        public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken ct)
        {
            lock (Executed) Executed.Add(action);
            return Task.FromResult(ActionResult.Ok());
        }
        public void ShowSpeech(ulong playerNetId, string text) { }
        public void PingMapNode(string nodeKey) { }
    }
    private sealed class Client : ILlmClient
    {
        public string Describe => "x";
        public Task<LlmResponse> CompleteAsync(LlmRequest r, CancellationToken ct) =>
            Task.FromResult(new LlmResponse { Text = "{\"thought\":\"t\",\"actions\":[\"a0\"]}" });
    }
    private sealed class Log : IPilotLog { public void Info(string m) { } public void Warn(string m) { } public void Error(string m) { } }

    [Fact]
    public async Task Switching_on_at_the_menu_after_a_run_allows_one_run()
    {
        var settings = new PilotSettings();
        settings.Profiles[0].ApiKey = "k";
        settings.Control.Mode = PilotMode.FullAuto;
        settings.Control.ActionDelayMs = 0;
        settings.Normalize();
        var bridge = new Bridge();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new Log(), _ => new Client());
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new Log()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        // 1) in a run, nothing to do
        bridge.Factory = () => new GameSnapshot { Screen = ScreenKind.Combat, InRun = true, CanAct = false, WaitReason = "enemy turn" };
        loop.Start();
        await Task.Delay(150);
        // 2) user switches the pilot off, quits to the menu
        settings.Control.Mode = PilotMode.Off;
        await Task.Delay(100);
        bridge.Factory = () =>
        {
            var s = new GameSnapshot { Screen = ScreenKind.MainMenu, InRun = false, CanAct = true };
            s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.StartNewRun, Label = "New run", SemanticKey = "menu:new_run" });
            return s;
        };
        // 3) user switches it back on to have it start a run
        settings.Control.Mode = PilotMode.FullAuto;
        await Task.Delay(400);
        loop.Stop();
        Assert.True(bridge.Executed.Count >= 1, "status: " + loop.Status.State + " " + loop.Status.Text);
    }
}
