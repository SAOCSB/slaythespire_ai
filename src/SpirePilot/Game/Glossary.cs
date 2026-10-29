using MegaCrit.Sts2.Core.HoverTips;

namespace SpirePilot.Game;

/// <summary>
/// Collects keyword explanations (the game's hover tips: "Vulnerable", "Exhaust", ...) for the snapshot being built,
/// so the model knows what the terms on its cards, relics, potions and powers mean. Main thread only.
/// </summary>
internal static class Glossary
{
    private const int MaxEntries = 40;
    private static Dictionary<string, string>? _target;

    /// <summary>Starts collecting into <paramref name="target"/> (called by the snapshot builder).</summary>
    public static void Begin(Dictionary<string, string> target) => _target = target;

    public static void End() => _target = null;

    /// <summary>Adds the titled hover tips of a model. Never throws.</summary>
    public static void Note(IEnumerable<IHoverTip>? tips)
    {
        var target = _target;
        if (target == null || tips == null || target.Count >= MaxEntries) return;
        try
        {
            foreach (var tip in tips)
            {
                if (tip is not HoverTip ht) continue;
                var title = ModelText.Clean(ht.Title);
                var text = ModelText.Clean(ht.Description);
                if (title.Length == 0 || text.Length == 0 || title.Length > 40 || target.ContainsKey(title)) continue;
                target[title] = text.Length > 240 ? text[..240] + "…" : text;
                if (target.Count >= MaxEntries) return;
            }
        }
        catch (Exception) { /* tips are best effort */ }
    }
}
