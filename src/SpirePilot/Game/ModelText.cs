using System.Runtime.CompilerServices;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Game.Combat;

namespace SpirePilot.Game;

/// <summary>
/// Converts game models into the snapshot's plain data types with localized, BBCode-free text.
/// Every method is main-thread only, never throws (returns best-effort data instead), and never changes game
/// state. (Card descriptions refresh the card's own dynamic-variable preview first, exactly like the game's card UI
/// does whenever it renders a card; that preview is display-only.)
/// </summary>
internal static class ModelText
{
    /// <summary>
    /// The game's text language ("eng", "zhs", ...; "" before localization is ready). Caches of game text key on it: the
    /// player can switch it at any time, independently of the mod's own language.
    /// </summary>
    public static string GameLanguage()
    {
        try { return LocManager.Instance?.Language ?? ""; }
        catch (Exception) { return ""; }
    }

    /// <summary>Localized text of a LocString with markup removed; "" when missing.</summary>
    public static string Loc(LocString? text)
    {
        try { return text == null ? "" : Clean(text.GetFormattedText()); }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// Removes BBCode / game tags and collapses whitespace. Inline icons become readable symbols
    /// (<c>[img]…energy…[/img]</c> → ⚡, star icon → ★, other images are dropped) so "Gain [E]" stays meaningful.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        try { return SpirePilot.Core.Agent.StateFormatter.Clean(ReplaceIcons(text)); }
        catch (Exception) { return text; }
    }

    private static string ReplaceIcons(string text)
    {
        if (text.IndexOf("[img", StringComparison.OrdinalIgnoreCase) < 0) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '[' && string.Compare(text, i, "[img", 0, 4, StringComparison.OrdinalIgnoreCase) == 0)
            {
                int open = text.IndexOf(']', i);
                int close = open < 0 ? -1 : text.IndexOf("[/img]", open, StringComparison.OrdinalIgnoreCase);
                if (open >= 0 && close >= 0)
                {
                    var path = text.Substring(open + 1, close - open - 1).ToLowerInvariant();
                    if (path.Contains("energy")) sb.Append('⚡');
                    else if (path.Contains("star")) sb.Append('★');
                    i = close + "[/img]".Length;
                    continue;
                }
            }
            sb.Append(text[i]);
            i++;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ cards

    /// <param name="card">The card.</param>
    /// <param name="key">Snapshot-local key, e.g. "h2" (hand slot) or "c4" (candidate 4).</param>
    /// <param name="inCombat">
    /// Resolve combat-dependent values: cost with every modifier, and (for cards in hand) playability. Outside combat
    /// only card-local cost modifiers apply.
    /// </param>
    public static CardInfo Card(CardModel card, string key, bool inCombat)
    {
        var info = new CardInfo { Key = key ?? "" };
        if (card == null) return info;
        info.Id = SafeId(card);
        Try(() => info.Upgraded = card.IsUpgraded);
        info.Name = CardName(card);
        Try(() => info.Type = card.Type.ToString());
        Try(() => info.Rarity = card.Rarity.ToString());
        Try(() => info.Target = card.TargetType.ToString());
        Try(() => info.Keywords = Keywords(card));
        Try(() => info.Cost = EnergyCost(card, inCombat, info.Keywords.Contains(nameof(CardKeyword.Unplayable))));
        Try(() => info.StarCost = StarCost(card, inCombat));
        Try(() => info.GainsBlock = card.GainsBlock);
        info.Description = CardDescription(card);
        if (inCombat)
        {
            try
            {
                if (card.Pile?.Type == PileType.Hand)
                {
                    bool ok = card.CanPlay(out var reason, out var preventer);
                    info.Playable = ok;
                    if (!ok) info.UnplayableReason = UnplayableText(reason, preventer);
                }
            }
            catch (Exception) { /* leave unknown */ }
        }
        return info;
    }

