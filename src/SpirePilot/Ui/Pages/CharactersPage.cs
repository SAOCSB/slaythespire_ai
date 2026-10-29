using Godot;
using SpirePilot.Core.Config;
using SpirePilot.Core.Text;

namespace SpirePilot.Ui.Pages;

/// <summary>Tab 3: allowed characters, pick policy, ascension, play-style instructions and prompt content.</summary>
internal sealed class CharactersPage : Page
{
    private static readonly string[] DefaultIds = { "IRONCLAD", "SILENT", "REGENT", "NECROBINDER", "DEFECT" };
    private static readonly CharacterPickPolicy[] Policies =
        { CharacterPickPolicy.Random, CharacterPickPolicy.RoundRobin, CharacterPickPolicy.First, CharacterPickPolicy.AiChoice };

    private sealed record CharEntry(string Id, string Name, bool Locked);

    private VBoxContainer _charList = null!;
    private Label _charHint = null!;
    private readonly Dictionary<string, CheckButton> _charSwitches = new(StringComparer.OrdinalIgnoreCase);
    private List<CharEntry> _chars = new();
    private string _charKey = "";
    private OptionButton _policy = null!;
    private SpinBox _ascension = null!;
    private TextEdit _style = null!;
    private CheckButton _verbose = null!, _piles = null!;
    private SpinBox _history = null!;

    public override string TitleKey => "ui.tab.chars";

    protected override void BuildContent()
    {
        var chars = UiKit.Card(Body, L10n.T("ui.chars.allowed"), L10n.T("ui.chars.allowed_hint"));
        _charList = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        chars.AddChild(_charList);
        _charHint = UiKit.Label("", UiTheme.FontSmall, UiTheme.Warn);
        _charHint.Visible = false;
        chars.AddChild(_charHint);

        _policy = UiKit.Options(id => Edit(s => s.Characters.Policy = (CharacterPickPolicy)id), "chars.policy");
        UiKit.SetItems(_policy, Policies.Select(p => (L10n.T("ui.chars.policy." + p), (int)p)));
        UiKit.Field(chars, L10n.T("ui.chars.policy"), _policy);
        _ascension = UiKit.Spin(-1, 20, 1, v => Edit(s => s.Characters.Ascension = (int)v), "chars.ascension");
        UiKit.Field(chars, L10n.T("ui.chars.ascension"), _ascension, L10n.T("ui.chars.ascension_hint"));
        chars.AddChild(UiKit.Muted(L10n.T("ui.chars.ascension_hint")));

        var style = UiKit.Card(Body, L10n.T("ui.chars.style"));
        _style = UiKit.Area(L10n.T("ui.chars.style_ph"), text => Edit(s => s.Strategy.Instruction = text), 96, "chars.style");
        style.AddChild(_style);

        var prompt = UiKit.Card(Body, L10n.T("ui.chars.prompt"));
        _verbose = UiKit.Switch(L10n.T("ui.chars.verbose"), on => Edit(s => s.Strategy.VerboseCards = on));
        _piles = UiKit.Switch(L10n.T("ui.chars.piles"), on => Edit(s => s.Strategy.IncludePiles = on));
        prompt.AddChild(_verbose);
        prompt.AddChild(_piles);
        _history = UiKit.Spin(0, 50, 1, v => Edit(s => s.Strategy.HistoryLength = (int)v), "chars.history");
        UiKit.Field(prompt, L10n.T("ui.chars.history"), _history);
    }

    public override void Sync(PilotSettings s)
    {
        RebuildCharacters(s);
        ShowAllowed(s);
        UiKit.SelectId(_policy, (int)s.Characters.Policy);
        UiKit.SetValue(_ascension, s.Characters.Ascension);
        UiKit.SetText(_style, s.Strategy.Instruction);
        UiKit.SetPressed(_verbose, s.Strategy.VerboseCards);
        UiKit.SetPressed(_piles, s.Strategy.IncludePiles);
        UiKit.SetValue(_history, s.Strategy.HistoryLength);
    }

    public override void Refresh(bool slow)
    {
        if (!slow) return;
        if (RebuildCharacters(Draft)) ShowAllowed(Draft);
    }

    /// <summary>Known characters: from the character-select screen when it was read, else the base game's five.</summary>
    private List<CharEntry> CurrentCharacters(PilotSettings s)
    {
        var list = new List<CharEntry>();
        var cs = Snap?.CharacterSelect;
        if (cs != null)
            foreach (var c in cs.Characters)
                if (!string.IsNullOrEmpty(c.Id) && list.All(x => !x.Id.Equals(c.Id, StringComparison.OrdinalIgnoreCase)))
                    list.Add(new CharEntry(c.Id, string.IsNullOrEmpty(c.Name) ? DefaultName(c.Id) : c.Name, c.Locked));
        foreach (var id in DefaultIds)
            if (list.All(x => !x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                list.Add(new CharEntry(id, DefaultName(id), false));
        foreach (var id in s.Characters.Allowed)
            if (list.All(x => !x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                list.Add(new CharEntry(id, DefaultName(id), false));
        return list;
    }

    private static string DefaultName(string id)
    {
        var key = "ui.char." + id.ToUpperInvariant();
        return L10n.Has(key) ? L10n.T(key) : id;
    }

    /// <summary>Recreates the switches when the character list changed; returns true if it did.</summary>
    private bool RebuildCharacters(PilotSettings s)
    {
        var chars = CurrentCharacters(s);
        var key = string.Join("|", chars.Select(c => c.Id + ":" + c.Name + ":" + c.Locked));
        if (key == _charKey) return false;
        _charKey = key;
        _chars = chars;
        foreach (var child in _charList.GetChildren())
        {
            _charList.RemoveChild(child);
            child.QueueFree();
        }
        _charSwitches.Clear();
        foreach (var c in chars)
        {
            var id = c.Id;
            var text = c.Locked ? $"{c.Name} {L10n.T("ui.chars.locked")}" : c.Name;
            var sw = UiKit.Switch(text, on => OnToggle(id, on));
            sw.TooltipText = id;
            _charSwitches[id] = sw;
            _charList.AddChild(sw);
        }
        return true;
    }

    private void ShowAllowed(PilotSettings s)
    {
        var allowed = s.Characters.Allowed;
        foreach (var (id, sw) in _charSwitches)
            UiKit.SetPressed(sw, allowed.Count == 0 || allowed.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    private void OnToggle(string id, bool on)
    {
        var all = _chars.Select(c => c.Id).ToList();
        var current = Draft.Characters.Allowed.Count == 0 ? all.ToList() : Draft.Characters.Allowed.ToList();
        if (!on && current.Count(x => !x.Equals(id, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            // An empty allow-list means "any", so the last character cannot be unchecked.
            if (_charSwitches.TryGetValue(id, out var sw)) UiKit.SetPressed(sw, true);
            UiKit.SetText(_charHint, L10n.T("ui.chars.keep_one"));
            _charHint.Visible = true;
            return;
        }
        _charHint.Visible = false;
        Edit(s =>
        {
            var allowed = s.Characters.Allowed.Count == 0 ? all.ToList() : s.Characters.Allowed.ToList();
            allowed.RemoveAll(x => x.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (on) allowed.Add(id);
            // Everything checked = no restriction (also covers characters added by future updates).
            if (all.All(a => allowed.Contains(a, StringComparer.OrdinalIgnoreCase))) allowed.Clear();
            s.Characters.Allowed = allowed;
        });
    }
}
