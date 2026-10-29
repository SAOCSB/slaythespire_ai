using System.Text.Json.Nodes;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Tests;

/// <summary>Owner-rule filtering for external agents, the side-effect-free prompt preview, and LLM failure handling.</summary>
public class FinalReviewTests
{
    private sealed class NullLog : IPilotLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) { }
    }

    private sealed class ScriptedClient : ILlmClient
    {
        public Func<LlmRequest, string>? Answer;
        public int Calls;
        public string Describe => "scripted";
        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Answer == null) throw new LlmException("HTTP 503: overloaded", 503);
            return Task.FromResult(new LlmResponse { Text = Answer(request) });
        }
    }

    private static PilotSettings Settings()
    {
        var s = new PilotSettings();
        s.Profiles[0].ApiKey = "k";
        s.Normalize();
        return s;
    }

    private static GameSnapshot CharacterSelect(string selected, int ascension)
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.CharacterSelect,
            CanAct = true,
            CharacterSelect = new CharacterSelectInfo
            {
                Ascension = ascension, MaxAscension = 10,
                Characters = { new CharacterOption { Id = "IRONCLAD", Selected = selected == "IRONCLAD" }, new CharacterOption { Id = "SILENT", Selected = selected == "SILENT" } },
            },
        };
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.SelectCharacter, Args = { [ActionArgs.Character] = "IRONCLAD" } });
        s.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.SelectCharacter, Args = { [ActionArgs.Character] = "SILENT" } });
        for (int a = 0; a <= 10; a++)
            s.Actions.Add(new ActionOption { Id = "a" + (2 + a), Kind = ActionKind.SetAscension, Args = { [ActionArgs.Ascension] = a.ToString() } });
        s.Actions.Add(new ActionOption { Id = "a13", Kind = ActionKind.Embark });
        return s;
    }

    [Fact]
    public void Filtered_applies_the_owner_rules_to_a_copy()
    {
        var settings = Settings();
        settings.Characters.Allowed = new() { "SILENT" };
        settings.Characters.Ascension = 3;
        var snap = CharacterSelect("SILENT", 0);
        int before = snap.Actions.Count;

        var filtered = OwnerRules.Filtered(snap, settings);

        Assert.Equal(before, snap.Actions.Count); // the original menu is untouched
        Assert.DoesNotContain(filtered.Actions, a => a.Kind == ActionKind.SelectCharacter && a.Arg(ActionArgs.Character) == "IRONCLAD");
        Assert.Contains(filtered.Actions, a => a.Kind == ActionKind.SelectCharacter && a.Arg(ActionArgs.Character) == "SILENT");
        // Only the step to the owner's ascension is left, and embarking waits for it.
        Assert.Equal(new int?[] { 3 }, filtered.Actions.Where(a => a.Kind == ActionKind.SetAscension).Select(a => a.IntArg(ActionArgs.Ascension)));
        Assert.DoesNotContain(filtered.Actions, a => a.Kind == ActionKind.Embark);

        var ready = OwnerRules.Filtered(CharacterSelect("SILENT", 3), settings);
        Assert.Contains(ready.Actions, a => a.Kind == ActionKind.Embark);
        Assert.DoesNotContain(ready.Actions, a => a.Kind == ActionKind.SetAscension);

        var wrong = OwnerRules.Filtered(CharacterSelect("IRONCLAD", 3), settings);
        Assert.DoesNotContain(wrong.Actions, a => a.Kind is ActionKind.Embark or ActionKind.SetAscension);
    }

    [Fact]
    public void Filtered_removes_continuing_a_disallowed_saved_run_and_potions_for_teammates()
    {
        var settings = Settings();
        settings.Characters.Allowed = new() { "SILENT" };
        settings.Team.AllowGivingPotions = false;
        var menu = new GameSnapshot { Screen = ScreenKind.MainMenu, SavedRun = new SavedRunInfo { CharacterId = "IRONCLAD", CharacterName = "Ironclad" } };
        menu.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.ContinueRun });
        menu.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.StartNewRun });
        var filtered = OwnerRules.Filtered(menu, settings);
        Assert.DoesNotContain(filtered.Actions, a => a.Kind == ActionKind.ContinueRun);
        Assert.NotEmpty(filtered.Notes);
        Assert.Contains(menu.Actions, a => a.Kind == ActionKind.ContinueRun);
        Assert.Empty(menu.Notes);

        var combat = TestData.Combat();
        combat.Actions.Add(new ActionOption { Id = "a4", Kind = ActionKind.UsePotion, SemanticKey = "potion:FIRE_POTION@P:2" });
        combat.Actions.Add(new ActionOption { Id = "a5", Kind = ActionKind.UsePotion, SemanticKey = "potion:BLOCK_POTION" });
        var noGiving = OwnerRules.Filtered(combat, settings);
        Assert.DoesNotContain(noGiving.Actions, a => a.Id == "a4");
        Assert.Contains(noGiving.Actions, a => a.Id == "a5");
    }

    [Fact]
    public void Prompt_preview_has_no_side_effects()
    {
        var settings = Settings();
        settings.Characters.Allowed = new() { "SILENT" };
        var hub = new TeamChatHub { LocalNetId = 1 };
        var engine = new DecisionEngine(() => settings, hub, new NullLog(), _ => new ScriptedClient());
        var snap = TestData.Combat();
        snap.IsMultiplayer = true;
        snap.Players.Add(new PlayerInfo { NetId = 2, Name = "Bob", Hp = 10, MaxHp = 70 });
        hub.Receive(new TeamMessage { From = 2, To = 1, FromName = "Bob", Kind = TeamMessageKind.RequestPotion, Text = "heal me please" });
        int actions = snap.Actions.Count;

        var (system, owner, user) = engine.PreviewPrompt(snap, "play defensively");
        var again = engine.PreviewPrompt(snap, "play defensively");

        Assert.False(string.IsNullOrWhiteSpace(system));
        Assert.Contains("SILENT", owner);
        Assert.Contains("heal me please", user);
        Assert.Contains("play defensively", user);
        Assert.Equal(user, again.User); // nothing was consumed by the first preview
        Assert.Single(hub.PeekAiInbox());
        Assert.Equal(actions, snap.Actions.Count);
    }

    [Theory]
    [InlineData(400, "Model Not Exist", true)]
    [InlineData(400, "Insufficient Balance", true)]
    [InlineData(400, "模型不存在，请检查模型代码。", true)]
    [InlineData(400, "API key not valid. Please pass a valid API key.", true)]
    [InlineData(400, "Your credit balance is too low to access the Anthropic API.", true)]
    [InlineData(400, "deepseek-chatt is not a valid model ID", true)]
    [InlineData(402, "Payment required", true)]
    [InlineData(400, "temperature must be between 0 and 2", false)]
    [InlineData(400, "This model's maximum context length is 8192 tokens", false)]
    [InlineData(429, "Rate limit reached", false)]
    public void Config_errors_are_recognised_across_providers(int status, string message, bool config)
    {
        Assert.Equal(config, new LlmException($"HTTP {status}: {message}", status).IsConfigError);
    }

    [Fact]
    public async Task A_timeout_is_retried_once_at_most()
    {
        int attempts = 0;
        var ex = await Assert.ThrowsAsync<LlmException>(() => LlmHttp.WithRetries<string>(() =>
        {
            attempts++;
            throw new LlmException("Request timed out after 1s.", retryable: true) { IsTimeout = true };
        }, maxRetries: 3, CancellationToken.None));
        Assert.True(ex.IsTimeout);
        Assert.Equal(2, attempts);

        attempts = 0;
        await Assert.ThrowsAsync<LlmException>(() => LlmHttp.WithRetries<string>(() =>
        {
            attempts++;
            throw new LlmException("HTTP 503: overloaded", 503, retryable: true, retryAfter: TimeSpan.FromMilliseconds(1));
        }, maxRetries: 2, CancellationToken.None));
        Assert.Equal(3, attempts); // other retryable errors keep their full retry count
    }

    [Fact]
    public async Task The_breaker_trips_again_on_the_first_failure_after_a_cooldown()
    {
        var settings = Settings();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var client = new ScriptedClient();
        var engine = new DecisionEngine(() => settings, new TeamChatHub(), new NullLog(), _ => client, () => now);

        for (int i = 0; i < 3; i++)
            Assert.Equal(DecisionSource.Fallback, (await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None))!.Source);
        Assert.Equal(3, client.Calls);

        // Open: no calls while cooling down.
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(3, client.Calls);

        // Half-open: one probe; its failure opens the breaker again at once (not after three more calls).
        now = now.AddMinutes(2);
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(4, client.Calls);
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(4, client.Calls);

        // A success closes it.
        now = now.AddMinutes(10);
        client.Answer = _ => "{\"thought\":\"ok\",\"actions\":[\"a2\"]}";
        Assert.Equal(DecisionSource.Llm, (await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None))!.Source);
        client.Answer = null;
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(7, client.Calls); // two failures after a success do not trip it
    }

    [Fact]
    public async Task New_llm_settings_or_a_reset_close_the_breaker()
    {
        var settings = Settings();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var client = new ScriptedClient();
        int built = 0;
        var engine = new DecisionEngine(() => settings, new TeamChatHub(), new NullLog(), _ => { built++; return client; }, () => now);
        for (int i = 0; i < 4; i++) await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(3, client.Calls);

        settings.Profiles[0].Model = "another-model";
        client.Answer = _ => "{\"thought\":\"ok\",\"actions\":[\"a2\"]}";
        Assert.Equal(DecisionSource.Llm, (await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None))!.Source);
        Assert.Equal(2, built);

        client.Answer = null;
        for (int i = 0; i < 4; i++) await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(7, client.Calls);
        engine.ResetCircuit();
        await engine.DecideAsync(TestData.Combat(), null, CancellationToken.None);
        Assert.Equal(8, client.Calls);
    }

    private const string AnthropicOk =
        "{\"type\":\"message\",\"stop_reason\":\"end_turn\",\"content\":[{\"type\":\"text\",\"text\":\"{}\"}],\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}";

    [Fact]
    public async Task An_unsupported_effort_falls_back_to_the_general_one_then_to_none()
    {
        var rejected = new HashSet<string> { "max" };
        using var server = new FakeLlmServer((r, _) =>
            r.Body!["output_config"]?["effort"]?.ToString() is { } effort && rejected.Contains(effort)
                ? (400, "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"output_config.effort: '" + effort + "' is not supported by this model\"}}")
                : (200, AnthropicOk));
        var profile = LlmPresets.FromPreset(LlmPresets.Find("anthropic")!);
        profile.ApiKey = "k";
        profile.BaseUrl = server.BaseUrl;
        profile.ReasoningEffort = "high";
        var client = new AnthropicClient(profile);
        LlmRequest Req(string? effort) => new() { Messages = { new LlmMessage(LlmRole.User, "x") }, Effort = effort };

        await client.CompleteAsync(Req("max"), CancellationToken.None);
        Assert.Equal(2, server.Requests.Count);
        Assert.Equal("high", server.Requests[1].Body!["output_config"]!["effort"]!.ToString());

        // The rejected value is remembered: the next combat call goes straight to the general effort.
        await client.CompleteAsync(Req("max"), CancellationToken.None);
        Assert.Equal(3, server.Requests.Count);
        Assert.Equal("high", server.Requests[2].Body!["output_config"]!["effort"]!.ToString());

        // When the general effort is rejected too, effort is dropped entirely.
        rejected.Add("high");
        await client.CompleteAsync(Req(null), CancellationToken.None);
        Assert.Null(server.Requests[^1].Body!["output_config"]?["effort"]);
        Assert.Equal(5, server.Requests.Count);
    }
}
