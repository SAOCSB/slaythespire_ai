using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Game.Rooms;
using SpirePilot.Infra;

namespace SpirePilot.Game.Readers;

/// <summary>
/// The act map. <see cref="FillModel"/> builds <see cref="GameSnapshot.Map"/> (nodes, current node, rows, model-based
/// next nodes, teammate votes) on every in-run snapshot from the run model only; <see cref="Read"/> (ScreenKind.Map)
/// adds the live travel / vote actions of the open map; <see cref="TryAddOpenMap"/> offers the top-bar map button
/// when a finished room offers nothing else. Also Ping(nodeKey) for teammates.
/// </summary>
internal static class MapReader
{
    // ================================================================== model (every in-run snapshot)

    /// <summary>The graph part of <see cref="MapInfo"/>, cached while the map, its content and the current node stay the same.</summary>
    private sealed record ModelEntry(ActMap Map, int Generation, string? Current, int Visited, long Signature, string Language,
        List<MapNodeInfo> Nodes, int Rows, List<string> Reachable)
    {
        /// <summary>When the content signature was last confirmed (it is re-checked at most once a second).</summary>
        public long CheckedAt { get; set; } = System.Diagnostics.Stopwatch.GetTimestamp();

        public bool SameKey(ActMap map, int generation, string? current, int visited) =>
            ReferenceEquals(Map, map) && Generation == generation && Current == current && Visited == visited && Language == L10n.Language;
    }

    private static readonly TimeSpan SignatureRecheck = TimeSpan.FromSeconds(1);

    private static ModelEntry? _model;

    /// <summary>
    /// Fills <see cref="GameSnapshot.Map"/> from the run model (never from the map screen): the graph, the current
    /// node, the rows, teammates' votes and the next nodes as the model sees them (children of the current node, or
    /// the act's starting point). Travel actions are added only by <see cref="Read"/>, from the live map.
    /// </summary>
    public static void FillModel(GameContext ctx, GameSnapshot snap)
    {
        var run = ctx.Run;
        if (run == null) return;
        ActMap? map = null;
        try { map = run.Map; }
        catch (Exception) { /* not generated yet */ }
        if (map == null) return;

        var model = Model(run, map);
        var info = new MapInfo
        {
            Nodes = new List<MapNodeInfo>(model.Nodes),
            Rows = model.Rows,
            Current = model.Current,
            Reachable = new List<string>(model.Reachable),
        };
        snap.Map = info;
        try { FillVotes(run, ctx.Me, info, IsCoop(ctx, run)); }
        catch (Exception ex) { ThrottledLog.Warn("map-votes", "map votes unreadable: " + ex.Message); }
    }

    private static ModelEntry Model(RunState run, ActMap map)
    {
        int generation = -1, visited = -1;
        string? current = null;
        try { generation = RunManager.Instance?.MapSelectionSynchronizer?.MapGenerationCount ?? -1; } catch (Exception) { }
        try { visited = run.VisitedMapCoords.Count; } catch (Exception) { }
        try { if (run.CurrentMapCoord is { } cur) current = Key(cur); } catch (Exception) { }
        var cached = _model;
        bool sameKey = cached != null && cached.SameKey(map, generation, current, visited);
        if (sameKey && System.Diagnostics.Stopwatch.GetElapsedTime(cached!.CheckedAt) < SignatureRecheck) return cached;
        var points = AllPoints(map);
        long signature = Signature(points);
        if (sameKey && cached!.Signature == signature)
        {
            cached.CheckedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            return cached;
        }

        var info = new MapInfo();
        FillGraph(run, points, info);
        try { info.Rows = map.GetRowCount(); }
        catch (Exception) { info.Rows = info.Nodes.Count == 0 ? 0 : info.Nodes.Max(n => n.Row) + 1; }
        var reachable = ModelNext(run, map).Select(p => p.coord).Distinct()
            .OrderBy(c => c.row).ThenBy(c => c.col).Select(Key).ToList();
        _model = new ModelEntry(map, generation, current, visited, signature, L10n.Language, info.Nodes, info.Rows, reachable);
        return _model;
    }

