using System.Globalization;
using Godot;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Route;
using SpirePilot.Core.Text;
using SpirePilot.Infra;

namespace SpirePilot.Ui.Pages;

/// <summary>
/// Tab 2: route preset / custom weights / instructions, the planner's best paths and the pinned path. Plans come from
/// the act map of the latest in-run snapshot (any screen, not only the open map); the last map seen per run + act is
/// kept, so the tab keeps working between captures that carry no map.
/// </summary>
internal sealed class RoutePage : Page
{
    private static readonly RoutePreset[] Presets =
        { RoutePreset.Balanced, RoutePreset.Aggressive, RoutePreset.Safe, RoutePreset.Shopper, RoutePreset.Explorer, RoutePreset.Custom };

    private static readonly MapNodeType[] WeightTypes =
        { MapNodeType.Monster, MapNodeType.Elite, MapNodeType.RestSite, MapNodeType.Shop, MapNodeType.Treasure, MapNodeType.Event };

    /// <summary>Summary order: the room types that shape a route first.</summary>
    private static readonly MapNodeType[] SummaryOrder =
        { MapNodeType.Elite, MapNodeType.RestSite, MapNodeType.Shop, MapNodeType.Event, MapNodeType.Treasure, MapNodeType.Monster };

    private OptionButton _preset = null!;
    private readonly Dictionary<MapNodeType, (HSlider slider, Label value)> _weights = new();
    private TextEdit _instruction = null!;
    private CheckButton _letAi = null!, _ping = null!;
    private Label _plansHint = null!;
    private Label _mapNote = null!;
    private VBoxContainer _plans = null!;
    private Label _pinned = null!;
    private Button _clearPin = null!;
    private OptionButton _nextNode = null!;
    private Button _pinNext = null!;
    private readonly List<string> _nextNodes = new();
    private string _nextKey = "";
    private string _planKey = "";

    /// <summary>Id of the placeholder item ("no next node"). Not -1: Godot replaces a -1 id with the item's index.</summary>
    private const int NoItemId = 10_000;

    /// <summary>The map the plans were last drawn from (static: survives a rebuild of the panel).</summary>
    private static MapInfo? _cachedMap;
    private static string _cachedSeed = "";
    private static int _cachedAct;

    /// <summary>The act map to plan on: this snapshot's, else the cached one of the same run and act.</summary>
    private readonly record struct MapView(MapInfo Map, string Seed, int Act, bool Cached);

    public override string TitleKey => "ui.tab.route";

    protected override void BuildContent()
    {
        var pref = UiKit.Card(Body, L10n.T("ui.route.preset"));
        _preset = UiKit.Options(OnPreset, "route.preset");
        UiKit.SetItems(_preset, Presets.Select(p => (L10n.T("ui.route.preset." + p), (int)p)));
        pref.AddChild(_preset);

        var weights = UiKit.Card(Body, L10n.T("ui.route.weights"), L10n.T("ui.route.weights_hint"));
        foreach (var type in WeightTypes)
        {
            var t = type;
            var value = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text, wrap: false);
            value.CustomMinimumSize = new Vector2(44, 0);
            value.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
            value.HorizontalAlignment = HorizontalAlignment.Right;
            var slider = UiKit.Slider(-3, 5, 0.1, v =>
            {
                UiKit.SetText(value, Fmt(v));
                Edit(s => s.Route.CustomWeights[t.ToString()] = Math.Round(v, 2));
                _planKey = "";
            }, "weight." + t);
            _weights[t] = (slider, value);
            UiKit.Field(weights, L10n.T("ui.route.short." + t), UiKit.Row(slider, value));
        }

        var how = UiKit.Card(Body, L10n.T("ui.route.instruction"));
        _instruction = UiKit.Area(L10n.T("ui.route.instruction_ph"), text => Edit(s => s.Route.Instruction = text), 56, "route.instruction");
        how.AddChild(_instruction);
        _letAi = UiKit.Switch(L10n.T("ui.route.let_ai"), on => Edit(s => s.Route.LetAiChooseNodes = on), L10n.T("ui.route.let_ai_tip"));
        _ping = UiKit.Switch(L10n.T("ui.route.ping"), on => Edit(s => s.Route.PingChoiceToTeammates = on));
        how.AddChild(_letAi);
        how.AddChild(_ping);