    // Pile listings (draw / discard / exhaust) only need names: the prompt prints names, and the full rules text costs a
    // dynamic-variable preview plus formatting per card on every snapshot. The localized name and the static fields are
    // cached per card instance and refreshed when the card's id, upgrade level, enchantment or the game language changes.
    private static readonly ConditionalWeakTable<CardModel, PileEntry> PileCache = new();

    private sealed class PileEntry
    {
        public string Signature = "";
        public string Name = "";
        public string Type = "";
        public string Rarity = "";
        public string Target = "";
        public bool Upgraded;
    }

    /// <summary>
    /// A card in a pile: id, name, type, rarity, target, keywords, energy / star cost and "gains block", without rules
    /// text or playability. Main thread only, never throws.
    /// </summary>
    public static CardInfo PileCard(CardModel card, bool inCombat)
    {
        var info = new CardInfo();
        if (card == null) return info;
        info.Id = SafeId(card);
        string sig = info.Id + "+" + UpgradeLevel(card) + "|" + GameLanguage();
        try
        {
            var ench = card.Enchantment;
            if (ench != null) sig += "~" + SafeId(ench);
        }
        catch (Exception) { }
        if (!PileCache.TryGetValue(card, out var entry) || entry.Signature != sig)
        {
            entry = new PileEntry { Signature = sig, Name = CardName(card) };
            var e = entry;
            Try(() => e.Upgraded = card.IsUpgraded);
            Try(() => e.Type = card.Type.ToString());
            Try(() => e.Rarity = card.Rarity.ToString());
            Try(() => e.Target = card.TargetType.ToString());
            PileCache.AddOrUpdate(card, entry);
        }
        info.Name = entry.Name;
        info.Upgraded = entry.Upgraded;
        info.Type = entry.Type;
        info.Rarity = entry.Rarity;
        info.Target = entry.Target;
        Try(() => info.Keywords = Keywords(card));
        Try(() => info.Cost = EnergyCost(card, inCombat, info.Keywords.Contains(nameof(CardKeyword.Unplayable))));
        Try(() => info.StarCost = StarCost(card, inCombat));
        Try(() => info.GainsBlock = card.GainsBlock);
        return info;
    }

    /// <summary>Localized card name with the upgrade marker ("Strike+", "Searing Blow+3").</summary>
    public static string CardName(CardModel card)
    {
        string name = "";
        try { name = card.Title ?? ""; } catch (Exception) { }
        if (name.Length == 0) name = Loc(SafeGet(() => card.TitleLocString));
        if (name.Length == 0) name = SafeId(card);
        name = Clean(name);
        try
        {
            int level = card.CurrentUpgradeLevel;
            if (level > 0 && !name.Contains('+')) name += level > 1 ? "+" + level : "+";
        }
        catch (Exception) { }
        return name;
    }

    /// <summary>Upgrade level used in semantic keys (0 when not upgraded).</summary>
    public static int UpgradeLevel(CardModel card)
    {
        try { return Math.Max(0, card.CurrentUpgradeLevel); }
        catch (Exception) { return 0; }
    }

