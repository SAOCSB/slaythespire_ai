using System.Text;
using System.Text.Json.Nodes;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

/// <summary>
/// Owner-filtered lookups for the HTTP API, the route lines of the state text, teammates' vanilla emotes in the inbox,
/// the build / game version check and the effort a test request was really sent with.
/// </summary>
public class TeamApiTests
{
    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    private static PilotSettings Settings()
    {
        var s = new PilotSettings();
        s.Profiles[0].ApiKey = "k";
        s.Normalize();
        return s;
    }

    // ---------------------------------------------------------------- owner-filtered action lookup

    private static GameSnapshot WithTeammatePotion()
    {
        var snap = TestData.Combat();
        snap.Actions.Add(new ActionOption { Id = "a4", Kind = ActionKind.UsePotion, Label = "Block potion → Bob", SemanticKey = "potion:BLOCK_POTION@P:2" });
        snap.Actions.Add(new ActionOption { Id = "a5", Kind = ActionKind.UsePotion, Label = "Block potion", SemanticKey = "potion:BLOCK_POTION@P:1" });
        return snap;
    }

    [Fact]
    public void An_action_an_owner_rule_removed_is_reported_as_such_by_key_and_by_id()
    {
        var settings = Settings();
        settings.Team.AllowGivingPotions = false;
        var raw = WithTeammatePotion();
        var menu = OwnerRules.Filtered(raw, settings);

        Assert.Null(OwnerRules.Find(menu, raw, "potion:BLOCK_POTION@P:2", null, out bool byKey));
        Assert.True(byKey);
        Assert.Null(OwnerRules.Find(menu, raw, null, "a4", out bool byId));
        Assert.True(byId);

        var own = OwnerRules.Find(menu, raw, "potion:BLOCK_POTION@P:1", null, out bool allowed);
        Assert.Equal("a5", own?.Id);
        Assert.False(allowed);
        Assert.Equal(6, raw.Actions.Count); // the capture itself is untouched
    }

    [Fact]
    public void Unknown_actions_are_not_blamed_on_the_owner_and_the_key_wins_over_the_id()
    {
        var settings = Settings();
        var raw = WithTeammatePotion();
        var menu = OwnerRules.Filtered(raw, settings);

        Assert.Null(OwnerRules.Find(menu, raw, "play:NOPE@-", null, out bool removed));
        Assert.False(removed);
        Assert.Null(OwnerRules.Find(menu, raw, null, "a99", out removed));
        Assert.False(removed);
        Assert.Equal("a3", OwnerRules.Find(menu, raw, "end_turn", "a0", out _)?.Id);
        Assert.Equal("a1", OwnerRules.Find(menu, raw, null, " A1 ", out _)?.Id);
    }

    // ---------------------------------------------------------------- route lines

