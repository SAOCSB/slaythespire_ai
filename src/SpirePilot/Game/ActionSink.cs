using SpirePilot.Core.Model;

namespace SpirePilot.Game;

/// <summary>Executes one action on the Godot main thread and waits until the game has accepted / settled it.</summary>
internal delegate Task<ActionResult> ActionExecutor(CancellationToken cancellationToken);

/// <summary>
/// Collects the legal actions of one snapshot together with the code that performs them. Readers describe what
/// they see and register the matching executor in the same pass, so the menu shown to the AI and what can actually
/// be executed can never disagree. Ids are assigned in insertion order ("a0", "a1", ...).
/// </summary>
internal sealed class ActionSink
{
    private readonly List<(ActionOption option, ActionExecutor execute)> _items = new();

    public long Sequence { get; }

    public ActionSink(long sequence) => Sequence = sequence;

    /// <param name="kind">What kind of action this is.</param>
    /// <param name="label">Localized human-readable label, e.g. "Play Strike → Cultist (6 dmg)".</param>
    /// <param name="semanticKey">
    /// Identity that survives snapshot changes, built from model ids, never from list positions:
    /// "play:STRIKE_IRONCLAD+0@E:CULTIST#0", "potion:FIRE_POTION@E:CULTIST#0", "end_turn", "map:3,4",
    /// "reward:card:BASH", "shop:relic:ANCHOR", "rest:SMITH", "event:0:{text key}", "select:STRIKE_IRONCLAD+0", "confirm".
    /// Two actions with the same key must be interchangeable (e.g. two identical Strikes).
    /// </param>
    /// <param name="execute">Runs on the main thread; must re-check node validity before touching anything.</param>
    /// <param name="args">Parameters for logs / the HTTP API (index, target, slot, node, character ...).</param>
    /// <param name="unpredictable">The result cannot be foreseen (draw / random / choice / screen change).</param>
    public ActionOption Add(ActionKind kind, string label, string semanticKey, ActionExecutor execute,
        IDictionary<string, string>? args = null, bool unpredictable = false)
    {
        var option = new ActionOption
        {
            Id = "a" + _items.Count,
            Kind = kind,
            Label = label,
            SemanticKey = semanticKey,
            Unpredictable = unpredictable,
            SnapshotSequence = Sequence,
        };
        if (args != null) foreach (var (k, v) in args) option.Args[k] = v;
        _items.Add((option, execute));
        return option;
    }

    public IReadOnlyList<ActionOption> Options => _items.Select(i => i.option).ToList();

    public int Count => _items.Count;

    public ActionExecutor? Find(string id) => _items.FirstOrDefault(i => i.option.Id == id).execute;

    public ActionExecutor? FindBySemanticKey(string key) =>
        string.IsNullOrEmpty(key) ? null : _items.FirstOrDefault(i => i.option.SemanticKey == key).execute;

    /// <summary>Removes every action (used when the screen turns out not to be actionable).</summary>
    public void Clear() => _items.Clear();
}