    private static List<string> Keywords(CardModel card)
    {
        var list = new List<string>();
        foreach (var k in card.Keywords)
            if (k != CardKeyword.None) list.Add(k.ToString());
        // Single-turn flags are not part of Keywords.
        Try(() => { if (card.ShouldRetainThisTurn && !list.Contains("Retain")) list.Add("Retain"); });
        Try(() => { if (card.IsSlyThisTurn && !list.Contains("Sly")) list.Add("Sly"); });
        Try(() => { if (card.ExhaustOnNextPlay && !list.Contains("Exhaust")) list.Add("Exhaust"); });
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>Energy cost as the card shows it; -1 for X; null for unplayable cards (statuses / curses).</summary>
    private static int? EnergyCost(CardModel card, bool inCombat, bool unplayable)
    {
        if (unplayable) return null;
        var cost = card.EnergyCost;
        if (cost == null) return null;
        if (cost.CostsX) return -1;
        try { return Math.Max(0, cost.GetWithModifiers(inCombat ? CostModifiers.All : CostModifiers.Local)); }
        catch (Exception) { return Math.Max(0, cost.Canonical); }
    }

    /// <summary>Star cost (Regent); null when the card has none, -1 for X stars.</summary>
    private static int? StarCost(CardModel card, bool inCombat)
    {
        if (card.HasStarCostX) return -1;
        int current = card.CurrentStarCost;
        if (current < 0) return null;
        int value = current;
        if (inCombat)
        {
            try { value = card.GetStarCostWithModifiers(); }
            catch (Exception) { value = current; }
        }
        value = Math.Max(0, value);
        return value > 0 ? value : null;
    }

    /// <summary>
    /// Rules text with dynamic values resolved the way the card UI shows it (owner's modifiers included, target-specific
    /// ones only while the card is being aimed). Falls back to the raw text.
    /// </summary>
    public static string CardDescription(CardModel card)
    {
        string text = "";
        try
        {
            Creature? target = SafeGet(() => card.CurrentTarget);
            try { card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, card.DynamicVars); }
            catch (Exception) { /* previews unavailable (no combat): the stored values still format */ }
            var pile = SafeGet(() => card.Pile)?.Type ?? PileType.None;
            text = Clean(card.GetDescriptionForPile(pile, target));
        }
        catch (Exception) { }
        if (text.Length == 0) text = Loc(SafeGet(() => card.Description));
        if (text.Length == 0)
        {
            try { text = Clean(card.Description?.GetRawText()); }
            catch (Exception) { }
        }
        return WithModifiers(text, card);
    }

    /// <summary><paramref name="text"/> followed by the card's enchantment and affliction, e.g. "… (Sharp 2)".</summary>
    private static string WithModifiers(string text, CardModel card)
    {
        try
        {
            var ench = card.Enchantment;
            if (ench != null)
            {
                var title = Loc(ench.Title);
                int amount = 0;
                Try(() => amount = ench.ShowAmount ? ench.DisplayAmount : 0);
                if (title.Length > 0) text += (text.Length > 0 ? " " : "") + "(" + title + (amount != 0 ? " " + amount : "") + ")";
            }
        }
        catch (Exception) { }
        try
        {
            var aff = card.Affliction;
            if (aff != null)
            {
                var title = Loc(aff.Title);
                if (title.Length > 0) text += (text.Length > 0 ? " " : "") + "(" + title + (aff.Amount != 0 ? " " + aff.Amount : "") + ")";
            }
        }
        catch (Exception) { }
        return text;
    }

    // ------------------------------------------------------------------ upgrade previews

    /// <summary>
    /// A detached, upgraded copy of a card's canonical model (never the live card, which stays untouched) with its
    /// text out of combat. The copy depends only on the card id, the upgrade level and the game language; its last
    /// in-combat text is kept for the live text it was previewed against.
    /// </summary>
    private sealed record UpgradedCopy(CardModel Card, string Text, int? Cost)
    {
        public string? PreviewedFor { get; set; }
        public string PreviewedText { get; set; } = "";
    }

    private static readonly Dictionary<string, UpgradedCopy?> UpgradedCopies = new();
    private const int MaxUpgradedCopies = 256;

