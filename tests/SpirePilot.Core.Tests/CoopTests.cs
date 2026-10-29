using System.Text.Json;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

/// <summary>Co-op conversations end to end through the loop: who hears what, and when the AI answers.</summary>
public class CoopTests
{
    private sealed class Bridge : IGameBridge
    {
        private readonly object _lock = new();
        private GameSnapshot? _current;
        private long _seq = 500;
        public List<ActionOption> Executed { get; } = new();
        public List<TeamMessageKind> Signals { get; } = new();
        public void Set(GameSnapshot? s) { lock (_lock) _current = s; }
        public Task<GameSnapshot?> CaptureAsync(CancellationToken ct)
        {
            lock (_lock)
            {
                if (_current != null) _current.Sequence = ++_seq;
                return Task.FromResult(_current);
            }
        }
        public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken ct)
        {
            lock (_lock) Executed.Add(action);
            Set(new GameSnapshot { Screen = ScreenKind.Combat, CanAct = false, IsMultiplayer = true, WaitReason = "animating" });
            return Task.FromResult(ActionResult.Ok());
        }
        public void ShowSpeech(ulong playerNetId, string text) { }
        public void PingMapNode(string nodeKey) { }
        public bool TrySignal(TeamMessageKind kind) { lock (_lock) Signals.Add(kind); return true; }
    }

    private sealed class Transport : ITeamTransport
    {
        public List<TeamMessage> Sent { get; } = new();
        public bool IsAvailable => true;
        public IReadOnlyCollection<ulong> LinkedPeers => new ulong[] { 2 };
        public bool Send(TeamMessage message) { lock (Sent) Sent.Add(message); return true; }
    }

    private sealed class Client : ILlmClient
    {
        public Func<LlmRequest, string> Answer { get; set; } = _ => "{\"thought\":\"t\",\"actions\":[]}";
        public List<LlmRequest> Requests { get; } = new();
        public string Describe => "test";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request);
            return Task.FromResult(new LlmResponse { Text = Answer(request) });
        }
    }

    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    private static (PilotLoop loop, Bridge bridge, Client client, TeamChatHub hub, Transport transport, PilotSettings settings) Make()
    {
        var settings = new PilotSettings();
        settings.Profiles[0].ApiKey = "k";
        settings.Control.Mode = PilotMode.FullAuto;
        settings.Control.ActionDelayMs = 0;
        settings.Normalize();
        var bridge = new Bridge();
        var client = new Client();
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = transport };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        return (loop, bridge, client, hub, transport, settings);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static string IdIn(LlmRequest r) =>
        System.Text.RegularExpressions.Regex.Match(r.Messages[0].Content, @"\[([0-9a-f]{12})\] Bob").Groups[1].Value;

    private static GameSnapshot Waiting(bool bobLinked = true)
    {
        var s = TestData.Combat();
        s.IsMultiplayer = true;
        s.CanAct = false;
        s.WaitReason = "waiting for teammates";
        s.Actions.Clear();
        s.Players.Add(new PlayerInfo { NetId = 2, Name = "Bob", Hp = 9, MaxHp = 70, HasTeamLink = bobLinked });
        return s;
    }

    [Fact]
    public async Task Answers_a_teammate_while_it_cannot_act()
    {
        var (loop, bridge, client, hub, transport, _) = Make();
        client.Answer = r => "{\"thought\":\"no potions left\",\"actions\":[],\"messages\":[{\"to\":\"Bob\",\"type\":\"decline\",\"text\":\"没药了\",\"reply_to\":\"" + IdIn(r) + "\"}]}";
        bridge.Set(Waiting());
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "potion?" });

        loop.Start();
        await Until(() => transport.Sent.Count == 1);
        await Task.Delay(100);
        loop.Stop();

        Assert.Equal(TeamMessageKind.Decline, transport.Sent[0].Kind);
        Assert.Equal(2ul, transport.Sent[0].To);
        Assert.Single(client.Requests); // answered once, then the inbox is read
        Assert.Contains("[NO ACTION NEEDED]", client.Requests[0].Messages[0].Content);
        Assert.Empty(bridge.Executed);
        Assert.Empty(hub.PeekAiInbox());
    }

    [Fact]
    public async Task Ended_turn_waits_quietly_and_only_considers_undo_for_a_teammate()
    {
        var (loop, bridge, client, hub, transport, _) = Make();
        var s = Waiting();
        s.CanAct = true;
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.UndoEndTurn, Label = "Undo end turn", SemanticKey = "undo_end_turn" });
        bridge.Set(s);
        loop.Start();
        await Task.Delay(150);
        Assert.Empty(client.Requests); // no LLM call, no undo without a reason
        Assert.Empty(bridge.Executed);

        client.Answer = r => "{\"thought\":\"Bob is dying\",\"actions\":[\"a0\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"accept\",\"text\":\"coming\",\"reply_to\":\"" + IdIn(r) + "\"}]}";
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "heal me" });
        await Until(() => bridge.Executed.Count == 1);
        loop.Stop();
        Assert.Equal(ActionKind.UndoEndTurn, bridge.Executed[0].Kind);
        Assert.Single(transport.Sent);
        Assert.Single(loop.Memory.OpenRequests); // accepted, delivered, not yet fulfilled
    }

    [Fact]
    public async Task An_undo_without_an_accept_still_remembers_the_request()
    {
        var (loop, bridge, client, hub, transport, _) = Make();
        var s = Waiting();
        s.CanAct = true;
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.UndoEndTurn, Label = "Undo end turn", SemanticKey = "undo_end_turn" });
        bridge.Set(s);
        client.Answer = _ => "{\"thought\":\"help Bob\",\"actions\":[\"a0\"]}";
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "heal me" });
        loop.Start();
        await Until(() => bridge.Executed.Count == 1);
        await Task.Delay(50);
        loop.Stop();
        Assert.Single(loop.Memory.OpenRequests);
        Assert.Empty(hub.PeekAiInbox()); // read: no second undo for the same request
    }

    [Fact]
    public async Task Teammates_without_the_mod_get_an_emote_instead_of_text()
    {
        var (loop, bridge, client, hub, transport, _) = Make();
        var s = TestData.Combat();
        s.IsMultiplayer = true;
        s.Players.Add(new PlayerInfo { NetId = 2, Name = "Bob", Hp = 50, MaxHp = 70, HasTeamLink = false });
        bridge.Set(s);
        client.Answer = _ => "{\"thought\":\"t\",\"actions\":[\"a3\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"request_potion\",\"text\":\"potion?\"}]}";
        loop.Start();
        await Until(() => bridge.Executed.Count == 1);
        loop.Stop();
        Assert.Empty(transport.Sent);
        Assert.Equal(new[] { TeamMessageKind.RequestPotion }, bridge.Signals);
        Assert.Contains(loop.Memory.History, h => h.Contains("cannot read messages"));
        Assert.Contains("[no SpirePilot", client.Requests[0].Messages[0].Content);
    }

    [Fact]
    public async Task Open_requests_are_not_repeated()
    {
        var (loop, bridge, client, _, transport, _) = Make();
        var s = TestData.Combat();
        s.IsMultiplayer = true;
        s.Players.Add(new PlayerInfo { NetId = 2, Name = "Bob", Hp = 50, MaxHp = 70, HasTeamLink = true });
        client.Answer = _ => "{\"thought\":\"t\",\"actions\":[\"a0\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"request_debuff\",\"debuff\":\"Weak\",\"target\":\"e0\",\"text\":\"weaken the cultist pls\"}]}";
        bridge.Set(s);
        loop.Start();
        await Until(() => bridge.Executed.Count == 1);
        var again = TestData.Combat();
        again.IsMultiplayer = true;
        again.Players.Add(new PlayerInfo { NetId = 2, Name = "Bob", Hp = 50, MaxHp = 70, HasTeamLink = true });
        bridge.Set(again);
        await Until(() => bridge.Executed.Count == 2);
        loop.Stop();
        Assert.Single(transport.Sent);
        Assert.Contains("[YOUR OPEN REQUESTS]", client.Requests[^1].Messages[0].Content);
    }

    private sealed class BlinkingBridge : IGameBridge
    {
        private readonly GameSnapshot _ready;
        private int _captures;
        public List<ActionOption> Executed { get; } = new();
        public BlinkingBridge(GameSnapshot ready) => _ready = ready;
        public Task<GameSnapshot?> CaptureAsync(CancellationToken ct)
        {
            int n = Interlocked.Increment(ref _captures);
            if (Executed.Count > 0) return Task.FromResult<GameSnapshot?>(new GameSnapshot { Screen = ScreenKind.Combat, CanAct = false, WaitReason = "enemy turn" });
            // Capture 2 and 3 (right after the decision) see a teammate's card animating.
            if (n is 2 or 3) return Task.FromResult<GameSnapshot?>(new GameSnapshot { Screen = ScreenKind.Combat, CanAct = false, WaitReason = "animating", Sequence = n });
            _ready.Sequence = n;
            return Task.FromResult<GameSnapshot?>(_ready);
        }
        public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken ct) { Executed.Add(action); return Task.FromResult(ActionResult.Ok()); }
        public void ShowSpeech(ulong playerNetId, string text) { }
        public void PingMapNode(string nodeKey) { }
    }

    [Fact]
    public async Task A_teammates_animation_does_not_throw_away_a_finished_decision()
    {
        var settings = new PilotSettings();
        settings.Profiles[0].ApiKey = "k";
        settings.Control.Mode = PilotMode.FullAuto;
        settings.Control.ActionDelayMs = 0;
        settings.Normalize();
        var client = new Client { Answer = _ => "{\"thought\":\"t\",\"actions\":[\"a0\"]}" };
        var snap = TestData.Combat();
        snap.IsMultiplayer = true;
        var bridge = new BlinkingBridge(snap);
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        loop.Start();
        await Until(() => bridge.Executed.Count == 1);
        loop.Stop();
        Assert.Single(client.Requests); // decided once, executed after the animation
    }

    [Fact]
    public void Enemy_targets_travel_by_combat_id_and_map_back_to_local_keys()
    {
        var sender = TestData.Combat();
        sender.Combat!.Enemies[0].CombatId = 12;
        var wire = PilotLoop.NormalizeTarget("e0", sender);
        Assert.Contains("cid:12", wire);

        var receiver = TestData.Combat();
        receiver.Combat!.Enemies[0].CombatId = 99;
        receiver.Combat.Enemies[1].CombatId = 12;
        Assert.StartsWith(receiver.Combat.Enemies[1].Key + " ", PromptBuilder.LocalTarget(wire!, receiver));
        receiver.Combat.Enemies[1].CombatId = 13;
        Assert.Contains("not in this fight", PromptBuilder.LocalTarget(wire!, receiver));
    }

    [Fact]
    public void Ai_counter_requests_are_not_actionable_but_a_yes_to_our_offer_is()
    {
        var engine = new DecisionEngine(() => new PilotSettings(), new TeamChatHub(), new NullLog());
        Assert.False(DecisionEngine.IsActionable(new TeamMessage { FromAi = true, Kind = TeamMessageKind.RequestPotion, ReplyTo = "abc" }));
        Assert.True(DecisionEngine.IsActionable(new TeamMessage { FromAi = true, Kind = TeamMessageKind.RequestPotion }));

        var offer = new TeamMessage { Id = "offer1", Kind = TeamMessageKind.AskNeedPotion, To = 2, Text = "need a potion?" };
        engine.Memory.TrackOutgoing(offer);
        Assert.True(engine.NeedsAttention(new TeamMessage { From = 2, Kind = TeamMessageKind.Accept, ReplyTo = "offer1" }));
        Assert.False(engine.NeedsAttention(new TeamMessage { From = 2, Kind = TeamMessageKind.Accept, ReplyTo = "other" }));
    }

    [Fact]
    public void Unknown_message_kinds_from_newer_versions_become_chat()
    {
        var m = JsonSerializer.Deserialize<TeamMessage>("{\"kind\":\"RequestHugs\",\"text\":\"hi\"}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(TeamMessageKind.Chat, m.Kind);
        var known = JsonSerializer.Deserialize<TeamMessage>("{\"kind\":\"requestPotion\"}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(TeamMessageKind.RequestPotion, known.Kind);
    }

    [Fact]
    public void A_flooding_peer_cannot_grow_the_inbox()
    {
        var hub = new TeamChatHub { LocalNetId = 1 };
        for (int i = 0; i < 50; i++) hub.Receive(new TeamMessage { From = 2, Kind = TeamMessageKind.RequestPotion, Text = "p" + i });
        Assert.Equal(5, hub.PeekAiInbox().Count);
        Assert.Equal("p49", hub.PeekAiInbox()[^1].Text);
    }

    [Fact]
    public void Requests_end_with_their_fight()
    {
        var memory = new AgentMemory();
        memory.NoteCombat("3:Cultists", 1);
        memory.TrackAccepted(new TeamMessage { Id = "r1", From = 2, Kind = TeamMessageKind.RequestDebuff });
        memory.TrackAccepted(new TeamMessage { Id = "r2", From = 2, Kind = TeamMessageKind.RequestPotion });
        memory.NoteCombat("3:Cultists", 2);
        Assert.Equal(new[] { "r2" }, memory.OpenRequests.Select(r => r.Id));
        memory.NoteCombat(null, 0);
        Assert.Empty(memory.OpenRequests);
    }
}

public class PinnedRouteLoopTests
{
    private sealed class MapBridge : IGameBridge
    {
        private readonly Func<GameSnapshot> _make;
        private long _seq;
        public List<ActionOption> Executed { get; } = new();
        public MapBridge(Func<GameSnapshot> make) => _make = make;
        public Task<GameSnapshot?> CaptureAsync(CancellationToken ct)
        {
            if (Executed.Count > 0) return Task.FromResult<GameSnapshot?>(new GameSnapshot { Screen = ScreenKind.Map, CanAct = false, WaitReason = "map travel" });
            var s = _make();
            s.Sequence = ++_seq;
            return Task.FromResult<GameSnapshot?>(s);
        }
        public Task<ActionResult> ExecuteAsync(ActionOption action, CancellationToken ct) { Executed.Add(action); return Task.FromResult(ActionResult.Ok()); }
        public void ShowSpeech(ulong playerNetId, string text) { }
        public void PingMapNode(string nodeKey) { }
    }

    private sealed class FixedAnswer : SpirePilot.Core.Llm.ILlmClient
    {
        private readonly string _answer;
        public FixedAnswer(string answer) => _answer = answer;
        public string Describe => "fixed";
        public Task<SpirePilot.Core.Llm.LlmResponse> CompleteAsync(SpirePilot.Core.Llm.LlmRequest request, CancellationToken ct) =>
            Task.FromResult(new SpirePilot.Core.Llm.LlmResponse { Text = _answer });
    }

    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    private static GameSnapshot MapSnap()
    {
        var s = new GameSnapshot { Screen = ScreenKind.Map, CanAct = true, InRun = true, Map = TestData.Map(), Run = new RunInfo { Act = 1, Floor = 3, Seed = "S1" } };
        s.Players.Add(new PlayerInfo { IsLocal = true, Hp = 70, MaxHp = 80 });
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.ChooseMapNode, Label = "0,0", SemanticKey = "map:0,0", Args = { [ActionArgs.Node] = "0,0" } });
        s.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.ChooseMapNode, Label = "1,0", SemanticKey = "map:1,0", Args = { [ActionArgs.Node] = "1,0" } });
        return s;
    }

    [Fact]
    public async Task A_live_instruction_beats_the_pinned_route_all_the_way_to_execution()
    {
        var settings = new PilotSettings();
        settings.Profiles[0].ApiKey = "k";
        settings.Control.Mode = PilotMode.FullAuto;
        settings.Control.ActionDelayMs = 0;
        settings.Route.PinnedPath = new() { "1,0" };
        settings.Route.PinnedAct = 1;
        settings.Route.PinnedSeed = "S1";
        settings.Normalize();
        var bridge = new MapBridge(MapSnap);
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => new FixedAnswer("{\"thought\":\"owner said so\",\"actions\":[\"a0\"]}"));
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        loop.SetLiveInstruction("ignore the pin, go to 0,0");
        loop.Start();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (bridge.Executed.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        loop.Stop();
        Assert.Equal("0,0", Assert.Single(bridge.Executed).Arg(ActionArgs.Node));
    }
}
