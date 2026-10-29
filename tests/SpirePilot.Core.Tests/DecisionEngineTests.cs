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
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        hub.Receive(new TeamMessage { From = 2, Kind = TeamMessageKind.RequestPotion, Text = "potion pls", Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        var d = await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
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
            req => req.Messages[0].Content.Contains("Problem with your previous answer") ? "{\"thought\":\"t\",\"actions\":[\"a1\"]}" : "{}");
        var d = await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal("a1", d!.FirstActionId);
        Assert.Equal(2, client.Requests.Count);
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