        var pinned = UiKit.Card(Body, null);
        _pinned = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text);
        _clearPin = UiKit.Button(L10n.T("ui.clear"), ClearPin, UiTheme.ButtonKind.Small);
        _clearPin.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        pinned.AddChild(UiKit.Row(_pinned, _clearPin));
        // Any next node, not only the first step of a suggested route (a particular shop, the other elite ...).
        _nextNode = UiKit.Options(_ => { }, "route.next");
        _pinNext = UiKit.Button(L10n.T("ui.route.pin_next"), PinNext, UiTheme.ButtonKind.Small);
        _pinNext.Disabled = true;
        UiKit.Field(pinned, L10n.T("ui.route.next_node"), UiKit.Row(_nextNode, _pinNext));

        var plans = UiKit.Card(Body, L10n.T("ui.route.plans"));
        _plansHint = UiKit.Muted(L10n.T("ui.route.no_map"));
        plans.AddChild(_plansHint);
        _mapNote = UiKit.Label(L10n.T("ui.route.cached_map"), UiTheme.FontSmall, UiTheme.Faint);
        _mapNote.Visible = false;
        plans.AddChild(_mapNote);
        _plans = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        _plans.AddThemeConstantOverride("separation", 6);
        plans.AddChild(_plans);
    }

    private static string Fmt(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

    public override void Sync(PilotSettings s)
    {
        UiKit.SelectId(_preset, (int)s.Route.Preset);
        ShowWeights(s.Route);
        UiKit.SetText(_instruction, s.Route.Instruction);
        UiKit.SetPressed(_letAi, s.Route.LetAiChooseNodes);
        UiKit.SetPressed(_ping, s.Route.PingChoiceToTeammates);
        _planKey = "";
        UpdatePinned();
    }

    public override void Refresh(bool slow)
    {
        if (!slow) return;
        UpdatePlans();
        UpdatePinned();
        UpdateNextNodes();
    }

    private void ShowWeights(RouteSettings route)
    {
        var w = RoutePlanner.WeightsFor(route);
        bool custom = route.Preset == RoutePreset.Custom;
        foreach (var (type, (slider, value)) in _weights)
        {
            var v = w.TryGetValue(type, out var x) ? x : 1.0;
            UiKit.SetValue(slider, v);
            slider.Editable = custom;
            slider.Modulate = custom ? Colors.White : new Color(1, 1, 1, 0.55f);
            UiKit.SetText(value, Fmt(v));
        }
    }

    private void OnPreset(int id)
    {
        var preset = (RoutePreset)id;
        Edit(s =>
        {
            // Start custom weights from the preset the user had, so "Custom" means "tweak what I had".
            if (preset == RoutePreset.Custom && s.Route.Preset != RoutePreset.Custom)
            {
                var w = RoutePlanner.WeightsFor(s.Route);
                foreach (var t in WeightTypes) s.Route.CustomWeights[t.ToString()] = Math.Round(w[t], 2);
            }
            s.Route.Preset = preset;
        });
        ShowWeights(Draft.Route);
        _planKey = "";
        UpdatePlans();
    }

    /// <summary>
    /// The map to plan on. Every in-run snapshot carries the act map (null when it could not be read); the last one
    /// seen is cached per run seed + act, so a capture without a map does not empty the tab. Null outside a run.
    /// </summary>
    private static MapView? CurrentMap(GameSnapshot? snap)
    {
        if (snap == null || !snap.InRun) return null;
        var seed = snap.Run?.Seed ?? "";
        var act = snap.Run?.Act ?? 0;
        if (snap.Map is { Nodes.Count: > 0 } map)
        {
            _cachedMap = map;
            _cachedSeed = seed;
            _cachedAct = act;
            return new MapView(WithNextNodes(map), seed, act, false);
        }
        if (_cachedMap == null) return null;
        // Without run info (unreadable this time) the cached map is the best guess; otherwise it must be the same run and act.
        if (snap.Run == null) return new MapView(WithNextNodes(_cachedMap), _cachedSeed, _cachedAct, true);
        if (seed != _cachedSeed || act != _cachedAct) return null;
        return new MapView(WithNextNodes(_cachedMap), seed, act, true);
    }

    /// <summary>
    /// The map with its next nodes: <see cref="MapInfo.Reachable"/> as read, or, when a snapshot has none (e.g. read
    /// while travel is disabled), the children of the current node. A copy: the snapshot's map is shared.
    /// </summary>
    private static MapInfo WithNextNodes(MapInfo map)
    {
        if (map.Reachable.Count > 0 || map.Current == null || map.Find(map.Current) is not { } current || current.Children.Count == 0)
            return map;
        return new MapInfo
        {
            Nodes = map.Nodes,
            Current = map.Current,
            Rows = map.Rows,
            TeammateVotes = map.TeammateVotes,
            Reachable = current.Children.Where(c => map.Find(c) != null).Distinct().ToList(),
        };
    }

    /// <summary>
    /// Whether the pin was made in another run: its seed differs from the current run's (the same rule as
    /// <see cref="RoutePlanner.PinnedNext"/>; a pin without a seed predates seeds and is not judged).
    /// </summary>
    private static bool PinFromOtherRun(RouteSettings route, string seed) =>
        route.PinnedPath.Count > 0 && route.PinnedSeed.Length > 0 && seed.Length > 0 && route.PinnedSeed != seed;

    private void UpdatePlans()
    {
        var snap = Snap;
        var view = CurrentMap(snap);
        _mapNote.Visible = view is { Cached: true };
        if (snap == null || view is not { } v)
        {
            ShowHint(snap is { InRun: true } ? "ui.route.no_map_yet" : "ui.route.no_map");
            if (_plans.GetChildCount() > 0) Clear(_plans);
            _planKey = "";
            return;
        }
        var map = v.Map;
        var me = snap.LocalPlayer;
        double hp = me != null && me.MaxHp > 0 ? (double)me.Hp / me.MaxHp : 1.0;
        int gold = me?.Gold ?? 0;
        var route = Draft.Route;
        int act = v.Act;
        string seed = v.Seed;
        string key = string.Join(";", map.Reachable) + "|" + map.Current + "|" + map.Nodes.Count + "|" + route.Preset + "|" +
                     string.Join(",", route.CustomWeights.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.ToString(CultureInfo.InvariantCulture))) +
                     "|" + Math.Round(hp, 2) + "|" + gold / 25 + "|" + act + "|" + seed + "|" +
                     string.Join(",", route.PinnedPath) + "|" + route.PinnedAct + "|" + route.PinnedSeed;
        if (key == _planKey) return;
        _planKey = key;

        var plans = map.Reachable.Count > 0 ? RoutePlanner.Plan(map, route, hp, gold, 5) : new List<RoutePath>();
        Clear(_plans);
        if (plans.Count == 0) ShowHint("ui.route.no_paths");
        else _plansHint.Visible = false;
        bool pinnedHere = route.PinnedAct == act && route.PinnedPath.Count > 0 && !PinFromOtherRun(route, seed);
        for (int i = 0; i < plans.Count; i++)
        {
            var path = plans[i];
            bool isPinned = pinnedHere && path.Nodes.SequenceEqual(route.PinnedPath);
            var row = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
            row.AddThemeStyleboxOverride("panel", UiTheme.Box(new Color(1, 1, 1, isPinned ? 0.07f : 0.03f),
                isPinned ? new Color(UiTheme.Gold, 0.7f) : new Color(1, 1, 1, 0.06f), 6, 1, 8, 5));
            var text = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
            text.AddThemeConstantOverride("separation", 2);
            text.AddChild(UiKit.Label(L10n.T("ui.route.plan_title", i + 1, path.Score.ToString("0.0", CultureInfo.InvariantCulture)), UiTheme.FontSmall, UiTheme.Gold, wrap: false));
            text.AddChild(UiKit.Label(Summary(path.Counts), UiTheme.FontBody, UiTheme.Text));
            var first = path.Nodes.Count > 0 ? map.Find(path.Nodes[0]) : null;
            if (first != null) text.AddChild(UiKit.Muted(L10n.T("ui.route.first", L10n.T("ui.route.short." + first.Type))));
            var nodes = path.Nodes.ToList();
            var pin = UiKit.Button(L10n.T("ui.route.pin"), () => Pin(nodes, act, seed), isPinned ? UiTheme.ButtonKind.Primary : UiTheme.ButtonKind.Small);
            pin.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            pin.Disabled = isPinned;
            row.AddChild(UiKit.Row(text, pin));
            _plans.AddChild(row);
        }
    }

    private void ShowHint(string key)
    {
        UiKit.SetText(_plansHint, L10n.T(key));
        _plansHint.Visible = true;
    }

    private void UpdatePinned()
    {
        var route = Draft.Route;
        if (route.PinnedPath.Count == 0)
        {
            UiKit.SetText(_pinned, L10n.T("ui.route.pinned_none"));
            UiKit.SetColor(_pinned, UiTheme.Muted);
            _clearPin.Visible = false;
            return;
        }
        var snap = Snap;
        var view = CurrentMap(snap);
        bool inRun = snap is { InRun: true } && (snap.Run != null || view != null);
        string seed = view?.Seed ?? snap?.Run?.Seed ?? "";
        int act = view?.Act ?? snap?.Run?.Act ?? 0;
        // Another run: its map is not this one (every act map uses the same coordinate grid), so it never applies.
        bool otherRun = inRun && PinFromOtherRun(route, seed);
        bool otherAct = inRun && !otherRun && route.PinnedAct != act;
        string summary;
        if (view is { } v && !otherRun && !otherAct)
        {
            var counts = route.PinnedPath.Select(v.Map.Find).Where(n => n != null && n.Type != MapNodeType.Boss)
                .GroupBy(n => n!.Type).ToDictionary(g => g.Key, g => g.Count());
            summary = counts.Count > 0 ? Summary(counts) : L10n.T("ui.route.nodes", route.PinnedPath.Count);
        }
        else
        {
            summary = L10n.T("ui.route.nodes", route.PinnedPath.Count);
        }
        var text = L10n.T("ui.route.pinned", route.PinnedAct, summary);
        if (otherRun) text += " " + L10n.T("ui.route.pinned_other_run");
        else if (otherAct) text += " " + L10n.T("ui.route.pinned_stale");
        UiKit.SetText(_pinned, text);
        UiKit.SetColor(_pinned, otherRun ? UiTheme.Warn : otherAct ? UiTheme.Muted : UiTheme.Gold);
        UiKit.SetText(_clearPin, L10n.T(otherRun ? "ui.route.clear_stale" : "ui.clear"));
        _clearPin.Visible = true;
    }

    /// <summary>The nodes the party can travel to next, left to right, to pin one by hand.</summary>
    private void UpdateNextNodes()
    {
        var map = CurrentMap(Snap)?.Map;
        var next = map == null
            ? new List<MapNodeInfo>()
            : map.Reachable.Select(map.Find).OfType<MapNodeInfo>().OrderBy(n => n.Col).ToList();
        var key = map?.Current + "|" + string.Join(";", next.Select(n => n.Key + ":" + n.Type));
        if (key == _nextKey) return;
        _nextKey = key;
        var from = map?.Current is { } current ? map.Find(current) : null;
        _nextNodes.Clear();
        _nextNodes.AddRange(next.Select(n => n.Key));
        UiKit.SetItems(_nextNode, next.Count == 0
            ? new[] { (L10n.T("ui.route.no_next"), NoItemId) }
            : next.Select((n, i) => (NodeLabel(n, from), i)));
        _pinNext.Disabled = next.Count == 0;
    }

    /// <summary>"Elite · left (3,1)": the room type and where it lies from the current node (the map shows no coordinates).</summary>
    private static string NodeLabel(MapNodeInfo node, MapNodeInfo? from)
    {
        var label = L10n.T("ui.route.short." + node.Type);
        if (from != null)
            label += " · " + L10n.T(node.Col < from.Col ? "ui.route.dir.left" : node.Col > from.Col ? "ui.route.dir.right" : "ui.route.dir.ahead");
        return label + $" ({node.Key})";
    }

    private void PinNext()
    {
        int i = _nextNode.Selected >= 0 ? _nextNode.GetItemId(_nextNode.Selected) : -1;
        if (CurrentMap(Snap) is not { } view || i < 0 || i >= _nextNodes.Count) return;
        // Stored like a pinned route of one node: it is followed for this step, then the AI / planner choose again.
        Pin(new List<string> { _nextNodes[i] }, view.Act, view.Seed);
    }

    private void Pin(List<string> nodes, int act, string seed)
    {
        Edit(s =>
        {
            s.Route.PinnedPath = nodes.ToList();
            s.Route.PinnedAct = act;
            // The pin belongs to this run's map: the planner ignores it in any other run.
            s.Route.PinnedSeed = seed;
        }, now: true);
        RefreshSoon();
    }

    private void ClearPin()
    {
        Edit(s =>
        {
            s.Route.PinnedPath = new List<string>();
            s.Route.PinnedAct = 0;
            s.Route.PinnedSeed = "";
        }, now: true);
        RefreshSoon();
    }

    /// <summary>Rebuilds the path list on the next frame (the pressed button may live inside it).</summary>
    private void RefreshSoon()
    {
        _planKey = "";
        MainThread.Post(() => UiKit.Guard("route.refresh", () =>
        {
            if (!SceneUtil.Valid(_plans)) return;
            UpdatePlans();
            UpdatePinned();
        }));
    }

    /// <summary>"精英2 篝火2 商店1 问号3 宝箱1 怪物5".</summary>
    private static string Summary(Dictionary<MapNodeType, int> counts)
    {
        var parts = SummaryOrder.Where(t => counts.TryGetValue(t, out var c) && c > 0)
            .Select(t => L10n.T("ui.route.short." + t) + counts[t]);
        var s = string.Join(" ", parts);
        return s.Length > 0 ? s : "—";
    }

    private static void Clear(Node node)
    {
        foreach (var child in node.GetChildren())
        {
            node.RemoveChild(child);
            child.QueueFree();
        }
    }
}
