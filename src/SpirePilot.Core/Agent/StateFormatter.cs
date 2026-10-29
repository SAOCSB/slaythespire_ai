using System.Text;
using SpirePilot.Core.Model;

namespace SpirePilot.Core.Agent;

/// <summary>Renders a <see cref="GameSnapshot"/> as compact text for the LLM (much cheaper than raw JSON).</summary>
public sealed class StateFormatter
{
    public bool VerboseCards { get; init; } = true;
    public bool IncludePiles { get; init; } = true;
    public int MaxGlossaryEntries { get; init; } = 24;

    public string Format(GameSnapshot s)
    {
        _local = s.LocalPlayer;
        _coop = s.IsMultiplayer;
        var sb = new StringBuilder();
        sb.Append("Screen: ").Append(s.Screen);
        if (s.Run is { } run)
        {
            sb.Append(" | Act ").Append(run.Act);
            if (!string.IsNullOrEmpty(run.ActName)) sb.Append(" (").Append(run.ActName).Append(')');
            sb.Append(" | Floor ").Append(run.Floor).Append(" | Ascension ").Append(run.AscensionLevel);
            if (!string.IsNullOrEmpty(run.BossName)) sb.Append(" | Boss: ").Append(run.BossName);
        }
        sb.AppendLine(s.IsMultiplayer ? " | Co-op" : "");

        if (s.LocalPlayer is { } me)
        {
            sb.Append("You: ");
            AppendPlayer(sb, me, detailed: true);
        }
        foreach (var mate in s.Teammates)
        {
            sb.Append("Teammate: ");
            AppendPlayer(sb, mate, detailed: false);
        }

        if (s.Combat is { } c)
        {
            AppendCombat(sb, c);
            AppendPowerNotes(sb, s, c);
        }
        if (s.Map is { } map) AppendMap(sb, map);
        if (s.Rewards is { } r) AppendRewards(sb, r);
        if (s.Shop is { } shop) AppendShop(sb, shop);
        if (s.Event is { } ev) AppendEvent(sb, ev);
        if (s.RestSite is { } rest)
        {
            sb.AppendLine("Rest site options:");
            foreach (var o in rest.Options)
                sb.Append("  - ").Append(o.Name).Append(o.Enabled ? "" : " [disabled]").Append(": ").AppendLine(Clean(o.Description));
        }
        if (s.Selection is { } sel) AppendSelection(sb, sel);
        if (s.CharacterSelect is { } cs)
        {
            sb.Append("Characters: ").AppendLine(string.Join(", ", cs.Characters.Select(ch => $"{ch.Name} [{ch.Id}]{(ch.Locked ? " (locked)" : "")}{(ch.Selected ? " (selected)" : "")}")));
            sb.Append("Ascension: ").Append(cs.Ascension).Append(" (max ").Append(cs.MaxAscension).AppendLine(")");
            if (cs.LobbyPicks.Count > 0)
                sb.Append("Lobby picks: ").AppendLine(string.Join(", ", cs.LobbyPicks.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        if (s.GameOver is { } go)
            sb.Append("Run over: ").Append(go.Victory ? "VICTORY" : "defeat").Append(go.FloorReached is { } f ? $" at floor {f}" : "").AppendLine();
        foreach (var note in s.Notes) sb.Append("Note: ").AppendLine(Clean(note));
        if (s.Glossary.Count > 0)
        {
            sb.Append("Glossary: ");
            sb.AppendLine(string.Join(" | ", s.Glossary.OrderBy(kv => kv.Key, StringComparer.Ordinal).Take(MaxGlossaryEntries)
                .Select(kv => kv.Key + ": " + Clean(kv.Value))));
        }
        return sb.ToString();
    }

    private void AppendPlayer(StringBuilder sb, PlayerInfo p, bool detailed)
    {
        sb.Append(p.Name);
        if (!string.IsNullOrEmpty(p.CharacterName)) sb.Append(" (").Append(p.CharacterName).Append(')');
        if (p.IsAiControlled) sb.Append(" [AI]");
        if (!p.IsLocal && !p.HasTeamLink) sb.Append(" [no SpirePilot: cannot read team messages]");
        if (!p.IsAlive) sb.Append(" [DEAD]");
        sb.Append(" HP ").Append(p.Hp).Append('/').Append(p.MaxHp);
        if (p.Block > 0) sb.Append(" Block ").Append(p.Block);
        if (p.MaxEnergy > 0) sb.Append(" Energy ").Append(p.Energy).Append('/').Append(p.MaxEnergy);
        if (p.Stars is { } stars) sb.Append(" Stars ").Append(stars);
        sb.Append(" Gold ").Append(p.Gold);
        if (p.HandCount is { } hc && !detailed) sb.Append(" Hand ").Append(hc);
        if (!detailed && p.EndedTurn) sb.Append(" (ended turn)");
        sb.AppendLine();

        if (p.Powers.Count > 0) sb.Append("  Powers: ").AppendLine(Powers(p.Powers));
        if (p.Orbs.Count > 0 || p.OrbSlots > 0)
            sb.Append("  Orbs [").Append(p.Orbs.Count).Append('/').Append(p.OrbSlots).Append("]: ")
              .AppendLine(string.Join("; ", p.Orbs.Select(o => VerboseCards && o.Description.Length > 0 ? $"{o.Name} ({Clean(o.Description)})" : o.Name)));
        foreach (var comp in p.Companions)
            sb.Append("  Companion ").Append(comp.Key).Append(' ').Append(comp.Name).Append(" HP ").Append(comp.Hp).Append('/').Append(comp.MaxHp)
              .Append(comp.Block > 0 ? $" Block {comp.Block}" : "").Append(comp.Powers.Count > 0 ? " | " + Powers(comp.Powers) : "").AppendLine();

        sb.Append("  Potions [").Append(p.Potions.Count).Append('/').Append(p.PotionSlots).Append("]: ");
        sb.AppendLine(p.Potions.Count == 0 ? "none" : string.Join("; ", p.Potions.Select(pt =>
            detailed && VerboseCards ? $"#{pt.Slot} {pt.Name} [{pt.Target}] {Clean(pt.Description)}"
            : !detailed && pt.Target == "AnyPlayer" ? $"#{pt.Slot} {pt.Name} (can be given to a player)" : $"#{pt.Slot} {pt.Name}")));

        if (detailed)
        {
            if (p.Relics.Count > 0)
                sb.Append("  Relics: ").AppendLine(string.Join("; ", p.Relics.Select(r =>
                    (VerboseCards ? $"{r.Name} ({Clean(r.Description)})" : r.Name) + (r.Counter is { } cnt && cnt >= 0 ? $" [{cnt}]" : ""))));
            if (p.Deck.Count > 0)
                sb.Append("  Deck (").Append(p.Deck.Count).Append("): ").AppendLine(Grouped(p.Deck));
            else if (p.DeckCount > 0)
                sb.Append("  Deck size: ").Append(p.DeckCount).AppendLine();
        }
        else if (p.Relics.Count > 0)
        {
            sb.Append("  Relics: ").AppendLine(string.Join(", ", p.Relics.Select(r => r.Name)));
        }
    }

    private PlayerInfo? _local;
    private bool _coop;

    /// <summary>
    /// What ending the turn now does to you, in StS2 order:
    /// (1) Doom on you is checked as your turn ends: you die if Doom >= HP (block is irrelevant);
    /// (2) Constrict hits as your turn ends (block absorbs it first);
    /// (3) enemy attacks hit your remaining block; Osty (Necrobinder) takes the unblocked attack damage up to his HP and
    ///     the rest reaches you; Magic Bomb lands after your turn (ignores block);
    /// (4) Poison ticks at the start of your next turn (ignores block), before you can play a card.
    /// Sandpit (owned by the enemy) kills the player it targets when it runs out.
    /// End-of-turn block (Frost orbs, Plating) and caps such as Intangible are not counted, so the line errs on the safe side.
    /// </summary>
    private void AppendThreat(StringBuilder sb, CombatInfo c, PlayerInfo? me)
    {
        me ??= _local;
        if (me == null || !c.IsPlayerTurn) return;
        int incoming = 0;
        foreach (var e in c.Enemies.Where(e => e.IsAlive))
            foreach (var i in e.Intents)
            {
                if (i.Damage is not { } d) continue;
                // In co-op an intent may name its target; only count the ones aimed at us (or unnamed).
                if (!string.IsNullOrEmpty(i.TargetName) && !string.Equals(i.TargetName, me.Name, StringComparison.OrdinalIgnoreCase)) continue;
                incoming += d * Math.Max(1, i.Hits ?? 1);
            }
        int Mine(string id) => me.Powers.Where(p => p.Id.Contains(id, StringComparison.OrdinalIgnoreCase)).Sum(p => p.Amount);
        int poison = Mine("POISON"), doom = Mine("DOOM"), constrict = Mine("CONSTRICT"), bomb = Mine("MAGIC_BOMB");
        int doomGrowth = Mine("NEUROSURGE"); // Neurosurge adds that much Doom at the start of each of your turns
        int block = Math.Max(0, me.Block);
        int constrictHit = Math.Max(0, constrict - block);
        int unblocked = Math.Max(0, incoming - Math.Max(0, block - constrict));
        // Osty carries DieForYouPower: unblocked attack damage aimed at his owner goes to him first (overflow assumed to reach you).
        int osty = me.Companions.Where(x => x.IsAlive && (x.Id.Contains("OSTY", StringComparison.OrdinalIgnoreCase) ||
                                                          x.Powers.Any(p => p.Id.Contains("DIE_FOR_YOU", StringComparison.OrdinalIgnoreCase))))
                                .Sum(x => Math.Max(0, x.Hp));
        int attacksToYou = Math.Max(0, unblocked - osty);
        int beforeNextTurn = constrictHit + attacksToYou + bomb + poison;
        // In co-op Sandpit only threatens the player it targets.
        int? sandpit = c.Enemies.Where(e => e.IsAlive).SelectMany(e => e.Powers)
            .Where(p => p.Id.Contains("SANDPIT", StringComparison.OrdinalIgnoreCase) &&
                        (p.TargetNetId == me.NetId || p.TargetNetId == null && !_coop))
            .Select(p => (int?)p.Amount).Min();

        sb.Append("Threat: incoming ").Append(incoming).Append(", unblocked ").Append(unblocked);
        if (osty > 0 && unblocked > 0) sb.Append(" (Osty takes up to ").Append(osty).Append(", you take ").Append(attacksToYou).Append(')');
        if (constrict > 0) sb.Append(", constrict ").Append(constrict).Append(" at your turn end");
        if (bomb > 0) sb.Append(", magic bomb ").Append(bomb);
        if (poison > 0) sb.Append(", poison ").Append(poison).Append(" at your next turn start");
        if (doom > 0) sb.Append(", doom ").Append(doom).Append(doomGrowth > 0 ? $" (+{doomGrowth}/turn)" : "");
        var handHurts = c.Hand.Where(h => EndOfTurnHandIds.Contains(h.Id)).Select(h => h.Name).Distinct().ToList();
        if (handHurts.Count > 0) sb.Append(", end-of-turn hand effects: ").Append(string.Join("/", handHurts));
        if (sandpit is { } sp) sb.Append(", sandpit ").Append(sp);

        if (doom > 0 && doom >= me.Hp)
            sb.Append(" — DOOM KILLS YOU WHEN THIS TURN ENDS (block does not help: heal above it or win now)");
        else if (beforeNextTurn >= me.Hp)
            sb.Append(" — ENDING THE TURN NOW WOULD KILL YOU");
        else if (sandpit is <= 1)
            sb.Append(" — SANDPIT KILLS YOU AT THE NEXT ENEMY TURN");
        else if (doom + doomGrowth > 0 && doom + doomGrowth >= me.Hp - beforeNextTurn)
            sb.Append(" — warning: after this damage, Doom kills you at the end of your next turn");
        sb.AppendLine();
    }

    /// <summary>Status/curse cards that hurt you when your turn ends while they are in hand (CardModel.HasTurnEndInHandEffect).</summary>
    private static readonly HashSet<string> EndOfTurnHandIds = new(StringComparer.OrdinalIgnoreCase) { "BURN", "DECAY", "TOXIC", "INFECTION", "REGRET" };

    private void AppendCombat(StringBuilder sb, CombatInfo c)
    {
        sb.Append("Combat: round ").Append(c.Round).Append(c.IsPlayerTurn ? " (your turn)" : " (enemy turn)");
        if (!string.IsNullOrEmpty(c.EncounterType)) sb.Append(" | ").Append(c.EncounterType);
        if (!string.IsNullOrEmpty(c.EncounterName)) sb.Append(" | ").Append(c.EncounterName);
        sb.AppendLine();
        sb.AppendLine("Enemies:");
        foreach (var e in c.Enemies.Where(e => e.IsAlive))
        {
            sb.Append("  ").Append(e.Key).Append(' ').Append(e.Name).Append(" HP ").Append(e.Hp).Append('/').Append(e.MaxHp);
            if (e.Block > 0) sb.Append(" Block ").Append(e.Block);
            if (e.Intents.Count > 0) sb.Append(" | Intent: ").Append(string.Join(" + ", e.Intents.Select(Intent)));
            if (e.Powers.Count > 0) sb.Append(" | ").Append(Powers(e.Powers));
            sb.AppendLine();
        }
        AppendThreat(sb, c, me: null);
        sb.Append("Hand (").Append(c.Hand.Count).AppendLine("):");
        foreach (var card in c.Hand) sb.Append("  ").AppendLine(Card(card, withKey: true));
        sb.Append("Piles: draw ").Append(c.DrawPileCount).Append(", discard ").Append(c.DiscardPileCount).Append(", exhaust ").Append(c.ExhaustPileCount).AppendLine();
        if (IncludePiles)
        {
            if (c.DrawPile.Count > 0) sb.Append("  Draw pile (unordered): ").AppendLine(Grouped(c.DrawPile));
            if (c.DiscardPile.Count > 0) sb.Append("  Discard pile: ").AppendLine(Grouped(c.DiscardPile));
            if (c.ExhaustPile.Count > 0) sb.Append("  Exhausted: ").AppendLine(Grouped(c.ExhaustPile));
        }
    }

    private static void AppendMap(StringBuilder sb, MapInfo map)
    {
        sb.Append("Map: at ").Append(map.Current ?? "start").Append(", rows ").Append(map.Rows).AppendLine();
        sb.Append("  Next options: ");
        sb.AppendLine(string.Join(", ", map.Reachable.Select(k => map.Find(k) is { } n ? $"{k}={n.Type}" : k)));
        if (map.TeammateVotes.Count > 0)
            sb.Append("  Teammate votes: ").AppendLine(string.Join(", ", map.TeammateVotes.Select(kv => $"{kv.Key}→{kv.Value}")));
        // Upcoming rows (compact): row -> node types, so the model sees what lies ahead.
        int fromRow = map.Current != null && map.Find(map.Current) is { } cur ? cur.Row + 1 : 0;
        foreach (var row in map.Nodes.Where(n => n.Row >= fromRow).GroupBy(n => n.Row).OrderBy(g => g.Key).Take(16))
        {
            sb.Append("  row ").Append(row.Key).Append(": ");
            sb.AppendLine(string.Join(" ", row.OrderBy(n => n.Col).Select(n =>
                $"{n.Key}:{Abbrev(n.Type)}" + (n.Children.Count > 0 ? "->" + string.Join("/", n.Children) : ""))));
        }
    }

    private static void AppendRewards(StringBuilder sb, RewardInfo r)
    {
        if (r.Items.Count > 0)
            sb.Append("Rewards: ").AppendLine(string.Join("; ", r.Items.Select(i => $"{i.Kind}: {Clean(i.Description)}")));
        if (r.CardChoices.Count > 0)
        {
            sb.AppendLine("Card reward choices:");
            foreach (var c in r.CardChoices) sb.Append("  ").AppendLine(Card(c, withKey: false));
        }
    }

    private static void AppendShop(StringBuilder sb, ShopInfo shop)
    {
        sb.AppendLine("Shop:");
        foreach (var i in shop.Items)
        {
            sb.Append("  ").Append(i.Kind).Append(' ').Append(i.Name).Append(" — ").Append(i.Price).Append('g');
            if (i.OnSale) sb.Append(" (sale)");
            if (!i.Affordable) sb.Append(" (can't afford)");
            if (i.Description.Length > 0) sb.Append(": ").Append(Clean(i.Description));
            sb.AppendLine();
        }
        if (shop.RemovalPrice is { } rp) sb.Append("  Card removal: ").Append(rp).Append('g').Append(shop.RemovalAvailable ? "" : " (unavailable)").AppendLine();
    }

    private static void AppendEvent(StringBuilder sb, EventInfo ev)
    {
        sb.Append("Event: ").AppendLine(ev.Title);
        if (ev.Body.Length > 0) sb.Append("  ").AppendLine(Clean(ev.Body));
        foreach (var o in ev.Options)
            sb.Append("  - ").Append(Clean(o.Text)).Append(o.Locked ? " [locked]" : "")
              .Append(string.IsNullOrEmpty(o.Description) ? "" : ": " + Clean(o.Description!)).AppendLine();
    }

    private static void AppendSelection(StringBuilder sb, SelectionInfo sel)
    {
        sb.Append("Selection: ").Append(sel.Purpose);
        if (sel.Prompt.Length > 0) sb.Append(" — ").Append(Clean(sel.Prompt));
        sb.Append(" (pick ").Append(sel.MinSelect == sel.MaxSelect ? sel.MinSelect.ToString() : $"{sel.MinSelect}-{sel.MaxSelect}").Append(')');
        if (sel.SelectedKeys.Count > 0) sb.Append(" selected: ").Append(string.Join(",", sel.SelectedKeys));
        sb.AppendLine();
        foreach (var c in sel.Candidates) sb.Append("  ").AppendLine(Card(c, withKey: true));
        foreach (var o in sel.OtherOptions) sb.Append("  - ").AppendLine(Clean(o));
    }

    private static string Card(CardInfo c, bool withKey)
    {
        var sb = new StringBuilder();
        if (withKey && c.Key.Length > 0) sb.Append(c.Key).Append(' ');
        sb.Append(c.Name);
        if (c.Upgraded && !c.Name.Contains('+')) sb.Append('+');
        sb.Append(" [").Append(c.Cost switch { null => "-", -1 => "X", var v => v.ToString() });
        if (c.StarCost is > 0) sb.Append("+").Append(c.StarCost).Append('★');
        sb.Append("] ").Append(c.Type);
        if (c.Target.Length > 0 && c.Target != "None") sb.Append(", ").Append(c.Target);
        if (c.Rarity.Length > 0) sb.Append(", ").Append(c.Rarity);
        if (c.Keywords.Count > 0) sb.Append(" {").Append(string.Join(",", c.Keywords)).Append('}');
        if (c.Playable == false) sb.Append(" (unplayable").Append(string.IsNullOrEmpty(c.UnplayableReason) ? "" : ": " + c.UnplayableReason).Append(')');
        if (c.Description.Length > 0) sb.Append(": ").Append(Clean(c.Description));
        return sb.ToString();
    }

    private static string Grouped(IEnumerable<CardInfo> cards) =>
        string.Join(", ", cards.GroupBy(c => c.Name + (c.Upgraded && !c.Name.Contains('+') ? "+" : ""))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Count() > 1 ? $"{g.Key} x{g.Count()}" : g.Key));

    private static string Powers(IEnumerable<PowerInfo> powers) =>
        string.Join(", ", powers.Select(p => $"{p.Name}{(p.Amount != 0 ? " " + p.Amount : "")}{(p.IsDebuff ? "(debuff)" : "")}"));

    private static string Intent(IntentInfo i)
    {
        var s = i.Type;
        if (i.Damage is { } d) s += $" {d}" + (i.Hits is > 1 ? $"x{i.Hits}" : "");
        if (!string.IsNullOrEmpty(i.TargetName)) s += $" → {i.TargetName}";
        // Non-attack moves are often new in StS2: say what they do.
        if (i.Damage == null && Clean(i.Description ?? "") is { Length: > 0 } what) s += " (" + Short(what, 110) + ")";
        return s;
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private const int MaxPowerNotes = 14;

    /// <summary>One line per distinct power in the fight (players and enemies): StS2 has many powers models don't know.</summary>
    private static void AppendPowerNotes(StringBuilder sb, GameSnapshot s, CombatInfo c)
    {
        var all = s.Players.SelectMany(p => p.Powers.Concat(p.Companions.SelectMany(x => x.Powers)))
            .Concat(c.Enemies.Where(e => e.IsAlive).SelectMany(e => e.Powers))
            .Where(p => !string.IsNullOrWhiteSpace(p.Description))
            .GroupBy(p => p.Id.Length > 0 ? p.Id : p.Name)
            .Select(g => g.First())
            .Take(MaxPowerNotes)
            .ToList();
        if (all.Count == 0) return;
        sb.Append("Power effects: ").AppendLine(string.Join(" | ", all.Select(p => p.Name + ": " + Short(Clean(p.Description), 140))));
    }

    private static string Abbrev(MapNodeType t) => t switch
    {
        MapNodeType.Monster => "M",
        MapNodeType.Elite => "E",
        MapNodeType.RestSite => "R",
        MapNodeType.Shop => "$",
        MapNodeType.Treasure => "T",
        MapNodeType.Event => "?",
        MapNodeType.Boss => "BOSS",
        MapNodeType.Ancient => "A",
        _ => "?",
    };

    /// <summary>Strips rich-text markup (BBCode / [tags]) and collapses whitespace.</summary>
    /// <summary>Godot's built-in BBCode tags plus the game's own text effects (MegaCrit.Sts2.Core.RichTextTags).</summary>
    private static readonly HashSet<string> MarkupTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "i", "u", "s", "code", "center", "left", "right", "fill", "indent", "url", "font", "font_size", "color",
        "outline_size", "outline_color", "table", "cell", "ul", "ol", "p", "br", "hint", "bgcolor", "fgcolor", "wave",
        "shake", "rainbow", "tornado", "fade", "pulse", "dropcap", "opentype_features", "lang", "char",
        "aqua", "blue", "gold", "green", "jitter", "orange", "pink", "purple", "red", "scramble", "sine",
        "thinkydots", "thinky_dots", "fadein", "fade_in", "flyin", "fly_in", "ancientbanner", "ancient_banner",
    };

    private static readonly System.Text.RegularExpressions.Regex Tag = new(
        @"\[img[^\]]*\][^\[]*\[/img\]|\[(/?)([A-Za-z_][A-Za-z0-9_]*)((?:[= ][^\]]*)?)\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Plain text from game text: removes markup tags and images, keeps literal brackets ("[Leave]"), turns
    /// [lb]/[rb] into brackets and collapses whitespace.
    /// </summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var plain = Tag.Replace(text, m =>
        {
            if (m.Value.StartsWith("[img", StringComparison.OrdinalIgnoreCase)) return "";
            var name = m.Groups[2].Value;
            if (name.Equals("lb", StringComparison.OrdinalIgnoreCase)) return "[";
            if (name.Equals("rb", StringComparison.OrdinalIgnoreCase)) return "]";
            // Known markup, or anything with attributes ("[color=#fff]"), is formatting; other words are text.
            return MarkupTags.Contains(name) || m.Groups[3].Value.StartsWith('=') ? "" : m.Value;
        });
        var sb = new StringBuilder(plain.Length);
        foreach (var ch in plain) sb.Append(char.IsWhiteSpace(ch) ? ' ' : ch);
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
    }
}
