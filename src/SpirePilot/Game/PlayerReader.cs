using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Game.Combat;

namespace SpirePilot.Game;

/// <summary>Fills run-level info and every player's public state (local player and teammates).</summary>
internal static class PlayerReader
{
    /// <summary>Fills <see cref="GameSnapshot.Run"/> (act, floor, ascension, seed, boss and what is known about it).</summary>
    public static void FillRun(GameContext ctx, GameSnapshot snap)
    {
        var run = ctx.Run;
        if (run == null) return;
        var info = new RunInfo();
        Part(() => info.Act = run.CurrentActIndex + 1);
        Part(() => info.Floor = run.TotalFloor);
        Part(() => info.AscensionLevel = run.AscensionLevel);
        Part(() => info.Seed = run.Rng?.StringSeed);
        Part(() => info.IsDaily = run.GameMode == GameMode.Daily || ctx.RunManager?.DailyTime != null);
        Part(() =>
        {
            var act = run.Act;
            if (act == null) return;
            var name = ModelText.Loc(act.Title);
            info.ActName = name.Length > 0 ? name : null;
        });
        Part(() =>
        {
            // BossEncounter throws before the act is set up (STS2MCP note), hence the per-part guard.
            var act = run.Act;
            if (act == null) return;
            var bosses = new List<EncounterModel>();
            if (act.BossEncounter is { } first) bosses.Add(first);
            try
            {
                if (act.HasSecondBoss && act.SecondBossEncounter is { } second) bosses.Add(second);
            }
            catch (Exception) { }
            var names = bosses.Select(b => ModelText.Loc(b.Title)).Where(n => n.Length > 0).ToList();
            info.BossName = names.Count > 0 ? string.Join(" + ", names) : null;
            info.BossDescription = BossNotes(act, bosses);
        });
        // RunManager.RunTime drives the top-bar timer; assumed to be seconds (RitsuLib reports the saved value as seconds).
        Part(() => info.RunTimeSeconds = ctx.RunManager?.RunTime);
        snap.Run = info;
    }

    private const int MaxBossMonsters = 3;
    private static string _bossKey = "";
    private static string? _bossNotes;
    private static string _bossFailedKey = "";

    /// <summary>
    /// What is known about the act's boss fight before it starts: each monster with its HP and the names of its moves
    /// (the monster's localization entries; intents, numbers and powers only exist once the fight runs). Cached per
    /// act, bosses and game language once something was read; null (and retried on the next capture) when nothing was.
    /// </summary>
    private static string? BossNotes(ActModel act, List<EncounterModel> bosses)
    {
        string key = RuntimeHelpers.GetHashCode(act) + "|" + string.Join(",", bosses.Select(ModelText.SafeId)) + "|" + ModelText.GameLanguage();
        if (key == _bossKey) return _bossNotes;
        var fights = new List<string>();
        var failures = new List<string>();
        foreach (var boss in bosses)
        {
            var monsters = new List<string>();
            try
            {
                foreach (var monster in boss.AllPossibleMonsters.Where(m => m != null).DistinctBy(ModelText.SafeId).Take(MaxBossMonsters))
                    monsters.Add(MonsterNote(monster));
            }
            catch (Exception ex) { failures.Add($"{ModelText.SafeId(boss)}: {ex.GetType().Name}"); }
            if (monsters.Count > 0) fights.Add(string.Join("; ", monsters));
        }
        if (fights.Count == 0)
        {
            // Right after an act starts the encounter may not be readable yet: only a failure that repeats is logged.
            if (bosses.Count > 0 && key == _bossFailedKey)
                ThrottledLog.Warn("boss-notes", "boss notes: no boss monsters readable for " + string.Join(", ", bosses.Select(ModelText.SafeId)) +
                                                (failures.Count > 0 ? " (" + string.Join("; ", failures) + ")" : ""));
            _bossFailedKey = key;
            return null;
        }
        _bossKey = key;
        _bossNotes = string.Join(" / ", fights);
        return _bossNotes;
    }

