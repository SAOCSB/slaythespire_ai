using Godot;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Game.Rooms;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>Map (nodes, edges, current, reachable, teammate votes) and travel/vote actions. Also Ping(nodeKey) for teammates. Called for ScreenKind.Map.</summary>
internal static class MapReader
{
    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var run = ctx.Run;
        if (run == null) return;
        ActMap? map = null;
        try { map = run.Map; }
        catch (Exception) { /* not generated yet */ }
        if (map == null) return;

        var info = new MapInfo();
        snap.Map = info;
        FillGraph(run, map, info);
        try { info.Rows = map.GetRowCount(); }
        catch (Exception) { info.Rows = info.Nodes.Count == 0 ? 0 : info.Nodes.Max(n => n.Row) + 1; }
        try { if (run.CurrentMapCoord is { } cur) info.Current = Key(cur); }
        catch (Exception) { }

        bool mp = ctx.IsMultiplayer && SafeCount(run) > 1;
        var me = ctx.Me;
        MapCoord? myVote = null;
        try { FillVotes(run, me, info, mp, out myVote); }
        catch (Exception ex) { ThrottledLog.Warn("map-votes", "map votes unreadable: " + ex.Message); }

        var screen = NMapScreen.Instance;
        if (!SceneUtil.VisibleInTree(screen) || !screen!.IsOpen) return;

