using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using SpirePilot.Core.Text;

namespace SpirePilot.Game.Combat;

/// <summary>One possible target of a card or potion: a creature (with its snapshot key) or "no target".</summary>
internal sealed class TargetOption
{
    /// <summary>The creature to pass to the game; null = untargeted (self / AoE / random / none).</summary>
    public Creature? Creature { get; init; }
    /// <summary>Snapshot key ("e0", "p1", "o0"); null when untargeted.</summary>
    public string? Key { get; init; }
    /// <summary>Semantic target: "E:&lt;MONSTER_ID&gt;#&lt;ordinal&gt;", "P:&lt;netId&gt;" or "-".</summary>
    public string Semantic { get; init; } = "-";
    /// <summary>Label text ("Cultist [e0]", teammate name, "yourself"); null when untargeted.</summary>
    public string? Label { get; init; }
    public bool IsEnemy { get; init; }
    public bool IsTeammate { get; init; }
}

/// <summary>
/// The creatures of the current fight with their keys and names, built once per snapshot so readers, labels and
/// target resolution agree. Main thread only; never throws (unreadable parts are left out).
/// </summary>
internal sealed class CombatView
{
    public GameContext Ctx { get; }
    public CombatState Cs { get; }
    public Player Me { get; }
    public List<CombatKeys.EnemyKey> Enemies { get; } = new();
    public List<(Player Player, int Slot, string Name)> Players { get; } = new();
    public List<(Creature Pet, Player Owner, string Key)> Pets { get; } = new();

