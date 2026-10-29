using SpirePilot.Core.Text;

namespace SpirePilot.Game.Combat;

/// <summary>Extra mod strings used by the combat readers (labels with numbers, unplayable reasons).</summary>
internal static class CombatStrings
{
    static CombatStrings()
    {
        L10n.Add("lbl.dmg", "{0} 伤害", "{0} dmg");
        L10n.Add("lbl.dmg_hits", "{0}×{1} 伤害", "{0}×{1} dmg");
        L10n.Add("lbl.block", "{0} 格挡", "{0} block");
        L10n.Add("lbl.kills", "可击杀", "kills");
        L10n.Add("lbl.kills_n", "可击杀 {0} 个", "kills {0}");
        L10n.Add("lbl.x_energy", "X={0}", "X={0}");
        L10n.Add("lbl.self", "自己", "yourself");
        L10n.Add("lbl.copies", "{0} ×{1}", "{0} ×{1}");
        L10n.Add("why.energy", "能量不足", "not enough energy");
        L10n.Add("why.stars", "星星不足", "not enough stars");
        L10n.Add("why.unplayable", "无法打出", "unplayable");
        L10n.Add("why.blocked_by", "被「{0}」阻止", "blocked by {0}");
        L10n.Add("why.blocked", "当前无法打出", "cannot be played right now");
        L10n.Add("why.no_allies", "没有存活的队友", "no living allies");
        L10n.Add("player.n", "玩家 {0}", "Player {0}");
        L10n.Add("act.undo_end_turn", "撤销结束回合（继续出牌）", "Undo end turn (keep playing)");
        L10n.Add("act.combat_proceed", "继续（离开已结束的战斗）", "Proceed (leave the finished fight)");
    }

    /// <summary>Touch to make sure the strings are registered (runs the static constructor once).</summary>
    public static void Ensure() { }
}
