using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

public class DecisionEngineTests
{
    private sealed class ScriptedClient : ILlmClient
    {
        private readonly Queue<Func<LlmRequest, string>> _answers;
        public List<LlmRequest> Requests { get; } = new();
        public ScriptedClient(params Func<LlmRequest, string>[] answers) => _answers = new(answers);
        public string Describe => "scripted";

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_answers.Count == 0) throw new LlmException("no more answers");
            return Task.FromResult(new LlmResponse { Text = _answers.Dequeue()(request), InputTokens = 10, OutputTokens = 5 });
        }
    }

    private sealed class NullLog : IPilotLog
    {
        public List<string> Warnings { get; } = new();
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void Error(string message) => Warnings.Add(message);
    }

    private static PilotSettings Settings()
    {
        var s = new PilotSettings();
        s.Profiles[0].ApiKey = "test-key";
        s.Normalize();
        return s;
    }

    private static (DecisionEngine engine, ScriptedClient client, TeamChatHub hub) Make(PilotSettings settings, params Func<LlmRequest, string>[] answers)
    {
        var client = new ScriptedClient(answers);
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => client);
        return (engine, client, hub);
    }

    [Fact]
    public async Task Executes_a_plan_without_calling_the_llm_again()
    {
        var (engine, client, _) = Make(Settings(), _ => "{\"thought\":\"t\",\"actions\":[\"a2\",\"a0\",\"a3\"]}");
        var snap = TestData.Combat();
        var first = await engine.DecideAsync(snap, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Llm, first!.Source);
        Assert.Equal("a2", first.FirstActionId);

        // After playing Defend the ids shift; the plan follows semantic keys.
        var next = TestData.Combat();
        next.Sequence = 8;
        next.Combat!.Hand.RemoveAt(1);
        next.Actions.RemoveAll(a => a.Kind == ActionKind.PlayCard && a.Label.Contains("Defend"));
        next.Actions[0].Id = "a5";
        var second = await engine.DecideAsync(next, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Plan, second!.Source);
        Assert.Equal("a5", second.FirstActionId);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Plan_is_dropped_when_a_team_message_arrives()
    {
        var (engine, client, hub) = Make(Settings(),
            _ => "{\"thought\":\"t\",\"actions\":[\"a2\",\"a0\"]}",
            _ => "{\"thought\":\"answer\",\"actions\":[\"a3\"]}");
        var first = TestData.Combat();
        first.IsMultiplayer = true;
        await engine.DecideAsync(first, null, CancellationToken.None);
        hub.Receive(new TeamMessage { From = 2, Kind = TeamMessageKind.RequestPotion, Text = "potion pls", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        var second = TestData.Combat();
        second.IsMultiplayer = true;
        var d = await engine.DecideAsync(second, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Llm, d!.Source);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("potion pls", client.Requests[1].Messages[0].Content);
        Assert.Single(d.HandledInboxIds);
    }

    [Fact]
    public async Task Retries_once_with_feedback_after_an_invalid_answer()
    {
        var (engine, client, _) = Make(Settings(),
            _ => "{\"thought\":\"t\",\"actions\":[\"a42\"]}",
            req => req.Messages.Count == 3 && req.Messages[2].Content.Contains("Invalid answer") ? "{\"thought\":\"t\",\"actions\":[\"a1\"]}" : "{}");
        var d = await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal("a1", d!.FirstActionId);
        Assert.Equal(2, client.Requests.Count);
        // The repair turn keeps the original prompt first (cache-friendly) and adds the bad answer.
        Assert.Equal(LlmRole.Assistant, client.Requests[1].Messages[1].Role);
    }

    [Fact]
    public async Task Falls_back_to_rules_when_the_llm_fails()
    {
        var (engine, _, _) = Make(Settings(), _ => throw new LlmException("boom"));
        var d = await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(DecisionSource.Fallback, d!.Source);
        // The fallback blocks because 9 damage is incoming and we have 0 block.
        Assert.Equal("a2", d.FirstActionId);
    }

    [Fact]
    public async Task Config_errors_stop_instead_of_falling_back()
    {
        var (engine, _, _) = Make(Settings(), _ => throw new LlmException("HTTP 401: invalid x-api-key", 401));
        await Assert.ThrowsAsync<PilotConfigException>(() => engine.DecideAsync(TestData.Combat(), null, CancellationToken.None));
        Assert.NotNull(engine.LastLlmError);
    }

    [Fact]
    public async Task Circuit_breaker_skips_the_llm_after_repeated_failures()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        int calls = 0;
        var client = new ScriptedClient(Enumerable.Range(0, 10).Select<int, Func<LlmRequest, string>>(_ => r => { calls++; throw new LlmException("down", 503, retryable: false); }).ToArray());
        var settings = Settings();
        var engine = new DecisionEngine(() => settings, new TeamChatHub(), new NullLog(), _ => client, () => now);
        for (int i = 0; i < 3; i++) Assert.Equal(DecisionSource.Fallback, (await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None))!.Source);
        Assert.Equal(3, calls);
        Assert.Equal(DecisionSource.Fallback, (await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None))!.Source);
        Assert.Equal(3, calls); // cooling down: no request
        now = now.AddMinutes(2);
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task Plan_aborts_when_the_player_takes_damage()
    {
        var (engine, client, _) = Make(Settings(),
            _ => "{\"thought\":\"t\",\"actions\":[\"a2\",\"a0\"]}",
            _ => "{\"thought\":\"re\",\"actions\":[\"a3\"]}");
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        var hurt = TestData.Combat();
        hurt.Sequence = 9;
        hurt.LocalPlayer!.Hp -= 5;
        hurt.Combat!.Hand.RemoveAt(1);
        var d = await engine.DecideAsync(hurt, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Llm, d!.Source);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains(engine.Memory.History, h => h.Contains("took damage"));
    }

    [Fact]
    public async Task Plan_aborts_when_cards_were_drawn()
    {
        var (engine, client, _) = Make(Settings(),
            _ => "{\"thought\":\"t\",\"actions\":[\"a2\",\"a0\"]}",
            _ => "{\"thought\":\"re\",\"actions\":[\"a3\"]}");
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        var drew = TestData.Combat(); // still 3 cards in hand after playing one => something was drawn
        drew.Sequence = 9;
        await engine.DecideAsync(drew, null, CancellationToken.None);
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public async Task Answers_from_other_ais_do_not_trigger_a_call_or_break_the_plan()
    {
        var (engine, client, hub) = Make(Settings(), _ => "{\"thought\":\"t\",\"actions\":[\"a2\",\"a0\"]}");
        var snap = TestData.Combat();
        snap.IsMultiplayer = true;
        await engine.DecideAsync(snap, null, CancellationToken.None);
        hub.Receive(new TeamMessage { From = 2, Kind = TeamMessageKind.Accept, FromAi = true, Text = "ok", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        var next = TestData.Combat();
        next.IsMultiplayer = true;
        next.Sequence = 8;
        next.Combat!.Hand.RemoveAt(1);
        next.Actions.RemoveAll(a => a.Label.Contains("Defend"));
        var d = await engine.DecideAsync(next, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Plan, d!.Source);
        Assert.Single(client.Requests);
        Assert.Single(d.HandledInboxIds);
    }

    [Fact]
    public async Task Pauses_when_no_allowed_character_is_unlocked()
    {
        var settings = Settings();
        settings.Characters.Allowed = new() { "DEFECT" };
        var (engine, _, _) = Make(settings);
        var snap = new GameSnapshot
        {
            Screen = ScreenKind.CharacterSelect,
            CharacterSelect = new CharacterSelectInfo { Characters = { new CharacterOption { Id = "IRONCLAD" }, new CharacterOption { Id = "DEFECT", Locked = true } } },
            Actions = { new ActionOption { Id = "a0", Kind = ActionKind.SelectCharacter, Args = { [ActionArgs.Character] = "IRONCLAD" } } },
        };
        await Assert.ThrowsAsync<PilotConfigException>(() => engine.DecideAsync(snap, null, CancellationToken.None));
    }

    [Fact]
    public async Task Ai_character_choice_only_sees_allowed_characters()
    {
        var settings = Settings();
        settings.Characters.Allowed = new() { "SILENT" };
        settings.Characters.Policy = CharacterPickPolicy.AiChoice;
        string? prompt = null;
        var (engine, _, _) = Make(settings, req => { prompt = req.Messages[0].Content; return "{\"thought\":\"t\",\"actions\":[\"a1\"]}"; });
        var snap = new GameSnapshot
        {
            Screen = ScreenKind.CharacterSelect,
            CharacterSelect = new CharacterSelectInfo { Characters = { new CharacterOption { Id = "IRONCLAD", Selected = true }, new CharacterOption { Id = "SILENT" } } },
            Actions =
            {
                new ActionOption { Id = "a0", Kind = ActionKind.SelectCharacter, Label = "Pick Ironclad", Args = { [ActionArgs.Character] = "IRONCLAD" } },
                new ActionOption { Id = "a1", Kind = ActionKind.SelectCharacter, Label = "Pick Silent", Args = { [ActionArgs.Character] = "SILENT" } },
                new ActionOption { Id = "a2", Kind = ActionKind.Embark, Label = "Embark" },
            },
        };
        var d = await engine.DecideAsync(snap, null, CancellationToken.None);
        Assert.Equal("a1", d!.FirstActionId);
        Assert.DoesNotContain("Pick Ironclad", prompt);
        Assert.DoesNotContain("Embark", prompt);
    }

    [Fact]
    public async Task Skips_the_llm_for_trivial_screens()
    {
        var (engine, client, _) = Make(Settings());
        var snap = new GameSnapshot { Screen = ScreenKind.CombatRewards, Actions = { new ActionOption { Id = "a0", Kind = ActionKind.Proceed, Label = "Proceed" } } };
        var d = await engine.DecideAsync(snap, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Trivial, d!.Source);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Follows_a_pinned_route_without_asking()
    {
        var settings = Settings();
        settings.Route.PinnedPath = new() { "1,0", "1,1" };
        settings.Route.PinnedAct = 1;
        var (engine, client, _) = Make(settings);
        var snap = new GameSnapshot
        {
            Screen = ScreenKind.Map,
            Run = new RunInfo { Act = 1 },
            Map = TestData.Map(),
            Actions =
            {
                new ActionOption { Id = "a0", Kind = ActionKind.ChooseMapNode, Args = { [ActionArgs.Node] = "0,0" } },
                new ActionOption { Id = "a1", Kind = ActionKind.ChooseMapNode, Args = { [ActionArgs.Node] = "1,0" } },
            },
        };
        var d = await engine.DecideAsync(snap, null, CancellationToken.None);
        Assert.Equal(DecisionSource.Rule, d!.Source);
        Assert.Equal("a1", d.FirstActionId);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task A_live_instruction_overrides_the_pinned_route()
    {
        var settings = Settings();
        settings.Route.PinnedPath = new() { "1,0" };
        settings.Route.PinnedAct = 1;
        var (engine, client, _) = Make(settings, req => req.Messages[0].Content.Contains("go left") ? "{\"thought\":\"ok\",\"actions\":[\"a0\"]}" : "{}");
        var snap = new GameSnapshot
        {
            Screen = ScreenKind.Map,
            Run = new RunInfo { Act = 1 },
            Map = TestData.Map(),
            Actions =
            {
                new ActionOption { Id = "a0", Kind = ActionKind.ChooseMapNode, Args = { [ActionArgs.Node] = "0,0" } },
                new ActionOption { Id = "a1", Kind = ActionKind.ChooseMapNode, Args = { [ActionArgs.Node] = "1,0" } },
            },
        };
        var d = await engine.DecideAsync(snap, "go left", CancellationToken.None);
        Assert.Equal(DecisionSource.Llm, d!.Source);
        Assert.Equal("a0", d.FirstActionId);
        Assert.Single(client.Requests);
    }

    [Fact]
    public async Task Character_policy_respects_the_allow_list()
    {
        var settings = Settings();
        settings.Characters.Allowed = new() { "SILENT" };
        var (engine, _, _) = Make(settings);
        var snap = new GameSnapshot
        {
            Screen = ScreenKind.CharacterSelect,
            CharacterSelect = new CharacterSelectInfo
            {
                Characters = { new CharacterOption { Id = "IRONCLAD", Selected = true }, new CharacterOption { Id = "SILENT" }, new CharacterOption { Id = "DEFECT", Locked = true } },
            },
            Actions =
            {
                new ActionOption { Id = "a0", Kind = ActionKind.SelectCharacter, Args = { [ActionArgs.Character] = "IRONCLAD" } },
                new ActionOption { Id = "a1", Kind = ActionKind.SelectCharacter, Args = { [ActionArgs.Character] = "SILENT" } },
                new ActionOption { Id = "a2", Kind = ActionKind.Embark },
            },
        };
        var d = await engine.DecideAsync(snap, null, CancellationToken.None);
        Assert.Equal("a1", d!.FirstActionId);

        snap.CharacterSelect.Characters[0].Selected = false;
        snap.CharacterSelect.Characters[1].Selected = true;
        d = await engine.DecideAsync(snap, null, CancellationToken.None);
        Assert.Equal("a2", d!.FirstActionId);
    }

    [Fact]
    public async Task Team_messages_are_dropped_in_single_player()
    {
        var (engine, _, _) = Make(Settings(), _ => "{\"thought\":\"t\",\"actions\":[\"a0\"],\"messages\":[{\"to\":\"all\",\"type\":\"chat\",\"text\":\"hi\"}]}");
        var d = await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Empty(d!.Messages);
    }

    [Fact]
    public async Task Accepted_requests_are_remembered()
    {
        var (engine, _, hub) = Make(Settings(), req =>
        {
            var id = System.Text.RegularExpressions.Regex.Match(req.Messages[0].Content, @"\[([0-9a-f]{12})\]").Groups[1].Value;
            return "{\"thought\":\"t\",\"actions\":[\"a3\"],\"messages\":[{\"to\":\"Bob\",\"type\":\"accept\",\"text\":\"ok\",\"reply_to\":\"" + id + "\"}]}";
        });
        hub.Receive(new TeamMessage { From = 2, FromName = "Bob", Kind = TeamMessageKind.RequestDebuff, Text = "vuln pls", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        var snap = TestData.Combat();
        snap.IsMultiplayer = true;
        var d = await engine.DecideAsync(snap, null, CancellationToken.None);
        Assert.Single(d!.Messages);
        Assert.Single(engine.Memory.OpenRequests);
    }
}
