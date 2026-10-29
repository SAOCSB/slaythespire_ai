using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Game.Combat;
using SpirePilot.Game.Rooms;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// The local player's potions outside combat: those the game lets them use anywhere (AnyTime: Blood Potion, Fruit
/// Juice, ...), offered on the room screens next to the screen's own actions, never on a choice overlay. They are aimed
/// at the local player, or at no creature when the potion takes none. Teammates are not offered as targets outside
/// combat: the vanilla UI has no creatures to aim at there, and the reference mods (live-tested) aim at the owner too.
/// Keys follow the combat ones: "potion:&lt;ID&gt;@P:&lt;own netId&gt;" or "potion:&lt;ID&gt;@-".
/// </summary>
internal static class PotionReader
{
    static PotionReader()
    {
        L10n.Add("potion.drink_for_reward", "饮用药水 {0}（药水栏已满，可为奖励「{1}」腾出位置）",
            "Drink potion {0} (belt is full; frees a slot for the reward {1})");
    }

    private static readonly TimeSpan UseTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Adds the usable potions on an in-run room screen out of combat that already offers actions of its own (a potion
    /// alone never makes a screen actionable). Potions the screen's reader added already are not repeated.
    /// </summary>
    public static void AddOutOfCombat(GameContext ctx, ScreenProbe probe, ActionSink sink)
    {
        var me = ctx.Me;
        if (!ctx.InRun || ctx.Combat != null || me == null || probe.HumanOnlyReason != null || sink.Count == 0) return;
        bool roomScreen = probe.Kind is ScreenKind.Map or ScreenKind.CombatRewards or ScreenKind.Shop or ScreenKind.RestSite
                              or ScreenKind.Event or ScreenKind.Treasure ||
                          probe.Kind == ScreenKind.Combat && probe.CombatFinished;
        if (!roomScreen || UiActions.InputPending()) return;
        Add(sink, me, null);
    }

    /// <summary>At rewards with a full belt: drinking a usable potion frees a slot for <paramref name="rewardText"/> without wasting it.</summary>
    public static void AddDrinksForReward(ActionSink sink, Player me, string rewardText) => Add(sink, me, rewardText);

    private static void Add(ActionSink sink, Player me, string? rewardText)
    {
        if (RoomUtil.CombatInProgress()) return;
        var slots = me.PotionSlots;
        if (slots == null) return;
        for (int slot = 0; slot < slots.Count; slot++)
        {
            var potion = slots[slot];
            if (potion == null || !ModelText.PotionUsable(potion, inCombat: false)) continue;
            try
            {
                if (AimsAtOwner(potion, me) is not { } atOwner) continue;
                string id = ModelText.SafeId(potion);
                string key = "potion:" + id + "@" + (atOwner ? CombatKeys.PlayerSemantic(me) : "-");
                if (sink.FindBySemanticKey(key) != null) continue;
                string name = ModelText.ModelName(potion);
                if (name.Length == 0) name = id;
                string label = rewardText != null ? L10n.T("potion.drink_for_reward", name, rewardText) : L10n.T("act.use_potion", name);
                var p = potion;
                int s = slot;
                sink.Add(ActionKind.UsePotion, label, key, ct => Use(p, s, atOwner, ct),
                    new Dictionary<string, string> { [ActionArgs.Slot] = s.ToString() }, unpredictable: true);
            }
            catch (Exception ex) { ThrottledLog.Warn("potion:outside", "potion action failed: " + ex.Message); }
        }
    }

    /// <summary>
    /// Where the potion goes outside combat: true = the owner (self / any-player potions, unless the game's check
    /// rejects the owner but takes no creature), false = no creature; null for potions that need an enemy or an ally.
    /// </summary>
    private static bool? AimsAtOwner(PotionModel potion, Player me)
    {
        switch (potion.TargetType)
        {
            case TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy or TargetType.AnyAlly:
                return null;
            case TargetType.TargetedNoCreature or TargetType.AllAllies:
                return false;
        }
        var owner = SafeCreature(me);
        if (owner != null && Check(potion, owner) != false) return true;
        return Check(potion, null) == true ? false : null;
    }

    /// <summary>
    /// Uses the potion through <see cref="PotionModel.EnqueueManualUse"/> (the game's synchronized path), then waits
    /// until it left the belt (or asks the player for a choice) and the game settled.
    /// </summary>
    private static async Task<ActionResult> Use(PotionModel potion, int slot, bool atOwner, CancellationToken ct)
    {
        var me = RoomUtil.Me();
        if (me == null) return ActionResult.Fail("no local player");
        if (RoomUtil.CombatInProgress()) return ActionResult.Fail("a fight has started");
        if (UiActions.InputPending()) return ActionResult.Fail("a choice is open");
        if (!ReferenceEquals(SlotPotion(me, slot), potion)) return ActionResult.Fail("potion is no longer in that slot");
        if (!ModelText.PotionUsable(potion, inCombat: false)) return ActionResult.Fail("potion cannot be used now");
        Creature? target = null;
        if (atOwner)
        {
            target = SafeCreature(me);
            if (target == null || !SafeBool(() => target.IsAlive)) return ActionResult.Fail("the local player is dead");
        }

        try { potion.EnqueueManualUse(target); }
        catch (Exception ex) { return ActionResult.Fail("potion use failed: " + ex.Message); }

        bool Gone() => SafeBool(() => potion.HasBeenRemovedFromState) || !ReferenceEquals(SlotPotion(me, slot), potion);
        var result = await UiActions.Expect(() => Gone() || UiActions.InputPending(), "use potion", UseTimeout, ct);
        if (!result.Success && SafeBool(() => potion.IsQueued)) result = ActionResult.Ok("still resolving");
        await RoomUtil.SettleAsync(ct);
        return result;
    }

    private static PotionModel? SlotPotion(Player me, int slot)
    {
        try
        {
            var slots = me.PotionSlots;
            return slots != null && slot >= 0 && slot < slots.Count ? slots[slot] : null;
        }
        catch (Exception) { return null; }
    }

    private static Creature? SafeCreature(Player me)
    {
        try { return me.Creature; }
        catch (Exception) { return null; }
    }

    /// <summary>The game's target check: true / false, or null when the check itself threw.</summary>
    private static bool? Check(PotionModel potion, Creature? target)
    {
        try { return potion.IsValidTarget(target); }
        catch (Exception) { return null; }
    }

    private static bool SafeBool(Func<bool> get)
    {
        try { return get(); }
        catch (Exception) { return false; }
    }
}
