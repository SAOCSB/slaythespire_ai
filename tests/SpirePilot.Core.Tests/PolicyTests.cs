using System.Text.Json;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;

namespace SpirePilot.Core.Tests;

public class FallbackPolicyTests
{
    private static ActionOption A(string id, ActionKind kind, string label = "", params (string k, string v)[] args)
    {
        var a = new ActionOption { Id = id, Kind = kind, Label = label, SemanticKey = kind + ":" + id };
        foreach (var (k, v) in args) a.Args[k] = v;
        return a;
    }

    [Fact]
    public void Popup_is_dismissed()
    {
        var s = new GameSnapshot { Screen = ScreenKind.Popup, Actions = { A("a0", ActionKind.DismissPopup) } };
        Assert.Equal("a0", FallbackPolicy.Choose(s, new PilotSettings())!.Id);
    }

    [Fact]
    public void Rewards_take_everything_but_cards_then_leave()
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.CombatRewards,
            Rewards = new RewardInfo { Items = { new RewardItemInfo { Index = 0, Kind = "Card" }, new RewardItemInfo { Index = 1, Kind = "Gold", Amount = 20 } } },
            Actions = { A("a0", ActionKind.ClaimReward, "", ("index", "0")), A("a1", ActionKind.ClaimReward, "", ("index", "1")), A("a2", ActionKind.Proceed) },
        };
        Assert.Equal("a1", FallbackPolicy.Choose(s, new PilotSettings())!.Id);
        s.Actions.RemoveAt(1);
        Assert.Equal("a2", FallbackPolicy.Choose(s, new PilotSettings())!.Id);
    }

    [Fact]
    public void Rest_site_heals_when_low_and_smiths_otherwise()
    {
        GameSnapshot Rest(int hp) => new()
        {
            Screen = ScreenKind.RestSite,
            Players = { new PlayerInfo { IsLocal = true, Hp = hp, MaxHp = 80 } },
            Actions = { A("a0", ActionKind.ChooseRestOption, "Rest", ("id", "HEAL")), A("a1", ActionKind.ChooseRestOption, "Smith", ("id", "SMITH")) },
        };
        Assert.Equal("a0", FallbackPolicy.Choose(Rest(20), new PilotSettings())!.Id);
        Assert.Equal("a1", FallbackPolicy.Choose(Rest(70), new PilotSettings())!.Id);
    }

    [Fact]
    public void Card_reward_is_skipped_and_events_take_the_first_option()
    {
        var cr = new GameSnapshot { Screen = ScreenKind.CardReward, Actions = { A("a0", ActionKind.PickRewardCard), A("a1", ActionKind.SkipCardReward) } };
        Assert.Equal("a1", FallbackPolicy.Choose(cr, new PilotSettings())!.Id);
        var ev = new GameSnapshot { Screen = ScreenKind.Event, Actions = { A("a0", ActionKind.ChooseEventOption), A("a1", ActionKind.ChooseEventOption) } };
        Assert.Equal("a0", FallbackPolicy.Choose(ev, new PilotSettings())!.Id);
    }

    [Fact]
    public void Selection_confirms_once_enough_cards_are_selected()
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.CardGridSelect,
            Selection = new SelectionInfo { Purpose = "Upgrade", MinSelect = 1, MaxSelect = 1 },
            Actions = { A("a0", ActionKind.SelectCard), A("a1", ActionKind.ConfirmSelection) },
        };
        Assert.Equal("a0", FallbackPolicy.Choose(s, new PilotSettings())!.Id);
        s.Selection.SelectedKeys.Add("c0");
        Assert.Equal("a1", FallbackPolicy.Choose(s, new PilotSettings())!.Id);
    }

    [Fact]
    public void Map_follows_the_route_planner()
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.Map,
            Map = TestData.Map(),
            Players = { new PlayerInfo { IsLocal = true, Hp = 10, MaxHp = 80 } },
            Actions = { A("a0", ActionKind.ChooseMapNode, "", ("node", "0,0")), A("a1", ActionKind.ChooseMapNode, "", ("node", "1,0")) },
        };
        // Low HP: avoid the elite path.
        Assert.Equal("a1", FallbackPolicy.Choose(s, new PilotSettings())!.Id);
    }

    [Fact]
    public void Combat_ends_the_turn_when_nothing_sensible_is_playable()
    {
        var s = TestData.Combat();
        s.Actions.RemoveAll(a => a.Kind == ActionKind.PlayCard);
        Assert.Equal("a3", FallbackPolicy.Choose(s, new PilotSettings())!.Id);
    }
}