    /// <summary>Cheap fingerprint of the map content (node types and edges change when relics / events modify the map).</summary>
    private static long Signature(List<MapPoint> points)
    {
        long sig = points.Count;
        foreach (var p in points)
        {
            try { sig = sig * 31 + (p.coord.col * 64 + p.coord.row) * 16 + (int)p.PointType + p.Children.Count * 1024; }
            catch (Exception) { sig = sig * 31 + 7; }
        }
        return sig;
    }

    /// <summary>The next map points according to the model: children of the current node, else the act's starting point.</summary>
    private static IEnumerable<MapPoint> ModelNext(RunState run, ActMap map)
    {
        try
        {
            if (run.CurrentMapCoord is { } cur)
            {
                var from = PointAt(map, cur);
                return from?.Children.Where(c => c != null).ToList() ?? new List<MapPoint>();
            }
            if (map.StartingMapPoint is { } start) return new[] { start };
            if (map.startMapPoints is { Count: > 0 } starts) return starts.Where(p => p != null).ToList();
            return map.GetPointsInRow(0).Where(p => p != null).ToList();
        }
        catch (Exception) { return Array.Empty<MapPoint>(); }
    }

    private static MapPoint? PointAt(ActMap map, MapCoord coord)
    {
        try { if (map.GetPoint(coord) is { } p) return p; }
        catch (Exception) { /* outside the grid (start / boss) */ }
        foreach (var special in Specials(map))
            if (special != null && special.coord == coord) return special;
        return null;
    }

    private static IEnumerable<MapPoint?> Specials(ActMap map)
    {
        MapPoint? Safe(Func<MapPoint?> get)
        {
            try { return get(); }
            catch (Exception) { return null; }
        }
        yield return Safe(() => map.StartingMapPoint);
        yield return Safe(() => map.BossMapPoint);
        yield return Safe(() => map.SecondBossMapPoint);
    }

    private static bool IsCoop(GameContext ctx, RunState run) => ctx.IsMultiplayer && SafeCount(run) > 1;

    // ================================================================== live map screen (ScreenKind.Map)

