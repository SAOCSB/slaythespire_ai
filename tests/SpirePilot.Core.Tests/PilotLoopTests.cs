using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

public class PilotLoopTests
{
    private sealed class FakeBridge : IGameBridge
    {
        private readonly object _lock = new();
        private GameSnapshot? _current;
        private long _seq = 100;
        public List<ActionOption> Executed { get; } = new();
        public Func<ActionOption, ActionResult> OnExecute { get; set; } = _ => ActionResult.Ok();
        public List<string> Pings { get; } = new();

        public void Set(GameSnapshot? s)
        {
            lock (_lock) _current = s;
        }

        public Task<GameSnapshot?> CaptureAsync(CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_current != null) _current.Sequence = ++_seq;
                return Task.FromResult(_current);
            }
        }

        public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken cancellationToken)
        {
            lock (_lock) Executed.Add(action);
            // After acting the game is busy until OnExecute (or the test) sets a new state.
            Set(new GameSnapshot { Screen = ScreenKind.Combat, CanAct = false, WaitReason = "animating" });
            return Task.FromResult(OnExecute(action));
        }

        public void ShowSpeech(ulong playerNetId, string text) { }
        public void PingMapNode(string nodeKey) => Pings.Add(nodeKey);
    }

    private sealed class FixedClient : ILlmClient
    {
        public string Answer { get; set; } = "{\"thought\":\"t\",\"actions\":[\"a1\"]}";
        public int Calls;
        public string Describe => "fixed";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new LlmResponse { Text = Answer });
        }
    }

    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    private static (PilotLoop loop, FakeBridge bridge, FixedClient client, PilotSettings settings) Make(PilotMode mode)
    {
        var settings = new PilotSettings();
        settings.Profiles[0].ApiKey = "k";
        settings.Control.Mode = mode;
        settings.Control.ActionDelayMs = 0;
        settings.Normalize();
        var bridge = new FakeBridge();
        var client = new FixedClient();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        return (loop, bridge, client, settings);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Full_auto_executes_the_llm_choice()
    {
        var (loop, bridge, _, _) = Make(PilotMode.FullAuto);
        bridge.Set(TestData.Combat());
        loop.Start();
        await Until(() => bridge.Executed.Count == 1);
        loop.Stop();
        Assert.Equal("play|Strike|e1", bridge.Executed[0].SemanticKey);
    }

    [Fact]
    public async Task Combat_only_leaves_the_map_to_the_human()
    {
        var (loop, bridge, client, _) = Make(PilotMode.CombatOnly);
        bridge.Set(new GameSnapshot
        {
            Screen = ScreenKind.Map, CanAct = true, Map = TestData.Map(),
            Actions = { new ActionOption { Id = "a0", Kind = ActionKind.ChooseMapNode, Args = { [ActionArgs.Node] = "0,0" } },
                        new ActionOption { Id = "a1", Kind = ActionKind.ChooseMapNode, Args = { [ActionArgs.Node] = "1,0" } } },
        });
        loop.Start();
        await Until(() => loop.Status.State == PilotState.HumanTurn);
        loop.Stop();
        Assert.Empty(bridge.Executed);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Advisor_suggests_without_acting()
    {
        var (loop, bridge, _, _) = Make(PilotMode.Advisor);
        bridge.Set(TestData.Combat());
        loop.Start();
        await Until(() => loop.Status.State == PilotState.Advising);
        loop.Stop();
        Assert.Empty(bridge.Executed);
        Assert.Equal("a1", loop.AdviceAction!.Id);
    }

    [Fact]
    public async Task Step_mode_waits_for_the_user()
    {
        var (loop, bridge, _, settings) = Make(PilotMode.FullAuto);
        settings.Control.StepMode = true;
        bridge.Set(TestData.Combat());
        loop.Start();
        await Until(() => loop.Status.State == PilotState.StepReady);
        await Task.Delay(100);
        Assert.Empty(bridge.Executed);
        loop.Step();
        await Until(() => bridge.Executed.Count == 1);
        loop.Stop();
    }

    [Fact]
    public async Task Pauses_after_repeated_failures()
    {
        var (loop, bridge, _, _) = Make(PilotMode.FullAuto);
        bridge.OnExecute = _ =>
        {
            bridge.Set(TestData.Combat());
            return ActionResult.Fail("nope");
        };
        bridge.Set(TestData.Combat());
        loop.Start();
        await Until(() => loop.Paused);
        loop.Stop();
        Assert.Equal(3, bridge.Executed.Count);
    }

    [Fact]
    public void Revalidate_follows_semantic_keys_across_snapshots()
    {
        var old = TestData.Combat();
        var fresh = TestData.Combat();
        fresh.Sequence = old.Sequence + 1;
        fresh.Actions.RemoveAt(0);
        foreach (var (a, i) in fresh.Actions.Select((a, i) => (a, i))) a.Id = "a" + i;
        var target = PilotLoop.Revalidate(old.Actions[2], old, fresh);
        Assert.Equal("play|Defend|", target!.SemanticKey);
        Assert.Null(PilotLoop.Revalidate(old.Actions[0], old, fresh));
    }

    [Theory]
    [InlineData("all", 0ul)]
    [InlineData("Bob", 2ul)]
    [InlineData("bo", 2ul)]
    [InlineData("3", 3ul)]
    [InlineData("Silent", 3ul)]
    [InlineData("nobody", 0ul)]
    public void Resolves_recipients(string to, ulong expected)
    {
        var s = new GameSnapshot
        {
            Players =
            {
                new PlayerInfo { NetId = 1, Name = "Me", IsLocal = true },
                new PlayerInfo { NetId = 2, Name = "Bob" },
                new PlayerInfo { NetId = 3, Name = "Alice", CharacterName = "Silent" },
            },
        };
        Assert.Equal(expected, PilotLoop.ResolveRecipient(to, s));
    }

    [Fact]
    public void Mode_filter()
    {
        var combat = TestData.Combat();
        var map = new GameSnapshot { Screen = ScreenKind.Map };
        Assert.True(PilotLoop.ModeAllows(PilotMode.CombatOnly, combat));
        Assert.False(PilotLoop.ModeAllows(PilotMode.CombatOnly, map));
        Assert.False(PilotLoop.ModeAllows(PilotMode.OutOfCombatOnly, combat));
        Assert.True(PilotLoop.ModeAllows(PilotMode.OutOfCombatOnly, map));
        Assert.False(PilotLoop.ModeAllows(PilotMode.Off, map));
    }
}
