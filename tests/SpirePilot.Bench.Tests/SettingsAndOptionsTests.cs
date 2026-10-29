using System.Text.Json;
using System.Text.Json.Nodes;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Team;

namespace SpirePilot.Bench.Tests;

public class SettingsAndOptionsTests
{
    private static string Temp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bench-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void A_single_profile_file_becomes_the_active_profile()
    {
        var path = Temp("""{ "provider": "OpenAiCompatible", "baseUrl": "https://api.deepseek.com/v1", "model": "deepseek-chat", "apiKey": "env:DEEPSEEK_KEY" }""");
        try
        {
            var s = BenchSettings.Load(path);
            Assert.Single(s.Profiles);
            Assert.Equal("deepseek-chat", s.Active!.Model);
            Assert.Equal(LlmProvider.OpenAiCompatible, s.Active.Provider);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), s.Active.Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_settings_file_keeps_only_the_profiles_unless_asked()
    {
        var path = Temp("""
        {
          // comments and trailing commas are fine, like the mod's own reader
          "route": { "preset": "Safe", "instruction": "never fight elites" },
          "profiles": [
            { "name": "claude", "provider": "Anthropic", "baseUrl": "https://api.anthropic.com", "model": "claude-opus-5-5", "apiKey": "k1" },
            { "name": "qwen", "provider": "OpenAiCompatible", "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1", "model": "qwen-plus", "apiKey": "k2" },
          ],
          "activeProfile": "claude",
        }
        """);
        try
        {
            var clean = BenchSettings.Load(path, profile: "QWEN");
            Assert.Equal("qwen", clean.Active!.Name);
            Assert.Equal(RoutePreset.Balanced, clean.Route.Preset);
            Assert.Equal("", clean.Route.Instruction);

            var kept = BenchSettings.Load(path, keepOwnerSettings: true);
            Assert.Equal("claude", kept.Active!.Name);
            Assert.Equal(RoutePreset.Safe, kept.Route.Preset);

            var ex = Assert.Throws<ArgumentException>(() => BenchSettings.Load(path, profile: "gpt"));
            Assert.Contains("claude, qwen", ex.Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Fixture_overrides_merge_but_never_replace_the_profile()
    {
        var baseSettings = TestSupport.Settings();
        baseSettings.Profiles[0].Model = "my-model";
        var f = TestSupport.Combat();
        f.Settings = JsonNode.Parse("""
        {
          "Route": { "Preset": "Aggressive", "pinnedPath": ["1,2"], "pinnedAct": 1 },
          "team": { "proactiveRequests": false },
          "profiles": [ { "name": "evil", "model": "other" } ],
          "activeProfile": "evil",
          "control": { "fallbackOnLlmFailure": true }
        }
        """)!.AsObject();
        var s = BenchSettings.ForFixture(baseSettings, f);
        Assert.Equal(RoutePreset.Aggressive, s.Route.Preset);
        Assert.Equal(new[] { "1,2" }, s.Route.PinnedPath);
        Assert.False(s.Team.ProactiveRequests);
        Assert.True(s.Team.AutoReply); // untouched values keep the base settings
        Assert.Equal("my-model", s.Active!.Model);
        Assert.Single(s.Profiles);
        // The benchmark scores the model: LLM failures are errors, never fallback answers.
        Assert.False(s.Control.FallbackOnLlmFailure);
        Assert.Contains(FixtureValidator.Validate(f), p => p.Contains("profiles"));
    }

    [Fact]
    public void Dry_run_settings_get_a_placeholder_key_only_when_missing()
    {
        var none = BenchSettings.ForDryRun(null);
        Assert.Empty(LlmClientFactory.Validate(none.Active!));
        var mine = TestSupport.Settings();
        Assert.Equal("test-key", BenchSettings.ForDryRun(mine).Active!.ApiKey);
    }

    [Fact]
    public void Options_parse()
    {
        var o = BenchOptions.Parse(new[] { "--settings", "s.json", "--profile=qwen", "--filter", "coop,map", "--filter", "zh", "--repeat", "3", "--out", "r.md" });
        Assert.Equal("s.json", o.SettingsPath);
        Assert.Equal("qwen", o.Profile);
        Assert.Equal(new[] { "coop", "map", "zh" }, o.Filters);
        Assert.Equal(3, o.Repeat);
        Assert.Equal("r.md", o.OutPath);

        Assert.True(BenchOptions.Parse(new[] { "--dry-run" }).DryRun);
        Assert.True(BenchOptions.Parse(new[] { "--validate" }).Validate);
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(new[] { "--dry-run", "--repeat", "0" }));
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(new[] { "--dry-run", "--settings" }));
        Assert.Throws<ArgumentException>(() => BenchOptions.Parse(new[] { "--dry-run", "--bogus" }));
    }

    [Fact]
    public void Filter_matches_id_file_name_and_tags()
    {
        var f = TestSupport.LoadFixture("coop-debuff-request-bash.json");
        Assert.True(f.MatchesFilter("DEBUFF"));
        Assert.True(f.MatchesFilter("ironclad"));
        Assert.True(f.MatchesFilter(".json"));
        Assert.False(f.MatchesFilter("necrobinder"));
    }

    [Fact]
    public void Validator_reports_broken_fixtures()
    {
        var f = TestSupport.Combat().With(s =>
        {
            s.Actions[1].Id = "a7";
            s.Actions[2].Args["index"] = "9";
            s.Actions[3].Args["target"] = "e5";
        });
        f.Rationale = "";
        f.ExpectAny = new() { "play:NOPE@-" };
        f.Forbid = new() { "play:STRIKE_IRONCLAD+0@E:CULTIST#0" };
        f.ExpectMessageKind = new() { "acept" };
        f.ExpectReplyTo = "m9";
        f.Inbox.Add(new TeamMessage { Id = "m1", From = 1, To = 1, Text = "self" });
        var problems = FixtureValidator.Validate(f);
        Assert.Contains(problems, p => p.Contains("rationale"));
        Assert.Contains(problems, p => p.Contains("\"a7\", expected \"a1\""));
        Assert.Contains(problems, p => p.Contains("hand index 9"));
        Assert.Contains(problems, p => p.Contains("targets \"e5\""));
        Assert.Contains(problems, p => p.Contains("\"play:NOPE@-\" matches no action"));
        Assert.Contains(problems, p => p.Contains("unknown message kind \"acept\""));
        Assert.Contains(problems, p => p.Contains("isMultiplayer"));
        Assert.Contains(problems, p => p.Contains("expect_reply_to \"m9\""));
        Assert.Contains(problems, p => p.Contains("comes from the local player"));

        var both = TestSupport.Combat();
        both.Forbid.Add("play:STRIKE_IRONCLAD+0@*");
        Assert.Contains(FixtureValidator.Validate(both), p => p.Contains("both expected and forbidden"));

        var nothing = TestSupport.Combat();
        nothing.ExpectAny.Clear();
        nothing.Forbid.Clear();
        Assert.Contains(FixtureValidator.Validate(nothing), p => p.Contains("checks nothing"));
        Assert.Empty(FixtureValidator.Validate(TestSupport.Combat()));
        Assert.Empty(FixtureValidator.Validate(TestSupport.Combat(multiplayer: true)));
    }

    [Theory]
    [InlineData("\"request_potion\"", TeamMessageKind.RequestPotion)]
    [InlineData("\"RequestDebuff\"", TeamMessageKind.RequestDebuff)]
    [InlineData("\"accept\"", TeamMessageKind.Accept)]
    [InlineData("8", TeamMessageKind.Accept)]
    public void Message_kinds_read_wire_and_enum_names(string json, TeamMessageKind expected)
    {
        var m = JsonSerializer.Deserialize<TeamMessage>($$"""{ "kind": {{json}}, "text": "x" }""", FixtureLoader.JsonOptions)!;
        Assert.Equal(expected, m.Kind);
    }
}
