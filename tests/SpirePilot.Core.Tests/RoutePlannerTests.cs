using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Route;

namespace SpirePilot.Core.Tests;

public class RoutePlannerTests
{
    [Fact]
    public void Aggressive_prefers_the_elite_path()
    {
        var best = RoutePlanner.Plan(TestData.Map(), new RouteSettings { Preset = RoutePreset.Aggressive }, hpRatio: 1.0);
        Assert.Equal(new[] { "0,0", "0,1", "0,2", "0,3" }, best[0].Nodes);
        Assert.Equal(1, best[0].Count(MapNodeType.Elite));
    }

    [Fact]
    public void Safe_avoids_the_elite_path()
    {
        var best = RoutePlanner.Plan(TestData.Map(), new RouteSettings { Preset = RoutePreset.Safe });
        Assert.Equal("1,0", best[0].Nodes[0]);
    }

    [Fact]
    public void Low_hp_steers_balanced_routes_away_from_elites()
    {
        var healthy = RoutePlanner.BestNext(TestData.Map(), new RouteSettings { Preset = RoutePreset.Balanced }, hpRatio: 1.0);
        var hurt = RoutePlanner.BestNext(TestData.Map(), new RouteSettings { Preset = RoutePreset.Balanced }, hpRatio: 0.2);
        Assert.Equal("0,0", healthy);
        Assert.Equal("1,0", hurt);
    }

    [Fact]
    public void Returns_distinct_paths_sorted_by_score()
    {
        var paths = RoutePlanner.Plan(TestData.Map(), new RouteSettings(), maxPaths: 5);
        Assert.Equal(2, paths.Count);
        Assert.True(paths[0].Score >= paths[1].Score);
        Assert.NotEqual(paths[0].Nodes, paths[1].Nodes);
    }

    [Fact]
    public void Pinned_route_wins_only_for_its_act()
    {
        var settings = new RouteSettings { PinnedPath = { "1,0", "1,1" }, PinnedAct = 1 };
        Assert.Equal("1,0", RoutePlanner.PinnedNext(TestData.Map(), settings, act: 1));
        Assert.Null(RoutePlanner.PinnedNext(TestData.Map(), settings, act: 2));
    }

    [Fact]
    public void Custom_weights_are_respected()
    {
        var settings = new RouteSettings
        {
            Preset = RoutePreset.Custom,
            CustomWeights = new() { ["Elite"] = 5, ["RestSite"] = 0, ["Monster"] = 0 },
        };
        Assert.Equal("0,0", RoutePlanner.BestNext(TestData.Map(), settings));
    }

    [Fact]
    public void Empty_map_gives_no_paths()
    {
        Assert.Empty(RoutePlanner.Plan(new MapInfo(), new RouteSettings()));
    }
}

public class PinnedRouteSeedTests
{
    [Fact]
    public void A_pin_from_another_run_is_ignored()
    {
        var settings = new SpirePilot.Core.Config.RouteSettings { PinnedPath = { "1,0" }, PinnedAct = 1, PinnedSeed = "ABC" };
        Assert.Equal("1,0", RoutePlanner.PinnedNext(TestData.Map(), settings, act: 1, seed: "ABC"));
        Assert.Null(RoutePlanner.PinnedNext(TestData.Map(), settings, act: 1, seed: "XYZ"));
        settings.PinnedSeed = ""; // older settings without a seed still work
        Assert.Equal("1,0", RoutePlanner.PinnedNext(TestData.Map(), settings, act: 1, seed: "XYZ"));
    }
}
