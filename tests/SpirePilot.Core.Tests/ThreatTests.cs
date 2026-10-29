using SpirePilot.Core.Agent;
using SpirePilot.Core.Model;

namespace SpirePilot.Core.Tests;

public class ThreatTests
{
    private static string Fmt(GameSnapshot s) => new StateFormatter().Format(s);

    [Fact]
    public void Doom_at_or_above_hp_kills_at_turn_end_regardless_of_block()
    {
        var s = TestData.Combat(); var me = s.LocalPlayer!;
        me.Hp = 10; me.Block = 99; me.Powers.Add(new PowerInfo { Id = "DOOM_POWER", Name = "Doom", Amount = 10, IsDebuff = true });
        Assert.Contains("DOOM KILLS YOU WHEN THIS TURN ENDS", Fmt(s));
    }

    [Fact]
    public void Doom_below_hp_is_not_lethal_now_only_a_warning()
    {
        var s = TestData.Combat(); var me = s.LocalPlayer!;   // incoming 9
        me.Hp = 15; me.Powers.Add(new PowerInfo { Id = "DOOM_POWER", Amount = 8 });
        var t = Fmt(s);
        Assert.DoesNotContain("WOULD KILL YOU", t);          // old formula flagged this (8 >= 15 - 9)
        Assert.Contains("Doom kills you at the end of your next turn", t);
    }

    [Fact]
    public void Osty_absorbs_unblocked_attack_damage()
    {
        var s = TestData.Combat(); var me = s.LocalPlayer!;
        me.Hp = 8; me.Companions.Add(new CreatureInfo { Key = "p0", Id = "OSTY", Name = "Osty", Hp = 5, MaxHp = 5 });
        var t = Fmt(s);
        Assert.Contains("Threat: incoming 9, unblocked 9 (Osty takes up to 5, you take 4)", t);
        Assert.DoesNotContain("WOULD KILL YOU", t);
    }

    [Fact]
    public void Poison_and_constrict_add_up()
    {
        var s = TestData.Combat(); var me = s.LocalPlayer!;
        me.Hp = 15; me.Block = 4;
        me.Powers.Add(new PowerInfo { Id = "POISON_POWER", Amount = 3 });
        me.Powers.Add(new PowerInfo { Id = "CONSTRICT_POWER", Amount = 6 });
        // constrict 6 eats 4 block -> 2; attacks 9 unblocked; poison 3 -> 14 < 15
        Assert.DoesNotContain("WOULD KILL YOU", Fmt(s));
        me.Hp = 14;
        Assert.Contains("WOULD KILL YOU", Fmt(s));
    }
}
