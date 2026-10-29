using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace SpirePilot.Game.Combat;

/// <summary>
/// Stable creature keys for one fight. Enemies get "e&lt;n&gt;" in the order they first appear (summons get the next
/// number, dead or removed enemies keep theirs), plus an ordinal among enemies with the same monster id for semantic
/// keys ("E:CULTIST#1"). Players are "p&lt;slot&gt;", companions "o&lt;slot&gt;". Main thread only.
/// </summary>
internal static class CombatKeys
{
    private sealed record Entry(int Index, string MonsterId, int Ordinal);

    private static WeakReference<ICombatState>? _combat;
    private static WeakReference<EncounterModel>? _encounter;
    private static readonly Dictionary<uint, Entry> ById = new();
    private static readonly Dictionary<Creature, Entry> ByRef = new(ReferenceEqualityComparer.Instance);
    private static readonly Dictionary<string, int> OrdinalCounters = new();
    private static int _next;

    /// <summary>One enemy with its stable key data.</summary>
    public readonly record struct EnemyKey(Creature Creature, int Index, string MonsterId, int Ordinal)
    {
        public string Key => "e" + Index;
        /// <summary>Semantic target, e.g. "E:CULTIST#0".</summary>
        public string Semantic => $"E:{MonsterId}#{Ordinal}";
    }

    /// <summary>The fight's enemies (dead ones included while the game lists them), ordered by key.</summary>
    public static List<EnemyKey> Enemies(ICombatState cs)
    {
        var result = new List<EnemyKey>();
        if (cs == null) return result;
        IReadOnlyList<Creature>? enemies = null;
        try { enemies = cs.Enemies; } catch (Exception) { }
        if (enemies == null) return result;
        Sync(cs);
        foreach (var e in enemies)
        {
            if (e == null) continue;
            try
            {
                var entry = Register(e);
                result.Add(new EnemyKey(e, entry.Index, entry.MonsterId, entry.Ordinal));
            }
            catch (Exception) { /* skip unreadable enemy */ }
        }
        result.Sort((a, b) => a.Index.CompareTo(b.Index));
        return result;
    }

    /// <summary>Key data of one enemy creature, or null if it is not an enemy of <paramref name="cs"/>.</summary>
    public static EnemyKey? Enemy(ICombatState cs, Creature creature)
    {
        foreach (var e in Enemies(cs))
            if (ReferenceEquals(e.Creature, creature)) return e;
        return null;
    }

    /// <summary>
    /// Keeps the keys while the fight is the same one: the same combat state object and the same encounter. A new
    /// combat state or encounter starts the numbering over; a wave of new enemies in the same fight never does (they
    /// get the next numbers).
    /// </summary>
    private static void Sync(ICombatState cs)
    {
        EncounterModel? encounter = null;
        try { encounter = cs.Encounter; } catch (Exception) { }
        bool sameCombat = _combat != null && _combat.TryGetTarget(out var current) && ReferenceEquals(current, cs);
        if (encounter != null)
        {
            EncounterModel? last = null;
            bool known = _encounter != null && _encounter.TryGetTarget(out last);
            bool changed = !known || !ReferenceEquals(last, encounter);
            // An encounter that could not be read before is no evidence of a new fight.
            if (known && changed) sameCombat = false;
            if (changed) _encounter = new WeakReference<EncounterModel>(encounter);
        }
        if (sameCombat) return;
        _combat = new WeakReference<ICombatState>(cs);
        ById.Clear();
        ByRef.Clear();
        OrdinalCounters.Clear();
        _next = 0;
    }

    private static Entry Register(Creature e)
    {
        uint? id = null;
        try { id = e.CombatId; } catch (Exception) { }
        if (id is { } cid && ById.TryGetValue(cid, out var known)) return known;
        if (id == null && ByRef.TryGetValue(e, out var knownRef)) return knownRef;

        string monsterId = ModelText.SafeId(e.Monster);
        if (monsterId.Length == 0)
        {
            try { monsterId = e.ModelId?.Entry ?? "?"; } catch (Exception) { monsterId = "?"; }
        }
        OrdinalCounters.TryGetValue(monsterId, out int ordinal);
        OrdinalCounters[monsterId] = ordinal + 1;
        var entry = new Entry(_next++, monsterId, ordinal);
        if (id is { } newId) ById[newId] = entry;
        else ByRef[e] = entry;
        return entry;
    }

    /// <summary>"p&lt;slot&gt;" for a player.</summary>
    public static string PlayerKey(int slot) => "p" + slot;

    /// <summary>Semantic target of a player: "P:&lt;netId&gt;".</summary>
    public static string PlayerSemantic(Player player)
    {
        try { return "P:" + player.NetId; }
        catch (Exception) { return "P:?"; }
    }
}
