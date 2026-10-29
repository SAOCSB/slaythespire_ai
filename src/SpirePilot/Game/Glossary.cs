using System.Diagnostics;
using MegaCrit.Sts2.Core.HoverTips;

namespace SpirePilot.Game;

/// <summary>
/// Collects keyword explanations (the game's hover tips: "Vulnerable", "Exhaust", ...) for the snapshot being built,
/// so the model knows what the terms on its cards, relics, potions and powers mean. Main thread only.
/// </summary>
internal static class Glossary
{
    private const int MaxEntries = 40;
    private const int MaxCached = 512;
    /// <summary>Cached tips are re-read after this long (language switches, the odd tip with a live number).</summary>
    private static readonly TimeSpan CacheLife = TimeSpan.FromSeconds(30);

    private static Dictionary<string, string>? _target;
    private static readonly Dictionary<string, (long At, List<(string Title, string Text)> Tips)> Cache = new();

    /// <summary>Starts collecting into <paramref name="target"/> (called by the snapshot builder).</summary>
    public static void Begin(Dictionary<string, string> target) => _target = target;

    public static void End() => _target = null;

    /// <summary>True while a snapshot is being built and the glossary still has room.</summary>
    public static bool Collecting => _target is { Count: < MaxEntries };

    /// <summary>Adds the titled hover tips of a model. Never throws.</summary>
    public static void Note(IEnumerable<IHoverTip>? tips)
    {
        if (!Collecting || tips == null) return;
        try { Add(Extract(tips, null, null)); }
        catch (Exception) { /* tips are best effort */ }
    }

    /// <summary>
    /// Adds the hover tips of a model, read through <paramref name="tips"/> only when <paramref name="key"/> (the model's
    /// identity, e.g. "card:STRIKE_IRONCLAD+0") is not cached: keyword tips are static text, and building them formats
    /// localized strings, which is too slow to repeat for every card on every snapshot. A tip whose id is
    /// <paramref name="skipId"/> or whose title is <paramref name="skipTitle"/> (the model's own tooltip, already shown
    /// as its description) is left out. Never throws.
    /// </summary>
    public static void Note(string key, Func<IEnumerable<IHoverTip>?> tips, string? skipId = null, string? skipTitle = null)
    {
        if (!Collecting) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (!Cache.TryGetValue(key, out var cached) || Stopwatch.GetElapsedTime(cached.At, now) > CacheLife)
            {
                if (Cache.Count >= MaxCached) Cache.Clear();
                cached = (now, Extract(tips(), skipId, skipTitle));
                Cache[key] = cached;
            }
            Add(cached.Tips);
        }
        catch (Exception) { /* tips are best effort */ }
    }

    private static List<(string, string)> Extract(IEnumerable<IHoverTip>? tips, string? skipId, string? skipTitle)
    {
        var list = new List<(string, string)>();
        if (tips == null) return list;
        foreach (var tip in tips)
        {
            if (tip is not HoverTip ht) continue;
            if (!string.IsNullOrEmpty(skipId) && ht.Id == skipId) continue;
            var title = ModelText.Clean(ht.Title);
            var text = ModelText.Clean(ht.Description);
            if (title.Length == 0 || text.Length == 0 || title.Length > 40) continue;
            if (!string.IsNullOrEmpty(skipTitle) && title == skipTitle) continue;
            list.Add((title, text.Length > 240 ? text[..240] + "…" : text));
        }
        return list;
    }

    private static void Add(List<(string Title, string Text)> tips)
    {
        var target = _target;
        if (target == null) return;
        foreach (var (title, text) in tips)
        {
            if (target.Count >= MaxEntries) return;
            target.TryAdd(title, text);
        }
    }
}