    /// <summary>
    /// Rules text of <paramref name="card"/> after its next upgrade, with the energy cost in front when the upgrade
    /// changes it ("(cost 1) Deal 9 damage."); null when the card cannot be upgraded, the preview failed or nothing
    /// visible changes. The game's own preview text (<see cref="CardModel.GetDescriptionForUpgradePreview"/>) only
    /// differs for a card that has just been upgraded, so it is read from an upgraded copy of the canonical card. In a
    /// fight the copy's numbers are previewed through the live card (Strength and the like, as its own text shows them).
    /// Main thread only, never throws.
    /// </summary>
    public static string? UpgradePreview(CardModel card, string currentText, bool inCombat)
    {
        try
        {
            if (card == null || !card.IsUpgradable) return null;
            int level = UpgradeLevel(card);
            string key = SafeId(card) + "+" + level + "|" + GameLanguage();
            if (!UpgradedCopies.TryGetValue(key, out var copy))
            {
                if (UpgradedCopies.Count >= MaxUpgradedCopies) UpgradedCopies.Clear();
                copy = BuildUpgradedCopy(card, level);
                UpgradedCopies[key] = copy;
            }
            if (copy == null) return null;

            string text = copy.Text;
            if (inCombat)
            {
                if (copy.PreviewedFor != currentText)
                {
                    copy.PreviewedText = "";
                    try
                    {
                        card.UpdateDynamicVarPreview(CardPreviewMode.Normal, null, copy.Card.DynamicVars);
                        copy.PreviewedText = UpgradedText(copy.Card);
                    }
                    catch (Exception) { /* keep the base values */ }
                    copy.PreviewedFor = currentText;
                }
                if (copy.PreviewedText.Length > 0) text = copy.PreviewedText;
            }
            text = WithModifiers(text, card);
            int? cost = null;
            try { cost = card.EnergyCost is { CostsX: false } live ? live.Canonical : null; }
            catch (Exception) { }
            bool costChanged = copy.Cost is { } upgradedCost && cost is { } liveCost && upgradedCost != liveCost;
            if (!costChanged && text == currentText) return null;
            return costChanged ? $"(cost {copy.Cost}) {text}" : text;
        }
        catch (Exception) { return null; }
    }

    private static UpgradedCopy? BuildUpgradedCopy(CardModel card, int level)
    {
        try
        {
            var canonical = card.CanonicalInstance ?? ModelDb.GetByIdOrNull<CardModel>(card.Id);
            if (canonical == null) return null;
            var copy = canonical.ToMutable();
            for (int i = 0; i <= level; i++) copy.UpgradeInternal();
            var text = UpgradedText(copy);
            if (text.Length == 0) return null;
            int? cost = null;
            try { cost = copy.EnergyCost is { CostsX: false } c ? c.Canonical : null; }
            catch (Exception) { }
            return new UpgradedCopy(copy, text, cost);
        }
        catch (Exception) { return null; }
    }

    private static string UpgradedText(CardModel upgraded)
    {
        string text = "";
        try { text = Clean(upgraded.GetDescriptionForUpgradePreview()); }
        catch (Exception) { }
        if (text.Length == 0)
        {
            try { text = Clean(upgraded.GetDescriptionForPile(PileType.None)); }
            catch (Exception) { }
        }
        return text;
    }

    /// <summary>Short localized reason why a card cannot be played.</summary>
    public static string UnplayableText(UnplayableReason reason, AbstractModel? preventer)
    {
        CombatStrings.Ensure();
        if (reason.HasFlag(UnplayableReason.EnergyCostTooHigh)) return L10n.T("why.energy");
        if (reason.HasFlag(UnplayableReason.StarCostTooHigh)) return L10n.T("why.stars");
        if (reason.HasFlag(UnplayableReason.NoLivingAllies)) return L10n.T("why.no_allies");
        if (reason.HasFlag(UnplayableReason.HasUnplayableKeyword)) return L10n.T("why.unplayable");
        var by = ModelName(preventer);
        if (by.Length > 0) return L10n.T("why.blocked_by", by);
        if (reason.HasFlag(UnplayableReason.BlockedByCardLogic)) return L10n.T("why.unplayable");
        return L10n.T("why.blocked");
    }

    /// <summary>Display name of any model (card, relic, power, potion, monster ...), "" if unknown.</summary>
    public static string ModelName(AbstractModel? model)
    {
        try
        {
            return model switch
            {
                null => "",
                CardModel c => CardName(c),
                RelicModel r => Loc(r.Title),
                PowerModel p => Loc(p.Title),
                PotionModel p => Loc(p.Title),
                MonsterModel m => Loc(m.Title),
                OrbModel o => Loc(o.Title),
                _ => SafeId(model),
            };
        }
        catch (Exception) { return SafeId(model); }
    }

