using System.Globalization;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace SpirePilot.Game.Combat;

/// <summary>
/// Semantic identity of a combat card: "&lt;CARD_ID&gt;+&lt;upgrades&gt;" followed by the variant data that makes this
/// copy play differently from a default copy of the same card, so plan steps and re-validation never pick the wrong
/// copy (an enchanted Strike, a Strike that costs 0 this turn, a dagger that grew this fight). Each part appears only
/// when it differs from the default, so truly identical copies share a key:
/// <list type="bullet">
/// <item><c>~c&lt;cost&gt;</c> the card's own energy cost (its local modifiers: "costs 0 this turn") differs from the
/// canonical cost; <c>~s&lt;stars&gt;</c> the same for stars. Global modifiers are left out: they apply to every copy
/// alike, so they tell no copy apart and would only make keys change between snapshots;</item>
/// <item><c>~&lt;ENCHANT_ID&gt;&lt;amount&gt;</c> enchantment, <c>~&lt;AFFLICTION_ID&gt;[amount]</c> affliction;</item>
/// <item><c>~retain</c>, <c>~sly</c> (keyword or this-turn flag), <c>~+&lt;keyword&gt;</c> / <c>~-&lt;keyword&gt;</c> other
/// keywords gained or lost against the card's canonical keywords;</item>
/// <item><c>~v&lt;hash&gt;</c> base dynamic values grew (or shrank) against the deck copy this card was made from (a short
/// hash of the base values, never of the description: its numbers follow Strength and the like).</item>
/// </list>
/// Main thread only; never throws (a part that cannot be read is left out).
/// </summary>
internal static class CardKeys
{
    /// <summary>The card's semantic identity, e.g. "STRIKE_IRONCLAD+0" or "STRIKE_IRONCLAD+0~c0~SHARP2".</summary>
    public static string Semantic(CardModel card)
    {
        var sb = new StringBuilder(48);
        sb.Append(ModelText.SafeId(card)).Append('+').Append(ModelText.UpgradeLevel(card));
        Part(() => Cost(sb, card));
        Part(() => Stars(sb, card));
        Part(() =>
        {
            var ench = card.Enchantment;
            if (ench != null) sb.Append('~').Append(ModelText.SafeId(ench)).Append(ench.Amount.ToString(CultureInfo.InvariantCulture));
        });
        Part(() =>
        {
            var aff = card.Affliction;
            if (aff == null) return;
            sb.Append('~').Append(ModelText.SafeId(aff));
            if (aff.Amount != 0) sb.Append(aff.Amount.ToString(CultureInfo.InvariantCulture));
        });
        Part(() => Keywords(sb, card));
        Part(() => Grown(sb, card));
        return sb.ToString();
    }

    private static void Cost(StringBuilder sb, CardModel card)
    {
        var cost = card.EnergyCost;
        if (cost == null || cost.CostsX) return;
        int now = Math.Max(0, cost.GetWithModifiers(CostModifiers.Local));
        if (now != Math.Max(0, cost.Canonical)) sb.Append("~c").Append(now.ToString(CultureInfo.InvariantCulture));
    }

    private static void Stars(StringBuilder sb, CardModel card)
    {
        if (card.HasStarCostX) return;
        int now = Math.Max(0, card.CurrentStarCost);
        int canonical = Math.Max(0, card.CanonicalStarCost);
        if (now != canonical) sb.Append("~s").Append(now.ToString(CultureInfo.InvariantCulture));
    }

    private static void Keywords(StringBuilder sb, CardModel card)
    {
        var current = new HashSet<CardKeyword>();
        foreach (var k in card.Keywords) if (k != CardKeyword.None) current.Add(k);
        Part(() => { if (card.ShouldRetainThisTurn) current.Add(CardKeyword.Retain); });
        Part(() => { if (card.IsSlyThisTurn) current.Add(CardKeyword.Sly); });
        Part(() => { if (card.ExhaustOnNextPlay) current.Add(CardKeyword.Exhaust); });
        var canonical = new HashSet<CardKeyword>();
        foreach (var k in card.CanonicalKeywords) if (k != CardKeyword.None) canonical.Add(k);

        var parts = new List<string>();
        foreach (var k in current)
        {
            if (canonical.Contains(k)) continue;
            parts.Add(k switch
            {
                CardKeyword.Retain => "retain",
                CardKeyword.Sly => "sly",
                _ => "+" + k.ToString().ToLowerInvariant(),
            });
        }
        foreach (var k in canonical)
            if (!current.Contains(k)) parts.Add("-" + k.ToString().ToLowerInvariant());
        parts.Sort(StringComparer.Ordinal);
        foreach (var p in parts) sb.Append('~').Append(p);
    }

    /// <summary>
    /// "~v&lt;hash&gt;" when the base values differ from the reference copy: the deck card this combat card was made from
    /// (same upgrade level), else the canonical card for an unupgraded card made during the fight. No reference, no part.
    /// </summary>
    private static void Grown(StringBuilder sb, CardModel card)
    {
        var own = card.DynamicVars;
        if (own == null || own.Count == 0) return;
        CardModel? reference = null;
        try
        {
            var deck = card.DeckVersion;
            if (deck != null && !ReferenceEquals(deck, card) && deck.CurrentUpgradeLevel == card.CurrentUpgradeLevel) reference = deck;
        }
        catch (Exception) { }
        if (reference == null)
        {
            try
            {
                var canonical = card.CanonicalInstance;
                if (canonical != null && !ReferenceEquals(canonical, card) && card.CurrentUpgradeLevel == 0) reference = canonical;
            }
            catch (Exception) { }
        }
        if (reference?.DynamicVars is not { } baseline) return;
        string mine = BaseValues(own);
        if (mine.Length == 0 || mine == BaseValues(baseline)) return;
        sb.Append("~v").Append(ShortHash(mine));
    }

    /// <summary>"name=value;" per variable in name order (the same text for identical copies).</summary>
    private static string BaseValues(DynamicVarSet set)
    {
        var parts = new List<string>();
        foreach (var v in set.Values)
            if (v != null) parts.Add(v.Name + "=" + v.BaseValue.ToString(CultureInfo.InvariantCulture));
        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    /// <summary>FNV-1a, 6 hex digits: deterministic across processes (unlike string.GetHashCode).</summary>
    private static string ShortHash(string text)
    {
        uint h = 2166136261;
        foreach (char c in text)
        {
            h ^= c;
            h *= 16777619;
        }
        return (h & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);
    }

    private static void Part(Action read)
    {
        try { read(); }
        catch (Exception) { /* this part stays out of the key */ }
    }
}