    public static void Read(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        var run = ctx.Run;
        if (run == null) return;
        if (snap.Map == null) FillModel(ctx, snap);
        var info = snap.Map;
        if (info == null) return;

        bool mp = IsCoop(ctx, run);
        var me = ctx.Me;
        MapCoord? myVote = mp && me != null ? VoteOf(me) : null;

        var screen = NMapScreen.Instance;
        if (!SceneUtil.VisibleInTree(screen) || !screen!.IsOpen) return;

        // Map votes / travel while a fight runs desync co-op (the map can be opened from the top bar mid-fight).
        if (ctx.Combat != null || RoomUtil.CombatInProgress())
        {
            RoomUtil.Note(snap, RoomUtil.T("rooms.map_combat"));
            TryAddCloseMap(snap, sink, screen, run, anyRoom: true); // back to the fight
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
        if (reachable.Count > 0)
        {
            // The live map is the authority on where a click can go right now.
            info.Reachable.Clear();
            foreach (var p in reachable) info.Reachable.Add(Key(p.Point.coord));
        }

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
            TryAddCloseMap(snap, sink, screen, run, anyRoom: false);
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

    private static void FillGraph(RunState run, List<MapPoint> points, MapInfo info)
    {
        var visited = new HashSet<MapCoord>();
        try { foreach (var c in run.VisitedMapCoords) visited.Add(c); }
        catch (Exception) { }

        foreach (var point in points)
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
        try
        {
            foreach (var p in map.GetAllMapPoints())
                if (p != null) byCoord[p.coord] = p;
        }
        catch (Exception) { }
        foreach (var p in Specials(map))
            if (p != null) byCoord[p.coord] = p;
        return byCoord.Values.ToList();
    }

    private static bool IsSpecial(ActMap map, MapCoord coord) => Specials(map).Any(p => p != null && p.coord == coord);

    /// <summary>A player's current map vote: the synchronizer first (current map generation only), the map screen's vote dictionary as fallback.</summary>
    private static MapCoord? VoteOf(Player p)
    {
        var sync = RunManager.Instance?.MapSelectionSynchronizer;
        try
        {
            int generation = sync?.MapGenerationCount ?? -1;
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

    /// <summary>Teammates' votes (co-op only).</summary>
    private static void FillVotes(RunState run, Player? me, MapInfo info, bool mp)
    {
        if (!mp) return;
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
                if (Reflect.GetOptional<NMapPoint>(screen, f) is { } p && SceneUtil.Valid(p)) list.Add(p);
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

    /// <summary>
    /// Whether a map point accepts a click right now: its protected <c>IsInputAllowed()</c> (false during the start-of-act
    /// animation and the map's brief input lock) and <c>IsTravelable</c>. A member that cannot be read counts as true.
    /// </summary>
    private static bool PointReady(NMapPoint node)
    {
        if (!SceneUtil.Alive(node)) return false;
        if (Reflect.TryCallOptional(node, "IsInputAllowed", out var allowed) && allowed is false) return false;
        return Reflect.GetOptional<object>(node, "IsTravelable") is not false;
    }

    private static async Task<ActionResult> Travel(NMapPoint node, MapCoord coord, bool mp, CancellationToken ct)
    {
        var screen = NMapScreen.Instance;
        if (!SceneUtil.VisibleInTree(screen) || !screen!.IsOpen) return ActionResult.Fail("map is not open");
        if (!SceneUtil.Alive(node)) return ActionResult.Fail("map node no longer exists");
        if (RoomUtil.CombatInProgress()) return ActionResult.Fail("combat in progress");
        if (screen.IsTraveling) return ActionResult.Fail("already travelling");
        bool TravelOn() => SceneUtil.Alive(screen) && (screen.IsTravelEnabled || screen.IsDebugTravelEnabled);
        if (!TravelOn() || !node.IsEnabled) return ActionResult.Fail("node is not travelable right now");

        var rm = RunManager.Instance;
        if (rm == null) return ActionResult.Fail("no run");
        var me = RoomUtil.Me();
        bool entered = false;
        void OnEntered() => entered = true;

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
            return screen.IsTraveling || !TravelOn();
        }

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
                // Single player: the point's own click handler keeps the game's travel guards, but it swallows a click
                // during the start-of-act animation / the map's brief input lock. Wait until the point takes input.
                await MainThread.WaitUntil(() => !SceneUtil.Alive(node) || !TravelOn() || PointReady(node), TimeSpan.FromSeconds(2), ct);
                if (!SceneUtil.Alive(node)) return ActionResult.Fail("map node no longer exists");
                if (!TravelOn()) return ActionResult.Fail("travel was disabled before the click");
                var pressed = UiActions.Press(node, "map node");
                if (!pressed.Success) return pressed;
                if (!await MainThread.WaitUntil(Started, TimeSpan.FromSeconds(0.5), ct) &&
                    SceneUtil.Alive(node) && TravelOn() && !Started() && PointReady(node))
                {
                    pressed = UiActions.Press(node, "map node"); // the first click was swallowed
                    if (!pressed.Success && !Started()) return pressed;
                }
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
    /// The map can be open over an unfinished room or a running fight (opened from the top bar by the human, or pushed
    /// open by a teammate's proceed in co-op) with travel disabled; then the only way on is the map's own back button.
    /// Offered only after the map has stayed open with travel disabled for <see cref="MapWatch.CloseAfter"/>, and never
    /// when the AI itself proceeded from this room (closing would loop with the room's proceed).
    /// </summary>
    private static void TryAddCloseMap(GameSnapshot snap, ActionSink sink, NMapScreen screen, RunState run, bool anyRoom)
    {
        AbstractRoom? room = null;
        try { room = run.CurrentRoom; } catch (Exception) { }
        if (!anyRoom && room is null or MapRoom) return;
        var back = RoomUtil.NodeOf<NBackButton>(screen, "_backButton", "%BackButton", "BackButton");
        if (!SceneUtil.Usable(back)) return;
        if (!MapWatch.CloseAllowed(out var note))
        {
            if (note != null) RoomUtil.Note(snap, note);
            return;
        }
        sink.Add(ActionKind.Proceed, RoomUtil.T("rooms.close_map"), "map:close", async ct =>
        {
            if (!SceneUtil.Alive(screen) || !screen.IsOpen) return ActionResult.Fail("map already closed");
            var r = UiActions.Press(back, "close map");
            if (!r.Success) return r;
            MapWatch.NoteClosedByAi();
            r = await UiActions.Expect(() => !SceneUtil.Alive(screen) || !screen.IsOpen || RoomUtil.Left(screen), "close map", ct: ct);
            await UiActions.WaitSettled(ct: ct);
            return r;
        });
    }

    // ------------------------------------------------------------------ open map (finished room, nothing else to do)

    /// <summary>A finished room has to offer nothing for this long before "open map" is offered.</summary>
    private static readonly TimeSpan OpenAfterIdle = TimeSpan.FromSeconds(4);

    /// <summary>
    /// When the map is closed over a finished room (the human closed it, or the game did not open it) and the room
    /// offers nothing, offers the top-bar map button. "Finished" means the map screen already allows travel, or (single
    /// player) the room is a combat room in its finished mode. Returns true when the action was added. Call after the
    /// screen reader, with the probe of this snapshot.
    /// </summary>
    public static bool TryAddOpenMap(GameContext ctx, ScreenProbe probe, GameSnapshot snap, ActionSink sink)
    {
        bool roomScreen = probe.Kind is ScreenKind.Combat or ScreenKind.Event or ScreenKind.RestSite or ScreenKind.Shop or ScreenKind.Treasure;
        var idle = MapWatch.IdleFor(probe.Screen, ctx.InRun && roomScreen && sink.Count == 0 && probe.HumanOnlyReason == null);
        if (idle < OpenAfterIdle || !MapWatch.ReopenAllowed) return false;
        if (RoomUtil.CombatInProgress() || RoomUtil.ModalOpen()) return false;
        try
        {
            if (NOverlayStack.Instance is { } stack && SceneUtil.Alive(stack) && stack.ScreenCount > 0) return false;
        }
        catch (Exception) { return false; }

        var map = NMapScreen.Instance;
        if (!SceneUtil.Alive(map)) return false;
        bool travelEnabled;
        try
        {
            if (map!.IsOpen || map.IsTraveling) return false;
            travelEnabled = map.IsTravelEnabled;
        }
        catch (Exception) { return false; }
        bool finishedFight = !IsCoopNow(ctx) && probe.Screen is NCombatRoom room && SceneUtil.Alive(room) && SafeMode(room) == CombatRoomMode.FinishedCombat;
        if (!travelEnabled && !finishedFight) return false;

        var button = MapButton();
        if (!SceneUtil.Usable(button)) return false;
        sink.Add(ActionKind.Proceed, RoomUtil.T("rooms.open_map"), "map:open", async ct =>
        {
            var screen = NMapScreen.Instance;
            if (SceneUtil.Alive(screen) && screen!.IsOpen) return ActionResult.Ok("map already open");
            var pressed = UiActions.Press(MapButton(), "open map");
            if (!pressed.Success) return pressed;
            var r = await UiActions.Expect(() => NMapScreen.Instance is { } s && SceneUtil.Alive(s) && s.IsOpen, "open map", TimeSpan.FromSeconds(5), ct);
            await UiActions.WaitSettled(TimeSpan.FromSeconds(3), ct);
            return r;
        }, unpredictable: true);
        return true;
    }

    private static bool IsCoopNow(GameContext ctx) => ctx.Run != null && IsCoop(ctx, ctx.Run);

    private static CombatRoomMode? SafeMode(NCombatRoom room)
    {
        try { return room.Mode; }
        catch (Exception) { return null; }
    }

    private static NClickableControl? MapButton()
    {
        try { return NRun.Instance?.GlobalUi?.TopBar?.Map; }
        catch (Exception) { return null; }
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
