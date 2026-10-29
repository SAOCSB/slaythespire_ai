using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using SpirePilot.Core.Model;

namespace SpirePilot.Game;

// OWNER: combat agent. STUB — to be replaced by the full implementation.
/// <summary>
/// Converts game models into the snapshot's plain data types with localized, BBCode-free text.
/// Every method is main-thread only, never throws (returns best-effort data instead), and never mutates the model.
/// </summary>
internal static class ModelText
{
    /// <summary>Localized text of a LocString with markup removed; "" when missing.</summary>
    public static string Loc(LocString? text)
    {
        try { return text == null ? "" : Clean(text.GetFormattedText()); }
        catch (Exception) { return ""; }
    }

    /// <summary>Removes BBCode / [img] tags and collapses whitespace.</summary>
    public static string Clean(string? text) => SpirePilot.Core.Agent.StateFormatter.Clean(text ?? "");

    /// <param name="card">The card.</param>
    /// <param name="key">Snapshot-local key, e.g. "h2" (hand slot) or "c4" (candidate 4).</param>
    /// <param name="inCombat">Resolve combat-dependent values (damage with modifiers, playability).</param>
    public static CardInfo Card(CardModel card, string key, bool inCombat) => new()
    {
        Key = key,
        Id = SafeId(card),
        Name = card.Title ?? "",
    };

    /// <summary>Potion in a belt slot; null for an empty slot.</summary>
    public static PotionInfo? Potion(PotionModel? potion, int slot, bool inCombat) =>
        potion == null ? null : new PotionInfo { Slot = slot, Id = SafeId(potion), Name = Loc(potion.Title) };

    public static RelicInfo Relic(RelicModel relic) => new() { Id = SafeId(relic), Name = Loc(relic.Title) };

    public static PowerInfo Power(PowerModel power) => new() { Id = SafeId(power) };

    /// <summary>A creature (enemy, pet, player creature) with hp/block/powers and, for monsters, intents.</summary>
    public static CreatureInfo Creature(Creature creature, string key) => new()
    {
        Key = key,
        Hp = creature.CurrentHp,
        MaxHp = creature.MaxHp,
        Block = creature.Block,
        IsAlive = creature.IsAlive,
    };

    /// <summary>Model id entry ("STRIKE_IRONCLAD"), or "" if unavailable.</summary>
    public static string SafeId(AbstractModel? model)
    {
        try { return model?.Id.Entry ?? ""; }
        catch (Exception) { return ""; }
    }
}