    // ------------------------------------------------------------------ dynamic numbers (labels)

    /// <summary>Damage / hits / block a card would produce against <paramref name="target"/> (null = untargeted).</summary>
    public readonly record struct CardNumbers(int? Damage, int Hits, int? Block);

    /// <summary>
    /// Previews the card's dynamic variables against a target on a detached copy of its variable set (the live card is
    /// not touched). Null when the card has no damage/block numbers or the preview failed.
    /// </summary>
    public static CardNumbers? Numbers(CardModel card, Creature? target)
    {
        try
        {
            var set = card.DynamicVars?.Clone(card);
            if (set == null) return null;
            card.UpdateDynamicVarPreview(CardPreviewMode.Normal, target, set);
            int? dmg = Var(set, CalculatedDamageVar.defaultName) ?? Var(set, DamageVar.defaultName) ?? Var(set, OstyDamageVar.defaultName);
            int? block = Var(set, CalculatedBlockVar.defaultName) ?? Var(set, BlockVar.defaultName);
            int hits = Math.Max(1, Var(set, RepeatVar.defaultName) ?? 1);
            if (dmg == null && block == null) return null;
            return new CardNumbers(dmg, hits, block);
        }
        catch (Exception) { return null; }
    }

    private static int? Var(DynamicVarSet set, string name)
    {
        if (!set.TryGetValue(name, out var v) || v == null) return null;
        return (int)Math.Max(0, Math.Floor(v.PreviewValue));
    }

    /// <summary>Base value of a potion's dynamic variable (potions have no per-target preview).</summary>
    public static int? PotionVar(PotionModel potion, string name)
    {
        try
        {
            var set = potion.DynamicVars;
            if (set == null || !set.TryGetValue(name, out var v) || v == null) return null;
            return (int)Math.Max(0, Math.Floor(v.BaseValue));
        }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------------ potions / relics / powers / orbs

    /// <summary>Potion in a belt slot; null for an empty slot.</summary>
    /// <param name="inCombat">A fight is in progress (combat-only potions are usable).</param>
    /// <param name="withDescription">Resolve the rules text (teammates' potions only need name and target).</param>
    public static PotionInfo? Potion(PotionModel? potion, int slot, bool inCombat, bool withDescription = true)
    {
        if (potion == null) return null;
        var info = new PotionInfo { Slot = slot, Id = SafeId(potion), Name = Loc(SafeGet(() => potion.Title)) };
        if (info.Name.Length == 0) info.Name = info.Id;
        if (withDescription) info.Description = Loc(SafeGet(() => potion.DynamicDescription));
        if (withDescription && info.Description.Length == 0)
        {
            try { info.Description = Clean(potion.HoverTip.Description); }
            catch (Exception) { }
        }
        Try(() => info.Target = potion.TargetType.ToString());
        Try(() => info.Rarity = potion.Rarity.ToString());
        info.UsableNow = PotionUsable(potion, inCombat);
        return info;
    }

    /// <summary>
    /// The game's manual-use rules: the local player's own potion, not queued, owner alive and allowed to use potions,
    /// the potion's own usability check, AnyTime anywhere, CombatOnly only in a fight, Automatic never by hand.
    /// Does not check whose turn it is (the readiness gate does).
    /// </summary>
    public static bool PotionUsable(PotionModel potion, bool inCombat)
    {
        try
        {
            if (potion == null || potion.IsQueued || potion.HasBeenRemovedFromState) return false;
            var owner = potion.Owner;
            if (!IsLocalOwner(potion, owner)) return false;
            if (owner != null)
            {
                if (owner.Creature != null && owner.Creature.IsDead) return false;
#if STS2_V0107
                if (!owner.CanRemovePotions) return false;
#else
                if (!owner.CanUseOrRemovePotions) return false;
#endif
            }
            if (!potion.PassesCustomUsabilityCheck) return false;
            return potion.Usage switch
            {
                PotionUsage.AnyTime => true,
                PotionUsage.CombatOnly => inCombat && CombatManager.Instance is { IsInProgress: true },
                _ => false,
            };
        }
        catch (Exception) { return false; }
    }

    private static bool IsLocalOwner(PotionModel potion, MegaCrit.Sts2.Core.Entities.Players.Player? owner)
    {
        try { if (LocalContext.IsMine(potion)) return true; } catch (Exception) { }
        try { if (owner != null && LocalContext.IsMe(owner)) return true; } catch (Exception) { }
        // Single player: the only player is the local one, whatever LocalContext says.
        try { return owner?.RunState?.Players.Count == 1; } catch (Exception) { return false; }
    }

    // Relic and power tooltips are formatted on every snapshot otherwise; they are cached per instance and refreshed
    // when the shown number changes, or after TextLife at the latest (texts that depend on other state).
    private static readonly TimeSpan TextLife = TimeSpan.FromSeconds(2);

    private sealed class CachedText
    {
        public string Key = "";
        public long At;
        public string Text = "";
    }

    private static readonly ConditionalWeakTable<RelicModel, CachedText> RelicTexts = new();
    private static readonly ConditionalWeakTable<PowerModel, CachedText> PowerTexts = new();

    private static string CachedOr<TModel>(ConditionalWeakTable<TModel, CachedText> table, TModel model, string key, Func<string> compute)
        where TModel : class
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (table.TryGetValue(model, out var c) && c.Key == key && System.Diagnostics.Stopwatch.GetElapsedTime(c.At, now) < TextLife)
            return c.Text;
        var text = compute();
        table.AddOrUpdate(model, new CachedText { Key = key, At = now, Text = text });
        return text;
    }

