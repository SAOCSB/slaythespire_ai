using System.Globalization;
using Godot;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Route;
using SpirePilot.Core.Text;
using SpirePilot.Infra;

namespace SpirePilot.Ui.Pages;

/// <summary>Tab 2: route preset / custom weights / instructions, the planner's best paths and the pinned path.</summary>
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
    private VBoxContainer _plans = null!;
    private Label _pinned = null!;
    private Button _clearPin = null!;
    private string _planKey = "";

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

        var plans = UiKit.Card(Body, L10n.T("ui.route.plans"));
        _plansHint = UiKit.Muted(L10n.T("ui.route.no_map"));
        plans.AddChild(_plansHint);
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

    private void UpdatePlans()
    {
        var snap = Snap;
        var map = snap?.Map;
        if (snap == null || map == null || map.Nodes.Count == 0 || map.Reachable.Count == 0)
        {
            _plansHint.Visible = true;
            if (_plans.GetChildCount() > 0) Clear(_plans);
            _planKey = "";
            return;
        }
        var me = snap.LocalPlayer;
        double hp = me != null && me.MaxHp > 0 ? (double)me.Hp / me.MaxHp : 1.0;
        int gold = me?.Gold ?? 0;
        var route = Draft.Route;
        int act = snap.Run?.Act ?? 0;
        string key = string.Join(";", map.Reachable) + "|" + map.Current + "|" + map.Nodes.Count + "|" + route.Preset + "|" +
                     string.Join(",", route.CustomWeights.OrderBy(k => k.Key).Select(k => k.Key + "=" + k.Value.ToString(CultureInfo.InvariantCulture))) +
                     "|" + Math.Round(hp, 2) + "|" + gold / 25 + "|" + act + "|" + string.Join(",", route.PinnedPath) + route.PinnedAct;
        if (key == _planKey) return;
        _planKey = key;

        var plans = RoutePlanner.Plan(map, route, hp, gold, 5);
        Clear(_plans);
        _plansHint.Visible = plans.Count == 0;
        bool pinnedHere = route.PinnedAct == act && route.PinnedPath.Count > 0;
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
            var pin = UiKit.Button(L10n.T("ui.route.pin"), () => Pin(nodes, act), isPinned ? UiTheme.ButtonKind.Primary : UiTheme.ButtonKind.Small);
            pin.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            pin.Disabled = isPinned;
            row.AddChild(UiKit.Row(text, pin));
            _plans.AddChild(row);
        }
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
        int act = snap?.Run?.Act ?? 0;
        string summary;
        var map = snap?.Map;
        if (map != null && route.PinnedAct == act)
        {
            var counts = route.PinnedPath.Select(map.Find).Where(n => n != null && n.Type != MapNodeType.Boss)
                .GroupBy(n => n!.Type).ToDictionary(g => g.Key, g => g.Count());
            summary = counts.Count > 0 ? Summary(counts) : L10n.T("ui.route.nodes", route.PinnedPath.Count);
        }
        else
        {
            summary = L10n.T("ui.route.nodes", route.PinnedPath.Count);
        }
        var text = L10n.T("ui.route.pinned", route.PinnedAct, summary);
        bool stale = snap?.Run != null && route.PinnedAct != act;
        if (stale) text += " " + L10n.T("ui.route.pinned_stale");
        UiKit.SetText(_pinned, text);
        UiKit.SetColor(_pinned, stale ? UiTheme.Muted : UiTheme.Gold);
        _clearPin.Visible = true;
    }

    private void Pin(List<string> nodes, int act)
    {
        Edit(s =>
        {
            s.Route.PinnedPath = nodes.ToList();
            s.Route.PinnedAct = act;
        }, now: true);
        RefreshSoon();
    }

    private void ClearPin()
    {
        Edit(s =>
        {
            s.Route.PinnedPath = new List<string>();
            s.Route.PinnedAct = 0;
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