    private static string MonsterNote(MonsterModel monster)
    {
        string note = ModelText.Loc(monster.Title);
        if (note.Length == 0) note = ModelText.SafeId(monster);
        try
        {
            int min = monster.MinInitialHp, max = monster.MaxInitialHp;
            note += " (HP " + (min == max ? min.ToString() : min + "-" + max) + ")";
        }
        catch (Exception) { }
        var moves = MoveNames(monster);
        return moves.Count > 0 ? note + ": moves " + string.Join(", ", moves) : note;
    }

    /// <summary>Move names from the monster's localization table: "&lt;monster&gt;.moves.&lt;MOVE&gt;.title" next to its "&lt;monster&gt;.name".</summary>
    private static List<string> MoveNames(MonsterModel monster)
    {
        var names = new List<string>();
        try
        {
            var title = monster.Title;
            string titleKey = title?.LocEntryKey ?? "";
            if (!titleKey.EndsWith(".name", StringComparison.Ordinal)) return names;
            var table = LocManager.Instance?.GetTable(title!.LocTable);
            if (table == null) return names;
            foreach (var move in table.GetLocStringsWithPrefix(titleKey[..^".name".Length] + ".moves."))
            {
                if (move?.LocEntryKey is not { } key || !key.EndsWith(".title", StringComparison.Ordinal)) continue;
                var name = ModelText.Loc(move);
                if (name.Length > 0 && !names.Contains(name)) names.Add(name);
            }
        }
        catch (Exception) { /* table not loaded */ }
        return names;
    }

    /// <summary>
    /// Fills <see cref="GameSnapshot.Players"/>: local player first, then teammates in slot order. Includes HP, block,
    /// gold, energy/stars/orbs/companions (in combat), powers, potions (with slots), relics, deck (local player full,
    /// teammates too), hand count and "ended turn" (co-op combat).
    /// </summary>
    public static void FillPlayers(GameContext ctx, GameSnapshot snap)
    {
        CombatStrings.Ensure();
        var players = OrderedPlayers(ctx);
        bool combatLive = ctx.Combat != null && CombatManager.Instance is { IsInProgress: true };
        foreach (var (player, slot) in players)
        {
            try { snap.Players.Add(ReadPlayer(ctx, player, slot, combatLive)); }
            catch (Exception ex) { snap.Notes.Add($"(player {slot} unreadable: {ex.GetType().Name})"); }
        }
    }