    /// <param name="relic">The relic.</param>
    /// <param name="withDescription">Resolve the rules text (teammates' relics only need names).</param>
    public static RelicInfo Relic(RelicModel relic, bool withDescription = true)
    {
        var info = new RelicInfo();
        if (relic == null) return info;
        info.Id = SafeId(relic);
        info.Name = Loc(SafeGet(() => relic.Title));
        if (info.Name.Length == 0) info.Name = info.Id;
        try { info.Counter = relic.ShowCounter ? relic.DisplayAmount : null; }
        catch (Exception) { }
        if (withDescription)
            info.Description = CachedOr(RelicTexts, relic, info.Counter + "|" + GameLanguage(), () => RelicDescription(relic));
        return info;
    }

    private static string RelicDescription(RelicModel relic)
    {
        var text = Loc(SafeGet(() => relic.DynamicDescription));
        if (text.Length == 0)
        {
            try { text = Clean(relic.HoverTip.Description); }
            catch (Exception) { }
        }
        return text;
    }

    /// <param name="power">The power.</param>
    /// <param name="withDescription">Resolve the tooltip text (cached per power instance and amount).</param>
    /// <param name="noteGlossary">Note the keyword tips the power refers to in the snapshot glossary.</param>
    public static PowerInfo Power(PowerModel power, bool withDescription = true, bool noteGlossary = true)
    {
        var info = new PowerInfo();
        if (power == null) return info;
        info.Id = SafeId(power);
        info.Name = Loc(SafeGet(() => power.Title));
        if (info.Name.Length == 0) info.Name = info.Id;
        Try(() => info.Amount = power.Amount);
        // A negative buff (e.g. Strength -2) counts as a debuff.
        Try(() => info.IsDebuff = power.TypeForCurrentAmount == PowerType.Debuff);
        // A power aimed at a player (Sandpit: owned by The Insatiable, Target = the threatened player).
        Try(() =>
        {
            var targetPlayer = power.Target?.Player;
            if (targetPlayer != null) info.TargetNetId = targetPlayer.NetId;
        });
        if (!withDescription) return info;
        string name = info.Name;
        info.Description = CachedOr(PowerTexts, power, info.Amount + "|" + GameLanguage(), () => PowerDescription(power, name));
        if (noteGlossary && Glossary.Collecting)
        {
            string ownTip = "";
            try { ownTip = power.Id.ToString(); } catch (Exception) { }
            Glossary.Note("power:" + info.Id, () => power.HoverTips, ownTip, name);
        }
        return info;
    }

