using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Text;

namespace SpirePilot.Core.Tests;

public class StateFormatterTests
{
    [Theory]
    [InlineData("Deal [gold]6[/gold] damage.", "Deal 6 damage.")]
    [InlineData("Gain  5\nBlock.", "Gain 5 Block.")]
    [InlineData("Weird [unterminated", "Weird [unterminated")]
    [InlineData("", "")]
    public void Clean_strips_markup(string raw, string expected) => Assert.Equal(expected, StateFormatter.Clean(raw));

    [Fact]
    public void Formats_combat_with_keys_and_intents()
    {
        var text = new StateFormatter().Format(TestData.Combat());
        Assert.Contains("e0 Cultist HP 40/48", text);
        Assert.Contains("Intent: Attack 9", text);
        Assert.Contains("h0 Strike [1] Attack", text);
        Assert.Contains("Deal 6 damage.", text);
    }

    [Fact]
    public void Threat_line_flags_lethal_end_turn()
    {
        var snap = TestData.Combat();
        snap.LocalPlayer!.Hp = 8;
        var text = new StateFormatter().Format(snap);
        Assert.Contains("Threat: incoming 9, unblocked 9", text);
        Assert.Contains("WOULD KILL YOU", text);
        snap.LocalPlayer.Hp = 50;
        Assert.DoesNotContain("WOULD KILL YOU", new StateFormatter().Format(snap));
    }

    [Fact]
    public void Glossary_is_rendered_sorted_and_capped()
    {
        var snap = TestData.Combat();
        snap.Glossary["Vulnerable"] = "Takes [gold]50%[/gold] more damage from attacks.";
        snap.Glossary["Block"] = "Prevents damage until next turn.";
        var text = new StateFormatter { MaxGlossaryEntries = 1 }.Format(snap);
        Assert.Contains("Glossary: Block: Prevents damage until next turn.", text);
        Assert.DoesNotContain("Vulnerable:", text);
    }

    [Fact]
    public void User_prompt_lists_every_action()
    {
        var snap = TestData.Combat();
        var prompt = PromptBuilder.BuildUser(snap, new DecisionContext(), new StateFormatter());
        foreach (var a in snap.Actions) Assert.Contains($"{a.Id} | {a.Label}", prompt);
        Assert.EndsWith("Reply with the JSON object only.", prompt);
    }

    [Fact]
    public void System_prompt_contains_standing_instructions()
    {
        var settings = new PilotSettings();
        settings.Strategy.Instruction = "Build around poison.";
        settings.Characters.Allowed.Add("SILENT");
        settings.Team.AllowGivingPotions = false;
        var block = PromptBuilder.BuildOwnerBlock(settings, "English", CharacterGuide.For("SILENT"));
        Assert.Contains("Build around poison.", block);
        Assert.Contains("SILENT", block);
        Assert.Contains("Never use your potions on teammates", block);
        Assert.Contains("Poison", block);
        Assert.DoesNotContain("\r", block);
    }
}

public class SettingsTests
{
    [Fact]
    public void Roundtrips_and_normalizes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spirepilot-tests-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var store = new SettingsStore(path);
            var s = store.Load();
            Assert.Equal(PilotMode.Off, s.Control.Mode);
            s.Control.Mode = PilotMode.CombatOnly;
            s.Profiles.Add(LlmPresets.FromPreset(LlmPresets.Find("deepseek")!));
            s.ActiveProfile = "deepseek";
            s.Ui.Scale = 99;
            store.Save();

            var again = new SettingsStore(path).Load();
            Assert.Equal(PilotMode.CombatOnly, again.Control.Mode);
            Assert.Equal("deepseek", again.Active!.Name);
            Assert.Equal(3f, again.Ui.Scale);
            Assert.Contains("\"combatOnly\"", File.ReadAllText(path).Replace("CombatOnly", "combatOnly"), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    [Fact]
    public void Broken_file_falls_back_to_defaults_and_is_kept()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spirepilot-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            var store = new SettingsStore(path);
            var s = store.Load();
            Assert.NotNull(store.LoadError);
            Assert.NotNull(s.Active);
            Assert.True(File.Exists(path + ".broken"));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    [Fact]
    public void Validates_profiles()
    {
        var p = new LlmProfile { BaseUrl = "not a url", Model = "", ApiKey = "" };
        Assert.Contains("base_url", Llm.LlmClientFactory.Validate(p));
        var local = new LlmProfile { BaseUrl = "http://127.0.0.1:11434/v1", Model = "qwen" };
        Assert.Empty(Llm.LlmClientFactory.Validate(local));
    }
}

public class L10nTests
{
    [Theory]
    [InlineData("auto", "zhs", "zh")]
    [InlineData("auto", "eng", "en")]
    [InlineData("en", "zhs", "en")]
    [InlineData("zh", "eng", "zh")]
    public void Resolves_language(string setting, string game, string expected)
    {
        L10n.Configure(setting, game);
        Assert.Equal(expected, L10n.Language);
    }

    [Fact]
    public void Formats_and_falls_back_to_key()
    {
        L10n.Configure("en", null);
        Assert.Equal("Play Strike → Cultist", L10n.T("act.play_target", "Strike", "Cultist"));
        Assert.Equal("no.such.key", L10n.T("no.such.key"));
    }
}