    [Fact]
    public void The_route_lines_are_the_prompts_own()
    {
        var settings = Settings();
        settings.Route.PinnedPath = new() { "1,0", "1,1" };
        settings.Route.PinnedAct = 1;
        var snap = new GameSnapshot
        {
            Screen = ScreenKind.Map, InRun = true, CanAct = true, LocalPlayerId = 1, Run = new RunInfo { Act = 1, Floor = 1 },
            Players = { new PlayerInfo { NetId = 1, Name = "Me", IsLocal = true, Hp = 70, MaxHp = 80 } },
            Map = TestData.Map(),
        };
        snap.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.ChooseMapNode, Label = "Travel to 0,0", SemanticKey = "map:0,0" });

        var (routes, pinned) = DecisionEngine.Routes(snap, settings);
        Assert.NotEmpty(routes);
        Assert.Equal("1,0", pinned);

        var sb = new StringBuilder();
        PromptBuilder.AppendRoutes(sb, routes, pinned);
        var block = sb.ToString().Replace("\r\n", "\n");
        Assert.StartsWith("[ROUTES]", block);
        Assert.Contains("its next node is 1,0.", block);
        var user = PromptBuilder.BuildUser(snap, new DecisionContext { RouteSuggestions = routes, PinnedNext = pinned }, new StateFormatter());
        Assert.Contains(block, user);

        var combat = TestData.Combat();
        Assert.Empty(DecisionEngine.Routes(combat, settings).Routes);
        sb.Clear();
        PromptBuilder.AppendRoutes(sb, new List<Route.RoutePath>(), null);
        Assert.Equal("", sb.ToString());
    }

    // ---------------------------------------------------------------- teammates' emotes

    private static TeamMessage Open(string id, TeamMessageKind kind, ulong to) =>
        new() { Id = id, Kind = kind, To = to, Text = "?", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() };

    [Fact]
    public void A_thumbs_up_answers_the_newest_open_request_to_that_teammate_or_everyone()
    {
        var outgoing = new List<TeamMessage>
        {
            Open("old", TeamMessageKind.RequestBlock, 2),
            Open("toall", TeamMessageKind.AskNeedPotion, 0),
            Open("other", TeamMessageKind.RequestDebuff, 3),
        };
        var up = TeamEmotes.ToMessage("ThumbUp", 2, "Bob", 1, outgoing);
        Assert.Equal(TeamMessageKind.Accept, up.Kind);
        Assert.Equal("toall", up.ReplyTo);
        Assert.Equal(1ul, up.To);
        Assert.True(up.IsEmote);
        Assert.Contains("Bob", up.Text);

        var down = TeamEmotes.ToMessage("ThumbDown", 3, "Cid", 1, outgoing);
        Assert.Equal(TeamMessageKind.Decline, down.Kind);
        Assert.Equal("other", down.ReplyTo);

        var nothingOpen = TeamEmotes.ToMessage("ThumbUp", 2, "Bob", 1, new List<TeamMessage>());
        Assert.Equal(TeamMessageKind.Accept, nothingOpen.Kind);
        Assert.Null(nothingOpen.ReplyTo);
        Assert.Equal(0ul, nothingOpen.To);
    }

    [Theory]
    [InlineData("Exclamation", "!")]
    [InlineData("QuestionMark", "?")]
    [InlineData("SadSlime", "SadSlime")]
    public void Other_emotes_are_short_human_chat(string reaction, string shown)
    {
        var m = TeamEmotes.ToMessage(reaction, 2, "Bob", 1, new List<TeamMessage> { Open("q", TeamMessageKind.AskNeedPotion, 2) });
        Assert.Equal(TeamMessageKind.Chat, m.Kind);
        Assert.False(m.FromAi);
        Assert.Null(m.ReplyTo);
        Assert.Equal(0ul, m.To);
        Assert.EndsWith(shown, m.Text);
        Assert.True(DecisionEngine.IsActionable(m));
    }

    [Fact]
    public void A_thumbs_up_to_the_AIs_potion_question_is_a_yes_it_acts_on()
    {
        var settings = Settings();
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog());
        engine.Memory.TrackOutgoing(Open("ask1", TeamMessageKind.AskNeedPotion, 2));

        Assert.True(hub.Receive(TeamEmotes.ToMessage("ThumbUp", 2, "Bob", 1, engine.Memory.OutgoingRequests)));
        var yes = Assert.Single(hub.PeekAiInbox());
        Assert.Equal("ask1", yes.ReplyTo);
        Assert.True(yes.IsEmote);
        Assert.True(engine.NeedsAttention(yes));
    }

    [Fact]
    public void Emote_floods_are_capped_like_any_inbox_traffic()
    {
        var hub = new TeamChatHub { LocalNetId = 1 };
        for (int i = 0; i < 9; i++) hub.Receive(TeamEmotes.ToMessage("Exclamation", 2, "Bob", 1, new List<TeamMessage>()));
        Assert.Equal(5, hub.PeekAiInbox().Count);
    }

    [Fact]
    public void The_emote_mark_never_travels_on_the_wire()
    {
        var m = TeamEmotes.ToMessage("ThumbUp", 2, "Bob", 1, new List<TeamMessage>());
        Assert.True(m.Sanitized().IsEmote);
        Assert.Equal(TeamWire.ParseOutcome.Ok, TeamWire.TryDecode(TeamWire.Encode(m), out var back));
        Assert.False(back!.IsEmote);
    }

    // ---------------------------------------------------------------- build / game version

    [Theory]
    [InlineData("v0.107.1", 0, 107)]
    [InlineData("0.111.0-beta", 0, 111)]
    [InlineData("0.111", 0, 111)]
    [InlineData(" V1.2.3+abc ", 1, 2)]
    public void Game_versions_are_read_as_major_minor(string text, int major, int minor)
    {
        Assert.Equal((major, minor), GameVersion.MajorMinor(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("v1")]
    [InlineData("1.x")]
    public void Unreadable_versions_are_unknown(string? text)
    {
        Assert.Null(GameVersion.MajorMinor(text));
        Assert.Null(GameVersion.Matches(text, "0.111"));
    }

    [Fact]
    public void Only_the_compiled_branch_matches()
    {
        Assert.True(GameVersion.Matches("v0.107.1", "0.107"));
        Assert.True(GameVersion.Matches("v0.111.0", "0.111"));
        Assert.False(GameVersion.Matches("v0.111.0", "0.107"));
        Assert.False(GameVersion.Matches("v0.107.1", "0.111"));
        Assert.Equal("0.111", GameVersion.Branch("v0.111.2"));
    }

    // ---------------------------------------------------------------- effort the request was sent with

    private const string AnthropicOk =
        "{\"type\":\"message\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    [Fact]
    public async Task A_test_with_a_rejected_combat_effort_reports_the_effort_really_used()
    {
        using var server = new FakeLlmServer((r, _) =>
            r.Body!["output_config"]?["effort"]?.ToString() == "minimal"
                ? (400, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"output_config.effort: 'minimal' is not supported\"}}")
                : (200, AnthropicOk));
        var profile = LlmPresets.FromPreset(LlmPresets.Find("anthropic")!);
        profile.ApiKey = "k";
        profile.BaseUrl = server.BaseUrl;
        profile.ReasoningEffort = "high";

        var general = await LlmClientFactory.TestAsync(profile, CancellationToken.None);
        Assert.Equal("high", general.Effort);
        var combat = await LlmClientFactory.TestAsync(profile, CancellationToken.None, "minimal");
        Assert.Equal("high", combat.Effort);
        Assert.Equal("minimal", server.Requests[1].Body!["output_config"]!["effort"]!.ToString());
    }

    [Fact]
    public async Task An_openai_compatible_server_without_reasoning_effort_reports_none()
    {
        using var server = new FakeLlmServer((r, _) => r.Body!["reasoning_effort"] != null
            ? (400, "{\"error\":{\"message\":\"Unrecognized request argument supplied: reasoning_effort\"}}")
            : (200, new JsonObject
            {
                ["model"] = "m",
                ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "{}" }, ["finish_reason"] = "stop" }),
            }.ToJsonString()));
        var profile = new LlmProfile { BaseUrl = server.BaseUrl, ApiKey = "k", Model = "m", ReasoningEffort = "low" };

        var response = await LlmClientFactory.TestAsync(profile, CancellationToken.None, "high");
        Assert.Equal("high", server.Requests[0].Body!["reasoning_effort"]!.ToString());
        Assert.Null(response.Effort);
    }
}
