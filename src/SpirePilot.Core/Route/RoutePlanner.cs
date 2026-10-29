using SpirePilot.Core.Config;
using SpirePilot.Core.Model;

namespace SpirePilot.Core.Route;

/// <summary>A candidate path through the act map.</summary>
public sealed class RoutePath
{
    /// <summary>Node keys in travel order, starting with a node reachable from the current position.</summary>
    public List<string> Nodes { get; init; } = new();
    public double Score { get; init; }
    public Dictionary<MapNodeType, int> Counts { get; init; } = new();

    public int Count(MapNodeType t) => Counts.TryGetValue(t, out var c) ? c : 0;

    /// <summary>Compact summary such as "E2 R2 $1 ?3 M5 T1".</summary>
    public string Summary =>
        $"M{Count(MapNodeType.Monster)} E{Count(MapNodeType.Elite)} R{Count(MapNodeType.RestSite)} " +
        $"${Count(MapNodeType.Shop)} ?{Count(MapNodeType.Event)} T{Count(MapNodeType.Treasure)}";
}

/// <summary>
/// Scores paths through the act map from the current position to the boss with per-room-type weights
/// (from the route preset or custom weights) plus a few context rules, and returns the best few distinct
/// paths. The map is a DAG (rows only go up), so a k-best dynamic program over nodes is exact.
/// </summary>
public static class RoutePlanner
{
    public static Dictionary<MapNodeType, double> WeightsFor(RouteSettings settings)
    {
        var w = settings.Preset switch
        {
            RoutePreset.Aggressive => Make(monster: 1.2, elite: 2.6, rest: 1.3, shop: 0.8, treasure: 1.5, evt: 0.8),
            RoutePreset.Safe => Make(monster: 0.8, elite: -1.5, rest: 2.0, shop: 1.2, treasure: 1.5, evt: 1.3),
            RoutePreset.Shopper => Make(monster: 1.0, elite: 0.8, rest: 1.2, shop: 2.6, treasure: 1.5, evt: 1.0),
            RoutePreset.Explorer => Make(monster: 0.8, elite: 0.8, rest: 1.2, shop: 1.0, treasure: 1.5, evt: 2.2),
            RoutePreset.Custom => FromCustom(settings.CustomWeights),
            _ => Make(monster: 1.0, elite: 1.3, rest: 1.3, shop: 1.0, treasure: 1.5, evt: 1.1),
        };
        w[MapNodeType.Boss] = 0;
        w[MapNodeType.Ancient] = 0;
        w[MapNodeType.Unknown] = w[MapNodeType.Event];
        return w;
    }

    private static Dictionary<MapNodeType, double> Make(double monster, double elite, double rest, double shop, double treasure, double evt) => new()
    {
        [MapNodeType.Monster] = monster,
        [MapNodeType.Elite] = elite,
        [MapNodeType.RestSite] = rest,
        [MapNodeType.Shop] = shop,
        [MapNodeType.Treasure] = treasure,
        [MapNodeType.Event] = evt,
    };

    private static Dictionary<MapNodeType, double> FromCustom(Dictionary<string, double> custom)
    {
        var w = Make(1, 1, 1, 1, 1.5, 1);
        foreach (var (name, value) in custom)
        {
            if (Enum.TryParse<MapNodeType>(name, ignoreCase: true, out var t) && double.IsFinite(value))
                w[t] = Math.Clamp(value, -5, 5);
        }
        return w;
    }