    /// <summary>All players with their slot index, local player first, then slot order.</summary>
    public static List<(Player player, int slot)> OrderedPlayers(GameContext ctx)
    {
        var result = new List<(Player, int)>();
        IReadOnlyList<Player>? list = null;
        try { list = ctx.Combat?.Players; } catch (Exception) { }
        try { if (list == null || list.Count == 0) list = ctx.Run?.Players; } catch (Exception) { }
        if (list == null) return result;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (p == null) continue;
            result.Add((p, SlotOf(ctx, p, i)));
        }
        return result
            .OrderBy(t => IsLocal(ctx, t.Item1) ? 0 : 1)
            .ThenBy(t => t.Item2)
            .ToList();
    }

    /// <summary>The player's slot index in the run (stable for the whole run); <paramref name="fallback"/> if unknown.</summary>
    public static int SlotOf(GameContext ctx, Player player, int fallback)
    {
        try
        {
            if (ctx.Run != null) return ctx.Run.GetPlayerSlotIndex(player);
            var rs = player.RunState;
            if (rs != null) return rs.GetPlayerSlotIndex(player);
        }
        catch (Exception) { }
        return fallback;
    }

    public static bool IsLocal(GameContext ctx, Player player)
    {
        try
        {
            if (ctx.Me != null) return ReferenceEquals(player, ctx.Me) || player.NetId == ctx.Me.NetId;
            return LocalContext.IsMe(player);
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Display name: the platform persona name in multiplayer (Steam name; placeholder names on LAN), otherwise the
    /// character name; "Player N" as a last resort. Readers use this for "→ teammate" labels.
    /// </summary>
    public static string DisplayName(GameContext ctx, Player player, int slot)
    {
        if (ctx.IsMultiplayer)
        {
            var name = PersonaName(ctx, player);
            if (name.Length > 0) return name;
        }
        var character = "";
        try { character = ModelText.Loc(player.Character?.Title); } catch (Exception) { }
        return character.Length > 0 ? character : L10n.T("player.n", slot + 1);
    }

    private static readonly Dictionary<ulong, (string name, DateTime at)> Personas = new();

    /// <summary>Platform persona name (cached for 30 s per player; "" when unavailable).</summary>
    private static string PersonaName(GameContext ctx, Player player)
    {
        ulong id;
        try { id = player.NetId; }
        catch (Exception) { return ""; }
        var now = DateTime.UtcNow;
        if (Personas.TryGetValue(id, out var cached) && now - cached.at < TimeSpan.FromSeconds(30)) return cached.name;
        string name = "";
        try
        {
            var ns = ctx.RunManager?.NetService;
            if (ns != null) name = (PlatformUtil.GetPlayerName(ns.Platform, id) ?? "").Trim();
        }
        catch (Exception) { /* platform name unavailable */ }
        Personas[id] = (name, now);
        return name;
    }

    /// <summary>
    /// One player's public state. Teammates get names, numbers, potions (name and target) and relic names: those rules
    /// texts are not used by the prompt and would be formatted on every snapshot. Their powers keep the description (the
    /// prompt's power notes explain every power in the fight; the text is cached per power and amount) but add nothing
    /// to the glossary.
    /// </summary>
    private static PlayerInfo ReadPlayer(GameContext ctx, Player p, int slot, bool combatLive)
    {
        var info = new PlayerInfo { IsLocal = IsLocal(ctx, p) };
        bool local = info.IsLocal;
        Part(() => info.NetId = p.NetId);
        Part(() => info.Name = DisplayName(ctx, p, slot));
        Part(() =>
        {
            var ch = p.Character;
            info.CharacterId = ModelText.SafeId(ch);
            info.CharacterName = ModelText.Loc(ch?.Title);
        });
        Part(() =>
        {
            var c = p.Creature;
            if (c == null) return;
            info.IsAlive = c.IsAlive;
            info.Hp = c.CurrentHp;
            info.MaxHp = c.MaxHp;
            info.Block = c.Block;
        });
        Part(() => info.Gold = p.Gold);

        Part(() =>
        {
            var powers = p.Creature?.Powers;
            if (powers == null) return;
            foreach (var pw in powers)
            {
                try { if (pw != null && pw.IsVisible) info.Powers.Add(ModelText.Power(pw, noteGlossary: local)); }
                catch (Exception) { }
            }
        });

        Part(() =>
        {
            var slots = p.PotionSlots;
            info.PotionSlots = Math.Max(p.MaxPotionCount, slots?.Count ?? 0);
            if (slots == null) return;
            for (int i = 0; i < slots.Count; i++)
            {
                try
                {
                    var potion = ModelText.Potion(slots[i], i, combatLive, withDescription: local);
                    if (potion != null) info.Potions.Add(potion);
                    if (local) Glossary.NotePotion(slots[i]);
                }
                catch (Exception) { }
            }
        });

        Part(() =>
        {
            var relics = p.Relics;
            if (relics == null) return;
            foreach (var r in relics)
            {
                if (r == null) continue;
                try { info.Relics.Add(ModelText.Relic(r, withDescription: local)); }
                catch (Exception) { }
                if (local) Glossary.NoteRelic(r);
            }
        });

        Part(() =>
        {
            var cards = p.Deck?.Cards;
            if (cards == null) return;
            info.DeckCount = cards.Count;
            foreach (var card in cards)
            {
                try { if (card != null) info.Deck.Add(DeckCard(card, combatLive)); }
                catch (Exception) { }
            }
        });

        // PlayerCombatState lingers after a fight and its Energy/MaxEnergy getters run hooks that fail without a
        // live combat (STS2MCP lesson): only read it while the fight is in progress.
        if (combatLive) Part(() => FillCombat(ctx, p, slot, info, local));
        return info;
    }

    private static void FillCombat(GameContext ctx, Player p, int slot, PlayerInfo info, bool local)
    {
        var pcs = p.PlayerCombatState;
        if (pcs == null) return;
        Part(() => info.Energy = pcs.Energy);
        Part(() => info.MaxEnergy = pcs.MaxEnergy);
        Part(() =>
        {
            int stars = pcs.Stars;
            bool show = stars > 0;
            try { show |= p.Character?.ShouldAlwaysShowStarCounter == true; } catch (Exception) { }
            info.Stars = show ? stars : null;
        });
        Part(() =>
        {
            var q = pcs.OrbQueue;
            if (q == null) return;
            info.OrbSlots = q.Capacity;
            foreach (var orb in q.Orbs)
            {
                try { if (orb != null) info.Orbs.Add(ModelText.Orb(orb)); }
                catch (Exception) { }
            }
        });
        Part(() => info.HandCount = pcs.Hand?.Cards.Count);
        Part(() => info.EndedTurn = CombatManager.Instance.IsPlayerReadyToEndTurn(p));
        Part(() =>
        {
            var pets = pcs.Pets;
            if (pets == null) return;
            int n = 0;
            foreach (var pet in pets)
            {
                if (pet == null) continue;
                try { info.Companions.Add(ModelText.Creature(pet, CompanionKey(slot, n), noteGlossary: local)); }
                catch (Exception) { }
                n++;
            }
        });
    }

    /// <summary>Key of a player's n-th companion: "o&lt;slot&gt;" for the first (Osty of slot 0 = "o0"), then "o0_1", ...</summary>
    public static string CompanionKey(int slot, int n) => n == 0 ? "o" + slot : $"o{slot}_{n}";

    // Master-deck cards rarely change; their text is cached per card instance and refreshed when the card changes
    // (upgrade, enchantment, cost or keyword changes are covered by the signature) or the game language changes. Text
    // read during a fight may carry the fight's modifiers (Strength and the like in the dynamic values): a cached
    // out-of-combat text is kept through the fight, and a text first read in combat is read again once the fight is over.
    private static readonly ConditionalWeakTable<CardModel, CachedCard> DeckCache = new();

    private sealed class CachedCard
    {
        public string Signature = "";
        public bool ReadInCombat;
        public CardInfo Info = new();
    }

    private static CardInfo DeckCard(CardModel card, bool combatLive)
    {
        string sig = DeckSignature(card);
        if (DeckCache.TryGetValue(card, out var cached) && cached.Signature == sig && (combatLive || !cached.ReadInCombat))
            return Copy(cached.Info);
        var info = ModelText.Card(card, "", inCombat: false);
        DeckCache.AddOrUpdate(card, new CachedCard { Signature = sig, ReadInCombat = combatLive, Info = Copy(info) });
        return info;
    }

    private static string DeckSignature(CardModel card)
    {
        try
        {
            var ench = card.Enchantment;
            return string.Join("|",
                ModelText.SafeId(card),
                card.CurrentUpgradeLevel,
                card.EnergyCost?.Canonical ?? 0,
                card.EnergyCost?.HasLocalModifiers == true,
                card.CurrentStarCost,
                string.Join(",", card.Keywords),
                ench == null ? "" : ModelText.SafeId(ench) + ench.Amount,
                ModelText.SafeId(card.Affliction),
                // Permanent growth (e.g. a dagger that gains damage) changes the base values.
                card.DynamicVars == null ? "" : string.Join(",", card.DynamicVars.Values.Select(v => v == null ? "" : v.Name + "=" + v.BaseValue)),
                L10n.Language,
                ModelText.GameLanguage());
        }
        catch (Exception) { return Guid.NewGuid().ToString(); }
    }

    private static CardInfo Copy(CardInfo c) => new()
    {
        Key = c.Key,
        Id = c.Id,
        Name = c.Name,
        Upgraded = c.Upgraded,
        Cost = c.Cost,
        StarCost = c.StarCost,
        Type = c.Type,
        Rarity = c.Rarity,
        Target = c.Target,
        Description = c.Description,
        Keywords = new List<string>(c.Keywords),
        Playable = c.Playable,
        GainsBlock = c.GainsBlock,
        UnplayableReason = c.UnplayableReason,
    };

    private static void Part(Action read)
    {
        try { read(); }
        catch (Exception) { /* one unreadable field must not lose the player */ }
    }
}