    private CombatView(GameContext ctx, CombatState cs, Player me)
    {
        Ctx = ctx;
        Cs = cs;
        Me = me;
        try { Enemies.AddRange(CombatKeys.Enemies(cs)); } catch (Exception) { }
        try
        {
            foreach (var (player, slot) in PlayerReader.OrderedPlayers(ctx))
            {
                string name = "";
                try { name = PlayerReader.DisplayName(ctx, player, slot); } catch (Exception) { }
                Players.Add((player, slot, name));
                try
                {
                    var pets = player.PlayerCombatState?.Pets;
                    if (pets == null) continue;
                    int n = 0;
                    foreach (var pet in pets)
                    {
                        if (pet == null) continue;
                        Pets.Add((pet, player, PlayerReader.CompanionKey(slot, n)));
                        n++;
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    /// <summary>The view of the running fight, or null when there is no fight / local player.</summary>
    public static CombatView? Create(GameContext ctx)
    {
        if (ctx.Combat == null || ctx.Me == null) return null;
        try { return new CombatView(ctx, ctx.Combat, ctx.Me); }
        catch (Exception) { return null; }
    }

    public bool IsMultiplayer => Players.Count > 1;

    /// <summary>Whether <paramref name="p"/> is the local player.</summary>
    public bool IsMe(Player? p)
    {
        if (p == null) return false;
        if (ReferenceEquals(p, Me)) return true;
        try { return p.NetId == Me.NetId; }
        catch (Exception) { return false; }
    }

    /// <summary>Alive, hittable enemies.</summary>
    public IEnumerable<CombatKeys.EnemyKey> TargetableEnemies() => Enemies.Where(e => Safe(() => e.Creature.IsAlive && e.Creature.IsHittable));

    /// <summary>Alive teammates (other players).</summary>
    public IEnumerable<(Player Player, int Slot, string Name)> Teammates() =>
        Players.Where(p => !IsMe(p.Player) && Safe(() => p.Player.Creature != null && p.Player.Creature.IsAlive));

    /// <summary>Alive player creatures, the local player first.</summary>
    public IEnumerable<(Player Player, int Slot, string Name)> LivingPlayers() =>
        Players.Where(p => Safe(() => p.Player.Creature != null && p.Player.Creature.IsAlive));

    public TargetOption Untargeted { get; } = new();

    public TargetOption ForEnemy(CombatKeys.EnemyKey e) => new()
    {
        Creature = e.Creature,
        Key = e.Key,
        Semantic = e.Semantic,
        Label = ModelText.CreatureName(e.Creature) + " [" + e.Key + "]",
        IsEnemy = true,
    };

    public TargetOption ForPlayer((Player Player, int Slot, string Name) p)
    {
        CombatStrings.Ensure();
        bool self = IsMe(p.Player);
        return new TargetOption
        {
            Creature = p.Player.Creature,
            Key = CombatKeys.PlayerKey(p.Slot),
            Semantic = CombatKeys.PlayerSemantic(p.Player),
            // Single player: aiming at yourself is the only option, the label needs no target.
            Label = self ? (IsMultiplayer ? L10n.T("lbl.self") : null) : p.Name,
            IsTeammate = !self,
        };
    }

    public TargetOption ForPet((Creature Pet, Player Owner, string Key) pet) => new()
    {
        Creature = pet.Pet,
        Key = pet.Key,
        // Companions: "O:<MONSTER_ID>@o<slot>[_n]" (slot-based keys are stable for the whole run).
        Semantic = "O:" + ModelText.SafeId(Safe2(() => pet.Pet.Monster)) + "@" + pet.Key,
        Label = ModelText.CreatureName(pet.Pet) + " [" + pet.Key + "]",
    };

    /// <summary>Maps any creature of the fight to a target option (enemy, player or pet); null if unknown.</summary>
    public TargetOption? Describe(Creature c)
    {
        foreach (var e in Enemies) if (ReferenceEquals(e.Creature, c)) return ForEnemy(e);
        foreach (var p in Players) if (ReferenceEquals(Safe2(() => p.Player.Creature), c)) return ForPlayer(p);
        foreach (var pet in Pets) if (ReferenceEquals(pet.Pet, c)) return ForPet(pet);
        return null;
    }

    // ------------------------------------------------------------------ card targets

    /// <summary>
    /// Every valid target of a playable card. Enemy- and ally-aimed cards list each candidate the game accepts
    /// (<see cref="CardModel.IsValidTarget"/>; a candidate is dropped when the check says no, kept when the check
    /// itself throws); self / AoE / random / untargeted cards get the null target, which is what the vanilla UI passes
    /// (STS2-Agent, live-tested). Vanilla AnyPlayer cards only accept null unless a library (BaseLib / RitsuLib)
    /// patches targeting, so those are probed both ways; the null target is only offered when the game accepts it.
    /// </summary>
    public List<TargetOption> CardTargets(CardModel card)
    {
        var list = new List<TargetOption>();
        TargetType type;
        try { type = card.TargetType; }
        catch (Exception) { return list; }

        switch (type)
        {
            case TargetType.AnyEnemy:
                foreach (var e in TargetableEnemies())
                    if (Keep(Check(card, e.Creature))) list.Add(ForEnemy(e));
                break;
            case TargetType.AnyAlly:
                foreach (var p in Teammates())
                    if (Keep(Check(card, p.Player.Creature))) list.Add(ForPlayer(p));
                break;
            case TargetType.AnyPlayer:
            {
                foreach (var p in LivingPlayers())
                    if (Check(card, p.Player.Creature) == true) list.Add(ForPlayer(p));
                if (list.Count == 0 && Check(card, null) == true) list.Add(Untargeted);
                break;
            }
            case TargetType.Osty:
            {
                foreach (var pet in MyOstyCandidates())
                    if (Safe(() => pet.Pet.IsAlive) && Keep(Check(card, pet.Pet))) list.Add(ForPet(pet));
                if (list.Count == 0 && Check(card, null) == true) list.Add(Untargeted);
                break;
            }
            case TargetType.None:
            case TargetType.Self:
            case TargetType.AllEnemies:
            case TargetType.RandomEnemy:
            case TargetType.AllAllies:
            case TargetType.TargetedNoCreature:
                list.Add(Untargeted);
                break;
            default:
            {
                // Custom target types added by libraries: let the game decide.
                if (Check(card, null) == true) list.Add(Untargeted);
                foreach (var c in AllCreatures())
                    if (Safe(() => c.IsAlive) && Check(card, c) == true && Describe(c) is { } opt) list.Add(opt);
                break;
            }
        }
        return list;
    }

    /// <summary>
    /// The local player's companions that an Osty card may aim at: the pets of their combat state, plus
    /// <see cref="Player.Osty"/> when the game tracks it outside that list.
    /// </summary>
    private List<(Creature Pet, Player Owner, string Key)> MyOstyCandidates()
    {
        var mine = Pets.Where(x => IsMe(x.Owner)).ToList();
        try
        {
            var osty = Me.Osty;
            if (osty != null && !mine.Any(x => ReferenceEquals(x.Pet, osty)))
            {
                int slot = Players.FirstOrDefault(p => IsMe(p.Player)).Slot;
                var entry = (osty, Me, PlayerReader.CompanionKey(slot, mine.Count));
                Pets.Add(entry);
                mine.Add(entry);
            }
        }
        catch (Exception) { /* no Osty on this character / version */ }
        return mine;
    }

    private IEnumerable<Creature> AllCreatures()
    {
        IReadOnlyList<Creature>? all = null;
        try { all = Cs.Creatures; } catch (Exception) { }
        return all?.Where(c => c != null) ?? Enumerable.Empty<Creature>();
    }

    /// <summary>The game's target check: true / false, or null when the check itself threw.</summary>
    private static bool? Check(CardModel card, Creature? target)
    {
        try { return card.IsValidTarget(target); }
        catch (Exception) { return null; }
    }

    /// <summary>A candidate stays unless the game's check rejected it (a check that threw is no rejection).</summary>
    private static bool Keep(bool? check) => check != false;

    // ------------------------------------------------------------------ potion targets

    /// <summary>
    /// Every valid target of a usable potion. Following STS2-Agent's live-tested table: AoE / random / no-creature
    /// potions take null (passing the owner makes the game drop the use), AnyPlayer potions are aimed at any living
    /// player in co-op (this is how a potion is "given" to a teammate) and at the owner in single player, self
    /// potions at the owner; potions the game lets you throw at an ally also list the living teammates. Candidates the
    /// game's check rejects are dropped (kept when the check throws).
    /// </summary>
    public List<TargetOption> PotionTargets(PotionModel potion)
    {
        var list = new List<TargetOption>();
        TargetType type;
        try { type = potion.TargetType; }
        catch (Exception) { return list; }
        var me = Players.FirstOrDefault(p => IsMe(p.Player));

        switch (type)
        {
            case TargetType.AnyEnemy:
                foreach (var e in TargetableEnemies())
                    if (Keep(Check(potion, e.Creature))) list.Add(ForEnemy(e));
                return list;
            case TargetType.AllEnemies:
            case TargetType.AllAllies:
            case TargetType.RandomEnemy:
            case TargetType.TargetedNoCreature:
                list.Add(Untargeted);
                return list;
            case TargetType.AnyPlayer:
            {
                var living = LivingPlayers().ToList();
                if (living.Count > 1)
                {
                    foreach (var p in living)
                        if (Keep(Check(potion, p.Player.Creature))) list.Add(ForPlayer(p));
                }
                else if (me.Player != null && Keep(Check(potion, Me.Creature))) list.Add(ForPlayer(me));
                return list;
            }
            case TargetType.AnyAlly:
                foreach (var p in Teammates())
                    if (Keep(Check(potion, p.Player.Creature))) list.Add(ForPlayer(p));
                return list;
            default:
            {
                // Self / None: the owner (STS2-Agent's default arm); null only if the game rejects the owner but takes null.
                var owner = me.Player != null ? Check(potion, Me.Creature) : false;
                if (owner != false && me.Player != null) list.Add(ForPlayer(me));
                else if (Check(potion, null) == true) list.Add(Untargeted);
                bool throwable = false;
                try { throwable = potion.CanThrowAtAlly(); } catch (Exception) { }
                if (throwable)
                    foreach (var p in Teammates())
                        if (Check(potion, p.Player.Creature) == true) list.Add(ForPlayer(p));
                return list;
            }
        }
    }

    private static bool? Check(PotionModel potion, Creature? target)
    {
        try { return potion.IsValidTarget(target); }
        catch (Exception) { return null; }
    }

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch (Exception) { return false; }
    }

    private static T? Safe2<T>(Func<T?> f) where T : class
    {
        try { return f(); }
        catch (Exception) { return null; }
    }
}