    /// <summary>
    /// Returns up to <paramref name="maxPaths"/> best distinct paths starting at one of the reachable nodes.
    /// <paramref name="hpRatio"/> (0..1) lowers elite value and raises rest value when HP is low.
    /// </summary>
    public static List<RoutePath> Plan(MapInfo map, RouteSettings settings, double hpRatio = 1.0, int gold = 0, int maxPaths = 3)
    {
        if (map.Nodes.Count == 0 || map.Reachable.Count == 0) return new();
        var weights = WeightsFor(settings);
        var byKey = map.Nodes.GroupBy(n => n.Key).ToDictionary(g => g.Key, g => g.First());
        // StS2 normally puts a rest site on every node of the row before the boss; then it is on every path and needs no bonus.
        var lastRow = map.Nodes.Where(n => n.Row == map.Rows - 1).ToList();
        bool preBossRestOnEveryPath = lastRow.Count > 0 && lastRow.All(n => n.Type == MapNodeType.RestSite);
        int k = Math.Max(1, maxPaths);
        var memo = new Dictionary<string, List<(double score, List<string> path)>>();

        List<(double score, List<string> path)> Best(string key, int depth)
        {
            if (memo.TryGetValue(key, out var cached)) return cached;
            var result = new List<(double, List<string>)>();
            if (!byKey.TryGetValue(key, out var node) || depth > 64)
            {
                memo[key] = result;
                return result;
            }
            double own = NodeValue(node, weights, hpRatio, gold, map.Rows, preBossRestOnEveryPath);
            var children = node.Children.Where(byKey.ContainsKey).Distinct().ToList();
            if (children.Count == 0)
            {
                result.Add((own, new List<string> { key }));
            }
            else
            {
                foreach (var child in children)
                    foreach (var (s, p) in Best(child, depth + 1))
                        result.Add((own + s, Prepend(key, p)));
            }
            memo[key] = result.OrderByDescending(r => r.Item1).Take(k).ToList();
            return memo[key];
        }

        return map.Reachable
            .Where(byKey.ContainsKey)
            .SelectMany(r => Best(r, 0))
            .OrderByDescending(r => r.score)
            .Take(k)
            .Select(r => new RoutePath
            {
                Nodes = r.path,
                Score = Math.Round(r.score, 2),
                Counts = r.path.Select(p => byKey[p].Type).Where(t => t != MapNodeType.Boss)
                    .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count()),
            })
            .ToList();
    }

    /// <summary>Best next node according to the planner, or null when there is no choice data.</summary>
    public static string? BestNext(MapInfo map, RouteSettings settings, double hpRatio = 1.0, int gold = 0) =>
        Plan(map, settings, hpRatio, gold, 1).FirstOrDefault()?.Nodes.FirstOrDefault();

    /// <summary>
    /// If the user pinned a path for this act and the next pinned node is reachable, returns it.
    /// The pinned path may include already-visited nodes; the first pinned node that is currently reachable wins.
    /// A pin made in another run (different seed) never applies: every act map uses the same coordinate grid.
    /// </summary>
    public static string? PinnedNext(MapInfo map, RouteSettings settings, int act, string? seed = null)
    {
        if (settings.PinnedPath.Count == 0 || settings.PinnedAct != act) return null;
        if (!string.IsNullOrEmpty(settings.PinnedSeed) && !string.IsNullOrEmpty(seed) && settings.PinnedSeed != seed) return null;
        return settings.PinnedPath.FirstOrDefault(key => map.Reachable.Contains(key));
    }

    private static double NodeValue(MapNodeInfo node, Dictionary<MapNodeType, double> weights, double hpRatio, int gold, int rows, bool preBossRestOnEveryPath)
    {
        double w = weights.TryGetValue(node.Type, out var v) ? v : 1.0;
        hpRatio = Math.Clamp(hpRatio, 0, 1);
        switch (node.Type)
        {
            case MapNodeType.Elite:
                // Elites are worth it above ~65-70% HP (CoreSystem); scale the penalty from there and never let a preset
                // weight walk a badly hurt player into one.
                if (hpRatio < 0.7) w -= (0.7 - hpRatio) * 6;
                if (hpRatio < 0.4) w = Math.Min(w, -1.0);
                break;
            case MapNodeType.RestSite:
                if (hpRatio < 0.6) w += (0.6 - hpRatio) * 3;
                // A late rest is worth more than an early one, unless the pre-boss row already gives every path one
                // (then boosting the row before it only rewards two rests in a row).
                if (!preBossRestOnEveryPath && rows > 0 && node.Row >= rows - 2) w += 0.5;
                break;
            case MapNodeType.Shop:
                if (gold >= 250) w += 0.8;          // removal plus a purchase
                else if (gold >= 150) w += 0.3;     // one meaningful purchase
                else if (gold < 75) w -= 0.8;
                break;
        }
        return w;
    }

    private static List<string> Prepend(string head, List<string> tail)
    {
        var list = new List<string>(tail.Count + 1) { head };
        list.AddRange(tail);
        return list;
    }
}
