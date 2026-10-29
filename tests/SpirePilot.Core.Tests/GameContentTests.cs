using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;

namespace SpirePilot.Core.Tests;

/// <summary>Game content in the state text: glossary order, teammates' decks, potions outside combat, upgrade previews, boss notes.</summary>
public class GameContentTests
{
    private static GameSnapshot MapWithPotions()
    {
        var s = new GameSnapshot
        {
            Screen = ScreenKind.Map,
            InRun = true,
            CanAct = true,
            LocalPlayerId = 1,
            Run = new RunInfo { Act = 1, Floor = 5 },
            Players =
            {
                new PlayerInfo
                {
                    NetId = 1, Name = "Me", IsLocal = true, Hp = 30, MaxHp = 80, PotionSlots = 3,
                    Potions =
                    {
                        new PotionInfo { Slot = 0, Id = "BLOOD_POTION", Name = "Blood Potion", Target = "AnyPlayer", Description = "Heal 20% of your Max HP.", UsableNow = true },
                        new PotionInfo { Slot = 1, Id = "FIRE_POTION", Name = "Fire Potion", Target = "AnyEnemy", Description = "Deal 20 damage." },
                    },
                },
            },
            Map = TestData.Map(),
        };
        s.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.ChooseMapNode, Label = "Travel to Monster (0,0)", SemanticKey = "map:0,0", Args = { [ActionArgs.Node] = "0,0" } });
        s.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.UsePotion, Label = "Use potion Blood Potion", SemanticKey = "potion:BLOOD_POTION@P:1", Args = { [ActionArgs.Slot] = "0" } });
        return s;
    }

    [Fact]
    public void Glossary_prefers_terms_shown_on_screen_over_the_alphabetical_rest()
    {
        var snap = TestData.Combat();
        for (int i = 0; i < 30; i++) snap.Glossary[$"Aterm{i:00}"] = "filler";
        snap.Glossary["Vulnerable"] = "Takes 50% more damage from attacks.";
        snap.Combat!.Hand[0].Description = "Deal 6 damage. Apply 2 Vulnerable.";

        var text = new StateFormatter().Format(snap);
        var glossary = text[text.IndexOf("Glossary: ", StringComparison.Ordinal)..];
        Assert.StartsWith("Glossary: Vulnerable: Takes 50% more damage from attacks. | Aterm00: filler", glossary);
        Assert.Contains("Aterm22: filler", glossary);
        Assert.DoesNotContain("Aterm23", glossary);
    }

    [Fact]
    public void Glossary_counts_powers_potions_candidates_and_menu_labels_as_on_screen()
    {
        var snap = MapWithPotions();
        snap.LocalPlayer!.Powers.Add(new PowerInfo { Id = "PLATING", Name = "Plating", Amount = 4 });
        snap.Selection = new SelectionInfo { Candidates = { new CardInfo { Key = "c0", Name = "Forge Strike", Description = "Forge 3." } } };
        snap.Actions.Add(new ActionOption { Id = "a2", Kind = ActionKind.Proceed, Label = "Proceed (Sly cards stay)", SemanticKey = "proceed" });
        foreach (var term in new[] { "Plating", "Heal", "Forge", "Sly", "Zzz", "Yyy" }) snap.Glossary[term] = "tip";

        var text = new StateFormatter { MaxGlossaryEntries = 4 }.Format(snap);
        Assert.Contains("Glossary: Forge: tip | Heal: tip | Plating: tip | Sly: tip", text);
        Assert.DoesNotContain("Zzz", text);
    }

    [Fact]
    public void Teammate_deck_is_one_line_of_types_and_notable_cards_in_coop()
    {
        var snap = MapWithPotions();
        snap.IsMultiplayer = true;
        var mate = new PlayerInfo { NetId = 2, Name = "Mate", Hp = 40, MaxHp = 70, HasTeamLink = true };
        void Add(string name, string type, string rarity, bool upgraded = false) =>
            mate.Deck.Add(new CardInfo { Name = name, Type = type, Rarity = rarity, Upgraded = upgraded });
        Add("Strike", "Attack", "Basic");
        Add("Strike", "Attack", "Basic");
        Add("Defend", "Skill", "Basic");
        Add("Pommel Strike", "Attack", "Common", upgraded: true);
        Add("Pommel Strike", "Attack", "Common", upgraded: true);
        Add("Inflame", "Power", "Uncommon");
        Add("Demon Form", "Power", "Rare");
        Add("Wound", "Status", "Status");
        snap.Players.Add(mate);

        var text = new StateFormatter().Format(snap);
        Assert.Contains("  Deck (8): 4 Attack, 2 Power, 1 Skill, 1 Status; notable: Demon Form, Inflame, Pommel Strike+ x2", text);
    }

    [Fact]
    public void Teammate_deck_line_is_left_out_of_single_player_fights_and_unknown_decks()
    {
        var snap = MapWithPotions();
        snap.Players.Add(new PlayerInfo { NetId = 2, Name = "Mate", Deck = { new CardInfo { Name = "Strike", Type = "Attack", Rarity = "Basic" } } });
        Assert.DoesNotContain("Deck (1)", new StateFormatter().Format(snap));

        snap.IsMultiplayer = true;
        Assert.Contains("  Deck (1): 1 Attack", new StateFormatter().Format(snap));
        snap.Combat = TestData.Combat().Combat;
        Assert.DoesNotContain("  Deck (", new StateFormatter().Format(snap));

        snap.Combat = null;
        snap.Players[1].Deck.Clear();
        Assert.DoesNotContain("  Deck (", new StateFormatter().Format(snap));
    }

    [Fact]
    public void Potions_offered_outside_combat_are_marked_usable_now()
    {
        var text = new StateFormatter().Format(MapWithPotions());
        Assert.Contains("#0 Blood Potion [AnyPlayer] Heal 20% of your Max HP. (usable now); #1 Fire Potion [AnyEnemy] Deal 20 damage." + Environment.NewLine, text);

        var brief = new StateFormatter { VerboseCards = false }.Format(MapWithPotions());
        Assert.Contains("#0 Blood Potion (usable now); #1 Fire Potion", brief);
    }

    [Fact]
    public void Combat_potions_get_no_usable_now_marker()
    {
        var snap = TestData.Combat();
        snap.LocalPlayer!.Potions.Add(new PotionInfo { Slot = 0, Name = "Fire Potion", Target = "AnyEnemy", Description = "Deal 20 damage." });
        snap.Actions.Add(new ActionOption { Id = "a4", Kind = ActionKind.UsePotion, Label = "Use potion Fire Potion → Cultist", SemanticKey = "potion:FIRE_POTION@E:CULTIST#0", Args = { [ActionArgs.Slot] = "0" } });
        Assert.DoesNotContain("(usable now)", new StateFormatter().Format(snap));
        Assert.False(snap.IsOutOfCombatPotion(snap.Actions[^1]));
    }

    [Fact]
    public void Fallback_never_drinks_a_potion_outside_combat()
    {
        var snap = new GameSnapshot
        {
            Screen = ScreenKind.CombatRewards,
            InRun = true,
            CanAct = true,
            Players = { new PlayerInfo { NetId = 1, IsLocal = true, PotionSlots = 3 } },
            Rewards = new RewardInfo { Items = { new RewardItemInfo { Index = 0, Kind = "Card", Description = "Card reward" } } },
        };
        snap.Actions.Add(new ActionOption { Id = "a0", Kind = ActionKind.UsePotion, Label = "Use potion Blood Potion", SemanticKey = "potion:BLOOD_POTION@P:1", Args = { [ActionArgs.Slot] = "0" } });
        snap.Actions.Add(new ActionOption { Id = "a1", Kind = ActionKind.ClaimReward, Label = "Claim reward: Card reward", SemanticKey = "reward:card:x", Args = { [ActionArgs.Index] = "0" } });
        Assert.True(snap.IsOutOfCombatPotion(snap.Actions[0]));
        Assert.Equal("a1", FallbackPolicy.Choose(snap, new PilotSettings())?.Id);

        snap.Actions.RemoveAt(1);
        Assert.Null(FallbackPolicy.Choose(snap, new PilotSettings()));
    }

    [Fact]
    public void Upgrade_previews_are_printed_for_choices_and_rewards()
    {
        var snap = MapWithPotions();
        snap.Screen = ScreenKind.CardGridSelect;
        snap.Selection = new SelectionInfo
        {
            Purpose = "Upgrade",
            Candidates = { new CardInfo { Key = "c0", Name = "Bash", Type = "Attack", Cost = 2, Description = "Deal 8 damage. Apply 2 Vulnerable.", UpgradedDescription = "Deal 10 damage. Apply 3 Vulnerable." } },
        };
        snap.Rewards = new RewardInfo { CardChoices = { new CardInfo { Name = "Inflame", Type = "Power", Cost = 1, Description = "Gain 2 Strength.", UpgradedDescription = "(cost 0) Gain 2 Strength." } } };

        var text = new StateFormatter().Format(snap);
        Assert.Contains("c0 Bash [2] Attack: Deal 8 damage. Apply 2 Vulnerable. → upgraded: Deal 10 damage. Apply 3 Vulnerable.", text);
        Assert.Contains("Inflame [1] Power: Gain 2 Strength. → upgraded: (cost 0) Gain 2 Strength.", text);
    }

    [Fact]
    public void Boss_notes_are_printed_once_and_capped()
    {
        var snap = MapWithPotions();
        snap.Run!.BossName = "Ceremonial Beast";
        snap.Run.BossDescription = "Ceremonial Beast (HP 252): moves Stamp, Plow, Beast Cry, Stomp, Crush " + new string('x', 400);
        var text = new StateFormatter().Format(snap);
        var line = text.Split('\n').Single(l => l.StartsWith("Boss notes: ", StringComparison.Ordinal));
        Assert.StartsWith("Boss notes: Ceremonial Beast (HP 252): moves Stamp, Plow", line);
        Assert.Equal("Boss notes: ".Length + 300, line.TrimEnd('\r').Length);
    }

    [Fact]
    public void Boss_notes_are_left_out_of_fights()
    {
        var snap = TestData.Combat();
        snap.Run!.BossName = "Ceremonial Beast";
        snap.Run.BossDescription = "Ceremonial Beast (HP 252): moves Stamp, Plow";
        var text = new StateFormatter().Format(snap);
        Assert.Contains("Boss: Ceremonial Beast", text);
        Assert.DoesNotContain("Boss notes:", text);
    }
}