public class CharacterPickerTests
{
    private static GameSnapshot Select(string? selected = null)
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.CharacterSelect,
            CharacterSelect = new CharacterSelectInfo { MaxAscension = 5, Ascension = 0 },
        };
        int i = 0;
        foreach (var id in new[] { "IRONCLAD", "SILENT", "DEFECT" })
        {
            s.CharacterSelect.Characters.Add(new CharacterOption { Id = id, Selected = id == selected });
            s.Actions.Add(new ActionOption { Id = "a" + i++, Kind = ActionKind.SelectCharacter, Args = { [ActionArgs.Character] = id } });
        }
        for (int a = 0; a <= 5; a++)
            s.Actions.Add(new ActionOption { Id = "a" + i++, Kind = ActionKind.SetAscension, Args = { [ActionArgs.Ascension] = a.ToString() } });
        s.Actions.Add(new ActionOption { Id = "a" + i, Kind = ActionKind.Embark });
        return s;
    }

    [Fact]
    public void Round_robin_rotates_through_allowed_characters()
    {
        var settings = new PilotSettings();
        settings.Characters.Allowed = new() { "SILENT", "DEFECT" };
        settings.Characters.Policy = CharacterPickPolicy.RoundRobin;
        settings.Characters.RoundRobinCursor = 0;
        Assert.Equal("SILENT", CharacterPicker.Choose(Select(), settings)!.Arg(ActionArgs.Character));
        settings.Characters.RoundRobinCursor = 1;
        Assert.Equal("DEFECT", CharacterPicker.Choose(Select(), settings)!.Arg(ActionArgs.Character));
        settings.Characters.RoundRobinCursor = 2;
        Assert.Equal("SILENT", CharacterPicker.Choose(Select(), settings)!.Arg(ActionArgs.Character));
    }

    [Fact]
    public void Sets_the_desired_ascension_before_embarking()
    {
        var settings = new PilotSettings();
        settings.Characters.Ascension = 3;
        var s = Select("IRONCLAD");
        var pick = CharacterPicker.Choose(s, settings)!;
        Assert.Equal(ActionKind.SetAscension, pick.Kind);
        Assert.Equal(3, pick.IntArg(ActionArgs.Ascension));
        s.CharacterSelect!.Ascension = 3;
        Assert.Equal(ActionKind.Embark, CharacterPicker.Choose(s, settings)!.Kind);
        settings.Characters.Ascension = -1; // highest unlocked
        Assert.Equal(5, CharacterPicker.Choose(s, settings)!.IntArg(ActionArgs.Ascension));
    }

    [Fact]
    public void Locked_characters_are_never_picked()
    {
        var settings = new PilotSettings();
        settings.Characters.Allowed = new() { "DEFECT" };
        var s = Select();
        s.CharacterSelect!.Characters[2].Locked = true;
        // DEFECT is locked: the restriction is never widened to other characters.
        Assert.Empty(CharacterPicker.AllowedIds(s.CharacterSelect, settings));
        Assert.Null(CharacterPicker.Choose(s, settings));
    }
}

public class MemoryTests
{
    [Fact]
    public void Memo_replaces_strips_menu_ids_and_is_capped()
    {
        var m = new AgentMemory();
        m.SetMemo("use a3 then a12 next turn; keep fire potion");
        Assert.DoesNotContain("a3", m.Memo);
        Assert.Contains("keep fire potion", m.Memo);
        m.SetMemo("");
        Assert.Contains("keep fire potion", m.Memo); // empty keeps the old memo
        m.SetMemo(new string('x', 1000));
        Assert.Equal(AgentMemory.MaxMemoLength, m.Memo!.Length);
        m.Reset();
        Assert.Null(m.Memo);
    }

    [Fact]
    public void History_is_bounded()
    {
        var m = new AgentMemory { MaxHistory = 3 };
        for (int i = 0; i < 10; i++) m.AddHistory("h" + i);
        Assert.Equal(new[] { "h7", "h8", "h9" }, m.History);
    }
}

public class ManifestTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    [Theory]
    [InlineData("0.107", "0.107.1")]
    [InlineData("0.111", "0.111.0")]
    public void Manifests_match_the_build_version(string branch, string minGame)
    {
        var root = RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var version = System.Text.RegularExpressions.Regex.Match(props, "<SpirePilotVersion>(.*?)</SpirePilotVersion>").Groups[1].Value;
        var bytes = File.ReadAllBytes(Path.Combine(root, "src", "SpirePilot", "manifest", $"mod_manifest.{branch}.json"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "manifest must not start with a BOM");
        using var doc = JsonDocument.Parse(bytes);
        var m = doc.RootElement;
        Assert.Equal("SpirePilot", m.GetProperty("id").GetString());
        Assert.Equal(version, m.GetProperty("version").GetString());
        Assert.Equal(minGame, m.GetProperty("min_game_version").GetString());
        Assert.True(m.GetProperty("has_dll").GetBoolean());
        Assert.False(m.GetProperty("has_pck").GetBoolean());
        Assert.False(m.GetProperty("affects_gameplay").GetBoolean());
        Assert.Equal(JsonValueKind.Array, m.GetProperty("dependencies").ValueKind);
    }
}
