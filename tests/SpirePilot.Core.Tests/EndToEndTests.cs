using System.Text.Json.Nodes;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

/// <summary>
/// Whole pipeline without the game: PilotLoop → DecisionEngine → real HTTP LLM client → fake LLM server → parsed
/// decision → team messages → executed action, for both provider protocols.
/// </summary>
public class EndToEndTests
{
    private sealed class Bridge : IGameBridge
    {
        private readonly object _lock = new();
        private GameSnapshot? _current;
        private long _seq = 1000;
        public List<ActionOption> Executed { get; } = new();
        public List<(ulong, string)> Bubbles { get; } = new();

        public void Set(GameSnapshot? s) { lock (_lock) _current = s; }

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
            lock (_lock)
            {
                Executed.Add(action);
                _current = new GameSnapshot { Screen = ScreenKind.Combat, CanAct = false, WaitReason = "animating" };
            }
            return Task.FromResult(ActionResult.Ok());
        }

        public void ShowSpeech(ulong playerNetId, string text) { lock (_lock) Bubbles.Add((playerNetId, text)); }
        public void PingMapNode(string nodeKey) { }
    }

    private sealed class Transport : ITeamTransport
    {
        public List<TeamMessage> Sent { get; } = new();
        public bool IsAvailable => true;
        public IReadOnlyCollection<ulong> LinkedPeers => new ulong[] { 2 };
        public bool Send(TeamMessage message) { lock (Sent) Sent.Add(message); return true; }
    }

    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    /// <summary>Co-op combat: Bob asked for a potion; we hold a player-targetable Blood Potion.</summary>
    private static GameSnapshot CoopCombat()
    {
        var s = TestData.Combat();
        s.IsMultiplayer = true;
        s.LocalPlayer!.Potions.Add(new PotionInfo { Slot = 0, Id = "BLOOD_POTION", Name = "Blood Potion", Target = "AnyPlayer", UsableNow = true });
        s.Players.Add(new PlayerInfo { NetId = 2, Name = "Bob", CharacterName = "Silent", Hp = 9, MaxHp = 70, HasTeamLink = true });
        s.Actions.Insert(3, new ActionOption
        {
            Id = "a3", Kind = ActionKind.UsePotion, Label = "Use Blood Potion → Bob", SemanticKey = "potion:BLOOD_POTION@P:2",
            Args = { [ActionArgs.Slot] = "0", [ActionArgs.Target] = "p1" },
        });
        s.Actions[4].Id = "a4"; // end turn
        return s;
    }

    private static (PilotLoop loop, Bridge bridge, TeamChatHub hub, Transport transport, List<DecisionTrace> traces) Make(LlmProfile profile)
    {
        var settings = new PilotSettings();
        settings.Profiles = new() { profile };
        settings.ActiveProfile = profile.Name;
        settings.Control.Mode = PilotMode.FullAuto;
        settings.Control.ActionDelayMs = 0;
        settings.Normalize();
        var bridge = new Bridge();
        var transport = new Transport();
        var hub = new TeamChatHub { LocalNetId = 1, LocalName = "Me", Transport = transport };
        var engine = new DecisionEngine(() => settings, hub, new NullLog());
        var loop = new PilotLoop(bridge, engine, hub, () => settings, new NullLog()) { IdleDelay = TimeSpan.FromMilliseconds(10) };
        var traces = new List<DecisionTrace>();
        loop.DecisionTraced += t => { lock (traces) traces.Add(t); };
        return (loop, bridge, hub, transport, traces);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    private static string DecisionJson(string requestId) =>
        new JsonObject
        {
            ["thought"] = "Bob is at 9 HP; heal him with the Blood Potion as he asked.",
            ["actions"] = new JsonArray("a3"),
            ["messages"] = new JsonArray(new JsonObject
            {
                ["to"] = "Bob", ["type"] = "accept", ["text"] = "给你用了血瓶！", ["reply_to"] = requestId,
            }),
            ["memo"] = "Bob is fragile; keep healing potions for him.",
        }.ToJsonString();

    private static string RequestIdFrom(FakeLlmServer.Received r)
    {
        var text = r.Body!.ToJsonString();
        return System.Text.RegularExpressions.Regex.Match(text, @"\[([0-9a-f]{12})\] Bob").Groups[1].Value;
    }

    [Fact]
    public async Task OpenAi_compatible_provider_answers_a_teammate_and_uses_a_potion_on_him()
    {
        using var server = new FakeLlmServer((r, _) => (200, new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "```json\n" + DecisionJson(RequestIdFrom(r)) + "\n```" },
                ["finish_reason"] = "stop",
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 3000, ["completion_tokens"] = 80 },
        }.ToJsonString()));
        var profile = new LlmProfile { Name = "deepseek", Provider = LlmProvider.OpenAiCompatible, BaseUrl = server.BaseUrl + "/v1", ApiKey = "sk-test", Model = "deepseek-chat" };
        var (loop, bridge, hub, transport, traces) = Make(profile);
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "我快死了，能给我一瓶药吗？", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        bridge.Set(CoopCombat());

        loop.Start();
        await Until(() => bridge.Executed.Count == 1 && traces.Count == 1);
        loop.Stop();

        Assert.Equal("potion:BLOOD_POTION@P:2", bridge.Executed[0].SemanticKey);
        var sent = Assert.Single(transport.Sent);
        Assert.Equal(TeamMessageKind.Accept, sent.Kind);
        Assert.Equal(2ul, sent.To);
        Assert.True(sent.FromAi);
        Assert.Empty(hub.PeekAiInbox()); // the request was handled
        Assert.Empty(loop.Memory.OpenRequests); // ... and fulfilled by the potion on Bob
        var prompt = server.Requests[0].Body!["messages"]![1]!["content"]!.ToString();
        Assert.Contains("[TEAM INBOX]", prompt);
        Assert.Contains("我快死了", prompt);
        Assert.Contains("a3 | Use Blood Potion → Bob", prompt);
        Assert.Equal(3000, traces[0].InputTokens);
        Assert.Contains(bridge.Bubbles, b => b.Item1 == 1 && b.Item2.Contains("血瓶"));
    }

    [Fact]
    public async Task Anthropic_provider_runs_the_same_pipeline_with_cached_system_blocks()
    {
        using var server = new FakeLlmServer((r, _) => (200, new JsonObject
        {
            ["type"] = "message",
            ["model"] = "claude-opus-5-5",
            ["stop_reason"] = "end_turn",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = DecisionJson(RequestIdFrom(r)) }),
            ["usage"] = new JsonObject { ["input_tokens"] = 900, ["output_tokens"] = 60, ["cache_read_input_tokens"] = 2500 },
        }.ToJsonString()));
        var profile = LlmPresets.FromPreset(LlmPresets.Find("anthropic")!, "claude");
        profile.BaseUrl = server.BaseUrl;
        profile.ApiKey = "sk-ant-test";
        var (loop, bridge, hub, transport, _) = Make(profile);
        hub.Receive(new TeamMessage { From = 2, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "potion please", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        bridge.Set(CoopCombat());

        loop.Start();
        await Until(() => bridge.Executed.Count == 1);
        loop.Stop();

        var req = server.Requests[0];
        Assert.Equal("/v1/messages", req.Path);
        var system = req.Body!["system"]!.AsArray();
        Assert.Equal(2, system.Count); // core prompt + owner block, both cacheable
        Assert.All(system, b => Assert.Equal("ephemeral", b!["cache_control"]!["type"]!.ToString()));
        Assert.Equal("low", req.Body["output_config"]!["effort"]!.ToString()); // combat turn → combat effort
        Assert.Single(transport.Sent);
        Assert.Equal("potion:BLOOD_POTION@P:2", bridge.Executed[0].SemanticKey);
    }
}