        // Map votes / travel while a fight runs desync co-op (the map can be opened from the top bar mid-fight).
        if (ctx.Combat != null || RoomUtil.CombatInProgress())
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.map_combat"));
            TryAddCloseMap(sink, screen, run, anyRoom: true); // back to the fight
            return;
        }
        if (screen.IsTraveling)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.map_traveling"));
            return;
        }

        var points = MapPointNodes(screen);
        // NMapPoint.IsEnabled is a stale cache during the travel animation: a click only counts while travel is enabled.
        bool travelEnabled = screen.IsTravelEnabled || screen.IsDebugTravelEnabled;
        var reachable = new List<NMapPoint>();
        if (travelEnabled)
        {
            foreach (var p in points)
            {
                try
                {
                    if (SceneUtil.Alive(p) && p.IsEnabled && p.Point != null && p.State is not (MapPointState.Traveled or MapPointState.Untravelable))
                        reachable.Add(p);
                }
                catch (Exception) { /* point being rebuilt */ }
            }
        }
        reachable = reachable.GroupBy(p => p.Point.coord).Select(g => g.First())
            .OrderBy(p => p.Point.coord.row).ThenBy(p => p.Point.coord.col).ToList();
        foreach (var p in reachable) info.Reachable.Add(Key(p.Point.coord));

        // Votes for nodes that are not a next step any more are stale (from the previous move).
        if (info.Reachable.Count > 0)
        {
            foreach (var stale in info.TeammateVotes.Where(kv => !info.Reachable.Contains(kv.Value)).Select(kv => kv.Key).ToList())
                info.TeammateVotes.Remove(stale);
            if (myVote is { } mine && !info.Reachable.Contains(Key(mine))) myVote = null;
        }

        // In co-op the local vote is final for this decision: re-voting only invites split votes and loops.
        if (mp && myVote is { } voted)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.map_voted", Key(voted)));
            return;
        }

        foreach (var node in reachable)
        {
            try { AddTravel(sink, info, node, mp); }
            catch (Exception ex) { ThrottledLog.Warn("map-node", "map node action failed: " + ex.Message); }
        }

        if (reachable.Count == 0)
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.map_no_travel"));
            TryAddCloseMap(sink, screen, run, anyRoom: false);
        }
    }

    /// <summary>Pings a map node ("col,row") for teammates with the game's own map ping (co-op). Main thread.</summary>
    public static void Ping(string nodeKey)
    {
        try
        {
            if (!TryParseKey(nodeKey, out var coord)) return;
            if (!RoomUtil.IsMultiplayer()) return;
            var run = RoomUtil.Run();
            var map = run?.Map;
            if (map == null || !(map.HasPoint(coord) || IsSpecial(map, coord))) return;
            RunManager.Instance?.FlavorSynchronizer?.SendMapPing(coord); // debounced by the game
        }
        catch (Exception ex)
        {
            ModLog.Warn("map ping failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ reading

    private static void FillGraph(RunState run, ActMap map, MapInfo info)
    {
        var visited = new HashSet<MapCoord>();
        try { foreach (var c in run.VisitedMapCoords) visited.Add(c); }
        catch (Exception) { }

        foreach (var point in AllPoints(map))
        {
            try
            {
                var type = MapType(point.PointType);
                var node = new MapNodeInfo
                {
                    Key = Key(point.coord),
                    Col = point.coord.col,
                    Row = point.coord.row,
                    Type = type,
                    TypeName = RoomUtil.T("node." + type),
                    Visited = visited.Contains(point.coord),
                };
                foreach (var child in point.Children.OrderBy(c => c.coord.col))
                    if (child != null) node.Children.Add(Key(child.coord));
                info.Nodes.Add(node);
            }
            catch (Exception) { /* skip a broken point */ }
        }
        info.Nodes.Sort((a, b) => a.Row != b.Row ? a.Row.CompareTo(b.Row) : a.Col.CompareTo(b.Col));
    }

    /// <summary>GetAllMapPoints() leaves out the starting and boss points; add them.</summary>
    private static List<MapPoint> AllPoints(ActMap map)
    {
        var byCoord = new Dictionary<MapCoord, MapPoint>();
        void Add(MapPoint? p)
        {
            if (p != null) byCoord[p.coord] = p;
        }
        try { foreach (var p in map.GetAllMapPoints()) Add(p); }
        catch (Exception) { }
        try { Add(map.StartingMapPoint); } catch (Exception) { }
        try { Add(map.BossMapPoint); } catch (Exception) { }
        try { Add(map.SecondBossMapPoint); } catch (Exception) { }
        return byCoord.Values.ToList();
    }

    private static bool IsSpecial(ActMap map, MapCoord coord)
    {
        try
        {
            return map.StartingMapPoint?.coord == coord || map.BossMapPoint?.coord == coord || map.SecondBossMapPoint?.coord == coord;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Teammates' votes (synchronizer first, the map screen's vote dictionary as fallback) and the local vote.</summary>
    private static void FillVotes(RunState run, Player? me, MapInfo info, bool mp, out MapCoord? myVote)
    {
        myVote = null;
        if (!mp) return;
        var sync = RunManager.Instance?.MapSelectionSynchronizer;
        int generation = -1;
        try { generation = sync?.MapGenerationCount ?? -1; } catch (Exception) { }

        MapCoord? VoteOf(Player p)
        {
            try
            {
                if (sync?.GetVote(p) is { } v && (generation < 0 || v.mapGenerationCount == generation)) return v.coord;
            }
            catch (Exception) { }
            try
            {
                var dict = NMapScreen.Instance?.PlayerVoteDictionary;
                if (dict != null && dict.TryGetValue(p, out var c) && c.HasValue) return c.Value;
            }
            catch (Exception) { }
            return null;
        }

        if (me != null) myVote = VoteOf(me);
        foreach (var mate in RoomUtil.Teammates(run, me))
        {
            if (VoteOf(mate) is { } c) info.TeammateVotes[RoomUtil.PlayerName(mate)] = Key(c);
        }
    }

    /// <summary>
    /// Map point nodes of the open map: the screen's own coord→node dictionary plus start/boss nodes (private fields),
    /// falling back to a scene scan of the points container (the paths container holds thousands of tick nodes).
    /// </summary>
    private static List<NMapPoint> MapPointNodes(NMapScreen screen)
    {
        var list = new List<NMapPoint>();
        try
        {
            var dict = Reflect.Get<Dictionary<MapCoord, NMapPoint>>(screen, "_mapPointDictionary");
            if (dict != null) list.AddRange(dict.Values.Where(SceneUtil.Valid));
            foreach (var f in new[] { "_startingPointNode", "_bossPointNode", "_secondBossPointNode" })
                if (Reflect.Get<NMapPoint>(screen, f) is { } p && SceneUtil.Valid(p)) list.Add(p);
        }
        catch (Exception) { list.Clear(); }
        if (list.Count == 0)
        {
            var root = RoomUtil.NodeOf<Control>(screen, "_points") ?? (Node)screen;
            list.AddRange(SceneUtil.Descendants<NMapPoint>(root));
        }
        return list.Distinct().ToList();
    }

    // ------------------------------------------------------------------ actions

    private static void AddTravel(ActionSink sink, MapInfo info, NMapPoint node, bool mp)
    {
        var coord = node.Point.coord;
        var key = Key(coord);
        var type = MapType(node.Point.PointType);
        var label = RoomUtil.T("act.map_node", RoomUtil.T("node." + type), key);
        var voters = info.TeammateVotes.Where(kv => kv.Value == key).Select(kv => kv.Key).ToList();
        if (voters.Count > 0) label += " " + RoomUtil.T("rooms.voted_by", string.Join(", ", voters));
        sink.Add(ActionKind.ChooseMapNode, label, "map:" + key,
            ct => Travel(node, coord, mp, ct),
            new Dictionary<string, string> { [ActionArgs.Node] = key, ["type"] = type.ToString() },
            unpredictable: true);
    }

    private static async Task<ActionResult> Travel(NMapPoint node, MapCoord coord, bool mp, CancellationToken ct)
    {
        var screen = NMapScreen.Instance;
        if (!SceneUtil.VisibleInTree(screen) || !screen!.IsOpen) return ActionResult.Fail("map is not open");
        if (!SceneUtil.Alive(node)) return ActionResult.Fail("map node no longer exists");
        if (RoomUtil.CombatInProgress()) return ActionResult.Fail("combat in progress");
        if (screen.IsTraveling) return ActionResult.Fail("already travelling");
        if (!(screen.IsTravelEnabled || screen.IsDebugTravelEnabled) || !node.IsEnabled) return ActionResult.Fail("node is not travelable right now");

        var rm = RunManager.Instance;
        if (rm == null) return ActionResult.Fail("no run");
        var me = RoomUtil.Me();
        bool entered = false;
        void OnEntered() => entered = true;
        rm.RoomEntered += OnEntered;
        try
        {
            if (mp)
            {
                // Co-op: record the local vote through the screen (synced to everyone); travel starts when all voted.
                screen.OnMapPointSelectedLocally(node);
            }
            else
            {
                // Single player: the point's own click handler keeps the game's travel guards.
                var pressed = UiActions.Press(node, "map node");
                if (!pressed.Success) return pressed;
            }

            bool AtTarget()
            {
                try { return RoomUtil.Run()?.CurrentMapCoord is { } c && c == coord; }
                catch (Exception) { return false; }
            }
            bool VoteRegistered()
            {
                try
                {
                    return me != null && rm.MapSelectionSynchronizer?.GetVote(me) is { } v && v.coord == coord;
                }
                catch (Exception) { return false; }
            }
            bool Started()
            {
                if (entered || AtTarget() || (mp && VoteRegistered())) return true;
                if (!SceneUtil.Alive(screen)) return true;
                return screen.IsTraveling || !(screen.IsTravelEnabled || screen.IsDebugTravelEnabled);
            }

            var registered = await UiActions.Expect(Started, "map travel", TimeSpan.FromSeconds(5), ct);
            if (!registered.Success) return registered;

            if (mp && !entered && !AtTarget() && SceneUtil.Alive(screen) && !screen.IsTraveling)
            {
                await UiActions.WaitSettled(TimeSpan.FromSeconds(2), ct);
                return ActionResult.Ok("vote submitted; waiting for teammates");
            }

            // Travel animation, then the room loads.
            bool arrived = await MainThread.WaitUntil(
                () => entered || (AtTarget() && !(SceneUtil.Alive(screen) && screen.IsTraveling)), TimeSpan.FromSeconds(20), ct);
            await UiActions.WaitSettled(ct: ct);
            return ActionResult.Ok(arrived ? "" : "travelling");
        }
        finally
        {
            rm.RoomEntered -= OnEntered;
        }
    }

    /// <summary>
    /// The map can be open over an unfinished room or a running fight (opened from the top bar, or pushed open by a
    /// teammate in co-op) with travel disabled. Then the only way on is the map's own back button.
    /// </summary>
    private static void TryAddCloseMap(ActionSink sink, NMapScreen screen, RunState run, bool anyRoom)
    {
        AbstractRoom? room = null;
        try { room = run.CurrentRoom; } catch (Exception) { }
        // Assumption (unverified): the back button only shows when the map was opened from the top bar, so closing the map
        // never undoes a finished room's proceed.
        if (!anyRoom && room is null or MapRoom) return;
        var back = RoomUtil.NodeOf<NBackButton>(screen, "_backButton", "%BackButton", "BackButton");
        if (!SceneUtil.Usable(back)) return;
        sink.Add(ActionKind.Proceed, RoomUtil.T("rooms.close_map"), "map:close", async ct =>
        {
            if (!SceneUtil.Alive(screen)) return ActionResult.Fail("map already closed");
            var r = UiActions.Press(back, "close map");
            if (!r.Success) return r;
            r = await UiActions.Expect(() => !SceneUtil.Alive(screen) || !screen.IsOpen || RoomUtil.Left(screen), "close map", ct: ct);
            await UiActions.WaitSettled(ct: ct);
            return r;
        });
    }

    // ------------------------------------------------------------------ helpers

    public static string Key(MapCoord c) => c.col + "," + c.row;

    public static bool TryParseKey(string? key, out MapCoord coord)
    {
        coord = default;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var parts = key.Split(',');
        if (parts.Length != 2 || !int.TryParse(parts[0].Trim(), out var col) || !int.TryParse(parts[1].Trim(), out var row)) return false;
        coord = new MapCoord(col, row);
        return true;
    }

    private static int SafeCount(RunState run)
    {
        try { return run.Players.Count; }
        catch (Exception) { return 1; }
    }

    /// <summary>The game's map point type → snapshot type. "Unknown" is the game's "?" (event) node.</summary>
    public static MapNodeType MapType(MapPointType t) => t switch
    {
        MapPointType.Monster => MapNodeType.Monster,
        MapPointType.Elite => MapNodeType.Elite,
        MapPointType.RestSite => MapNodeType.RestSite,
        MapPointType.Shop => MapNodeType.Shop,
        MapPointType.Treasure => MapNodeType.Treasure,
        MapPointType.Unknown => MapNodeType.Event,
        MapPointType.Boss => MapNodeType.Boss,
        MapPointType.Ancient => MapNodeType.Ancient,
        _ => MapNodeType.Unknown,
    };
}