    /// <summary>
    /// The tooltip text the game shows for the power (amounts and owner/applier names filled in): its own hover tip
    /// (matched by id, by model or by title), else the smart / plain description.
    /// </summary>
    private static string PowerDescription(PowerModel power, string name)
    {
        try
        {
            string id = "", entry = "";
            try
            {
                id = power.Id.ToString();
                entry = power.Id.Entry;
            }
            catch (Exception) { }
            string? byTitle = null;
            foreach (var tip in power.HoverTips)
            {
                if (tip is not HoverTip ht || string.IsNullOrEmpty(ht.Description)) continue;
                if (ht.Id == id || (entry.Length > 0 && (ht.Id == entry || SafeId(ht.CanonicalModel) == entry))) return Clean(ht.Description);
                if (byTitle == null && name.Length > 0 && Clean(ht.Title) == name) byTitle = ht.Description;
            }
            if (byTitle != null) return Clean(byTitle);
        }
        catch (Exception) { }
        string text = "";
        try { if (power.HasSmartDescription) text = Loc(power.SmartDescription); }
        catch (Exception) { }
        if (text.Length == 0)
        {
            try { text = Clean(power.DumbHoverTip.Description); }
            catch (Exception) { }
        }
        if (text.Length == 0) text = Loc(SafeGet(() => power.Description));
        return text;
    }

    /// <summary>A channeled orb (Defect); passive/evoke values are already Focus-modified in its tooltip.</summary>
    public static OrbInfo Orb(OrbModel orb)
    {
        var info = new OrbInfo();
        if (orb == null) return info;
        info.Id = SafeId(orb);
        info.Name = Loc(SafeGet(() => orb.Title));
        if (info.Name.Length == 0) info.Name = info.Id;
        try
        {
            string id = orb.Id.ToString();
            HoverTip? first = null;
            foreach (var tip in orb.HoverTips)
            {
                if (tip is not HoverTip ht) continue;
                first ??= ht;
                if (ht.Id == id && !string.IsNullOrEmpty(ht.Description))
                {
                    info.Description = Clean(ht.Description);
                    break;
                }
            }
            if (info.Description.Length == 0 && first is { } f) info.Description = Clean(f.Description);
        }
        catch (Exception) { }
        if (info.Description.Length == 0)
        {
            try
            {
                int passive = (int)orb.PassiveVal, evoke = (int)orb.EvokeVal;
                info.Description = $"Passive {passive}, Evoke {evoke}";
            }
            catch (Exception) { }
        }
        return info;
    }

    // ------------------------------------------------------------------ creatures and intents

    /// <summary>A creature (enemy, pet, player creature) with hp/block/powers and, for monsters, intents.</summary>
    /// <param name="noteGlossary">Note its powers' keyword tips in the glossary (not for a teammate's companion).</param>
    public static CreatureInfo Creature(Creature creature, string key, bool noteGlossary = true)
    {
        var info = new CreatureInfo { Key = key ?? "" };
        if (creature == null) return info;
        // Same on every client (unlike the key): team messages name enemies by it.
        Try(() => info.CombatId = creature.CombatId);
        Try(() => info.Hp = creature.CurrentHp);
        Try(() => info.MaxHp = creature.MaxHp);
        Try(() => info.Block = creature.Block);
        Try(() => info.IsAlive = creature.IsAlive);
        var monster = SafeGet(() => creature.Monster);
        var player = SafeGet(() => creature.Player);
        info.Id = monster != null ? SafeId(monster) : player != null ? SafeId(SafeGet(() => player.Character)) : "";
        if (info.Id.Length == 0) Try(() => info.Id = creature.ModelId?.Entry ?? "");
        info.Name = CreatureName(creature);
        try
        {
            foreach (var p in creature.Powers)
            {
                try
                {
                    if (p == null || !p.IsVisible) continue;
                    info.Powers.Add(Power(p, noteGlossary: noteGlossary));
                }
                catch (Exception) { /* skip this power */ }
            }
        }
        catch (Exception) { }
        if (monster != null && info.IsAlive) info.Intents = Intents(creature, monster);
        return info;
    }

    /// <summary>Localized monster title, else the creature's display name.</summary>
    public static string CreatureName(Creature creature)
    {
        string name = "";
        try
        {
            var monster = creature.Monster;
            if (monster != null) name = Loc(monster.Title);
        }
        catch (Exception) { }
        if (name.Length == 0)
        {
            try { name = Clean(creature.Name); }
            catch (Exception) { }
        }
        if (name.Length == 0) name = SafeId(SafeGet(() => creature.Monster));
        return name;
    }

    private static List<IntentInfo> Intents(Creature creature, MonsterModel monster)
    {
        var list = new List<IntentInfo>();
        try
        {
            var move = monster.NextMove;
            if (move?.Intents == null) return list;
            var targets = IntentTargets(creature);
            foreach (var intent in move.Intents)
            {
                if (intent == null) continue;
                try { list.Add(Intent(intent, targets, creature)); }
                catch (Exception) { /* skip this intent */ }
            }
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>
    /// Damage is computed against the local player (their Vulnerable / Intangible apply), which is what matters for
    /// this client's decisions; falls back to every player creature. Assumption: the intent UI passes the player
    /// creatures the same way (the exact set is private in NIntent).
    /// </summary>
    private static IReadOnlyList<Creature> IntentTargets(Creature owner)
    {
        try
        {
            var cs = owner.CombatState;
            if (cs == null) return Array.Empty<Creature>();
            var me = LocalContext.GetMe(cs);
            if (me?.Creature is { IsAlive: true } mine) return new[] { mine };
            return cs.PlayerCreatures ?? (IReadOnlyList<Creature>)Array.Empty<Creature>();
        }
        catch (Exception) { return Array.Empty<Creature>(); }
    }

    private static IntentInfo Intent(AbstractIntent intent, IReadOnlyList<Creature> targets, Creature owner)
    {
        var info = new IntentInfo();
        Try(() => info.Type = intent.IntentType.ToString());
        if (intent is AttackIntent attack)
        {
            try
            {
                info.Damage = Math.Max(0, attack.GetSingleDamage(targets, owner));
                info.Hits = Math.Max(1, attack.Repeats);
            }
            catch (Exception) { }
        }
        string desc = "";
        try
        {
            var tip = intent.GetHoverTip(targets, owner);
            desc = Clean(tip.Description);
            if (desc.Length == 0) desc = Clean(tip.Title);
        }
        catch (Exception) { }
        if (desc.Length == 0)
        {
            try { desc = Loc(intent.GetIntentLabel(targets, owner)); }
            catch (Exception) { }
        }
        if (intent is StatusIntent status && desc.Length == 0)
        {
            try { desc = status.CardCount + " status card(s)"; }
            catch (Exception) { }
        }
        info.Description = desc.Length > 0 ? desc : null;
        return info;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Model id entry ("STRIKE_IRONCLAD"), or "" if unavailable.</summary>
    public static string SafeId(AbstractModel? model)
    {
        try { return model?.Id?.Entry ?? ""; }
        catch (Exception) { return ""; }
    }

    private static T? SafeGet<T>(Func<T?> get) where T : class
    {
        try { return get(); }
        catch (Exception) { return null; }
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception) { /* best effort */ }
    }
}
