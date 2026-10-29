using System.Globalization;
using Godot;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Text;
using SpirePilot.Infra;

namespace SpirePilot.Ui.Pages;

/// <summary>Tab 6: pilot activity log with filter, log file location, diagnostics, and the remaining settings.</summary>
internal sealed class LogPage : Page
{
    private static readonly PilotEventKind[] Kinds =
        { PilotEventKind.Decision, PilotEventKind.Action, PilotEventKind.TeamMessage, PilotEventKind.Info, PilotEventKind.Error };

    /// <summary>Id of the "All" filter item. Not -1: Godot replaces a -1 id with the item's index (0 = Decision).</summary>
    private const int FilterAll = 100;

    private static readonly string[] ToggleKeys =
        { "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Insert", "Home", "End", "Pageup", "Pagedown", "Pause", "Scrolllock" };

    private OptionButton _filter = null!;
    private LineEdit _search = null!;
    private RichTextLabel _log = null!;
    private long _renderedSeq;
    private int _renderedTrim = -1;
    private int _renderedCount;
    private int _filterId = -1;
    private string _searchText = "";

    private Label _copied = null!;
    private CheckButton _traces = null!;
    private Label _diagResult = null!;
    private Button _diagCopy = null!, _diagOpen = null!;
    private string? _diagPath;
    private bool _diagWriting;
    private OptionButton _language = null!;
    private OptionButton _toggleKey = null!;
    private string _customKey = "";
    private CheckButton _showOnStart = null!;
    private HSlider _scale = null!;
    private Label _scaleValue = null!;
    private CheckButton _httpEnabled = null!;
    private SpinBox _httpPort = null!;
    private LineEdit _httpToken = null!;
    private Label _httpStatus = null!;
    private CheckButton _logPrompts = null!;

    public override string TitleKey => "ui.tab.log";
    public override ulong SnapshotIntervalMs => 0;

    protected override void BuildContent()
    {
        var events = UiKit.Card(Body, L10n.T("ui.log.events"));
        _filter = UiKit.Options(id =>
        {
            _filterId = id == FilterAll ? -1 : id;
            RenderAll();
        }, "log.filter");
        var items = new List<(string, int)> { (L10n.T("ui.log.filter.all"), FilterAll) };
        items.AddRange(Kinds.Select(k => (L10n.T("ui.log.filter." + k), (int)k)));
        UiKit.SetItems(_filter, items);
        _search = UiKit.Line(L10n.T("ui.log.search_ph"), text =>
        {
            _searchText = text.Trim();
            RenderAll();
        }, where: "log.search");
        _search.ClearButtonEnabled = true;
        var clear = UiKit.Button(L10n.T("ui.clear"), () =>
        {
            Feeds.ClearLog();
            RenderAll();
        }, UiTheme.ButtonKind.Small);
        events.AddChild(UiKit.Row(_filter, _search, clear));
        _log = UiKit.Rich(300, follow: true);
        events.AddChild(_log);

        var file = UiKit.Card(Body, L10n.T("ui.log.file"));
        var path = ModLog.FilePath ?? "—";
        var pathLabel = UiKit.Label(path, UiTheme.FontSmall, UiTheme.Text);
        file.AddChild(pathLabel);
        _copied = UiKit.Muted("");
        file.AddChild(UiKit.Row(
            UiKit.Button(L10n.T("ui.log.copy"), () =>
            {
                DisplayServer.ClipboardSet(path);
                UiKit.SetText(_copied, L10n.T("ui.log.copied"));
            }, UiTheme.ButtonKind.Small),
            UiKit.Button(L10n.T("ui.log.open"), () => OpenFolderOf(ModLog.FilePath), UiTheme.ButtonKind.Small),
            _copied));

        // ---- diagnostics: decision traces and a report for bug reports ----
        var diag = UiKit.Card(Body, L10n.T("ui.log.diag"));
        _traces = UiKit.Switch(L10n.T("ui.log.traces"), on => Edit(s => s.SaveDecisionTraces = on), L10n.T("ui.log.traces_tip"));
        diag.AddChild(_traces);
        diag.AddChild(UiKit.Muted(L10n.T("ui.log.traces_hint", Path.Combine(PilotRuntime.DataDir, "traces"))));
        diag.AddChild(UiKit.Row(UiKit.Button(L10n.T("ui.log.diag_write"), WriteDiagnostics, UiTheme.ButtonKind.Normal, L10n.T("ui.log.diag_tip"))));
        _diagResult = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text);
        _diagResult.Visible = false;
        diag.AddChild(_diagResult);
        _diagCopy = UiKit.Button(L10n.T("ui.log.copy"), () =>
        {
            if (_diagPath == null) return;
            DisplayServer.ClipboardSet(_diagPath);
            UiKit.SetText(_diagCopy, L10n.T("ui.log.copied"));
        }, UiTheme.ButtonKind.Small);
        _diagOpen = UiKit.Button(L10n.T("ui.log.open"), () => OpenFolderOf(_diagPath), UiTheme.ButtonKind.Small);
        _diagCopy.Visible = _diagOpen.Visible = false;
        diag.AddChild(UiKit.Row(_diagCopy, _diagOpen));

        // ---- other settings ----
        var misc = UiKit.Card(Body, L10n.T("ui.misc"));
        _language = UiKit.Options(id => Edit(s => s.Ui.Language = id switch { 1 => "zh", 2 => "en", _ => "auto" }, now: true), "misc.language");
        UiKit.SetItems(_language, new[] { (L10n.T("ui.lang.auto"), 0), (L10n.T("ui.lang.zh"), 1), (L10n.T("ui.lang.en"), 2) });
        UiKit.Field(misc, L10n.T("ui.misc.language"), _language);
        _toggleKey = UiKit.Options(OnToggleKey, "misc.key");
        UiKit.Field(misc, L10n.T("ui.misc.toggle_key"), _toggleKey);
        _showOnStart = UiKit.Switch(L10n.T("ui.misc.show_on_start"), on => Edit(s => s.Ui.ShowOnStart = on));
        misc.AddChild(_showOnStart);
        _scaleValue = UiKit.Label("", UiTheme.FontSmall, UiTheme.Text, wrap: false);
        _scaleValue.CustomMinimumSize = new Vector2(52, 0);
        _scaleValue.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        _scaleValue.HorizontalAlignment = HorizontalAlignment.Right;
        _scale = UiKit.Slider(0.6, 2.0, 0.05, v =>
        {
            UiKit.SetText(_scaleValue, FormatScale(v));
            Edit(s => s.Ui.Scale = (float)v);
            Overlay.ApplyScale((float)v);
        }, "misc.scale");
        UiKit.Field(misc, L10n.T("ui.misc.scale"), UiKit.Row(_scale, _scaleValue));
        _logPrompts = UiKit.Switch(L10n.T("ui.misc.log_prompts"), on => Edit(s => s.LogPrompts = on));
        misc.AddChild(_logPrompts);

        var http = UiKit.Card(Body, L10n.T("ui.misc.http"));
        _httpEnabled = UiKit.Switch(L10n.T("ui.misc.http_enable"), on => Edit(s => s.HttpApi.Enabled = on, now: true));
        http.AddChild(_httpEnabled);
        _httpPort = UiKit.Spin(1024, 65535, 1, v => Edit(s => s.HttpApi.Port = (int)v), "http.port");
        UiKit.Field(http, L10n.T("ui.misc.http_port"), _httpPort);
        _httpToken = UiKit.Line("", text => Edit(s => s.HttpApi.Token = text.Trim()), where: "http.token");
        _httpToken.Secret = true;
        UiKit.Field(http, L10n.T("ui.misc.http_token"), _httpToken);
        _httpStatus = UiKit.Muted("");
        http.AddChild(_httpStatus);

        var about = UiKit.Card(Body, null);
        about.AddChild(UiKit.Muted(L10n.T("ui.misc.version", PilotRuntime.Version)));
        about.AddChild(UiKit.Muted(L10n.T("ui.misc.settings_file", PilotRuntime.Store?.Path ?? "—")));

        RenderAll();
    }

    private static string FormatScale(double v) => ((int)Math.Round(v * 100)).ToString(CultureInfo.InvariantCulture) + "%";

    public override void Sync(PilotSettings s)
    {
        var lang = (s.Ui.Language ?? "auto").Trim().ToLowerInvariant();
        UiKit.SelectId(_language, lang == "zh" ? 1 : lang == "en" ? 2 : 0);

        var key = (s.Ui.ToggleKey ?? "F8").Trim();
        int idx = Array.FindIndex(ToggleKeys, k => k.Equals(key, StringComparison.OrdinalIgnoreCase));
        _customKey = idx < 0 ? key : "";
        var items = ToggleKeys.Select((k, i) => (k, i)).ToList();
        if (idx < 0)
        {
            items.Add((key, ToggleKeys.Length));
            idx = ToggleKeys.Length;
        }
        UiKit.SetItems(_toggleKey, items);
        _toggleKey.Select(idx);

        UiKit.SetPressed(_showOnStart, s.Ui.ShowOnStart);
        UiKit.SetValue(_scale, s.Ui.Scale);
        UiKit.SetText(_scaleValue, FormatScale(s.Ui.Scale));
        UiKit.SetPressed(_logPrompts, s.LogPrompts);
        UiKit.SetPressed(_traces, s.SaveDecisionTraces);
        UiKit.SetPressed(_httpEnabled, s.HttpApi.Enabled);
        UiKit.SetValue(_httpPort, s.HttpApi.Port);
        UiKit.SetText(_httpToken, s.HttpApi.Token);
    }

    private void OnToggleKey(int id)
    {
        var name = id >= 0 && id < ToggleKeys.Length ? ToggleKeys[id] : _customKey;
        if (string.IsNullOrEmpty(name)) return;
        Edit(s => s.Ui.ToggleKey = name, now: true);
    }

    /// <summary>Writes the diagnostics report (the log and the file are handled off the main thread) and shows where.</summary>
    private void WriteDiagnostics()
    {
        if (_diagWriting) return;
        _diagWriting = true;
        _diagResult.Visible = true;
        UiKit.SetText(_diagResult, L10n.T("ui.log.diag_writing"));
        UiKit.SetColor(_diagResult, UiTheme.Muted);
        _diagCopy.Visible = _diagOpen.Visible = false;
        PilotRuntime.WriteDiagnosticsAsync().ContinueWith(t => MainThread.Post(() => UiKit.Guard("log.diag", () =>
        {
            _diagWriting = false;
            // The page may have been rebuilt meanwhile (language change): its old controls are gone.
            if (!SceneUtil.Valid(_diagResult)) return;
            var path = t.Result;
            _diagPath = path;
            UiKit.SetText(_diagResult, path != null ? L10n.T("ui.log.diag_written", path) : L10n.T("ui.log.diag_failed"));
            UiKit.SetColor(_diagResult, path != null ? UiTheme.Good : UiTheme.Bad);
            UiKit.SetText(_diagCopy, L10n.T("ui.log.copy"));
            _diagCopy.Visible = _diagOpen.Visible = path != null;
        })), TaskScheduler.Default);
    }

    private static void OpenFolderOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return;
        var err = OS.ShellOpen(dir);
        if (err != Error.Ok) ModLog.Warn("UI: could not open " + dir + ": " + err);
    }

    public override void Pump()
    {
        if (_renderedTrim != Feeds.LogTrimVersion)
        {
            RenderAll();
            return;
        }
        var log = Feeds.Log;
        if (log.Count == 0 || log[^1].Seq <= _renderedSeq) return;
        // Append only what is new.
        int start = log.Count;
        while (start > 0 && log[start - 1].Seq > _renderedSeq) start--;
        for (int i = start; i < log.Count; i++) Append(log[i]);
        _renderedSeq = log[^1].Seq;
    }

    public override void Refresh(bool slow)
    {
        if (!slow) return;
        var api = PilotRuntime.HttpApi;
        UiKit.SetText(_httpStatus, api != null ? L10n.T("ui.misc.http_running", api.Url) : L10n.T("ui.misc.http_stopped"));
        UiKit.SetColor(_httpStatus, api != null ? UiTheme.Good : UiTheme.Muted);
    }

    private void RenderAll()
    {
        _log.Clear();
        _renderedCount = 0;
        _renderedTrim = Feeds.LogTrimVersion;
        foreach (var item in Feeds.Log) Append(item);
        _renderedSeq = Feeds.Log.Count > 0 ? Feeds.Log[^1].Seq : 0;
        if (_renderedCount == 0) _log.AppendText(UiKit.Colored(L10n.T("ui.log.empty"), UiTheme.Faint));
    }

    private void Append(Feeds.LogItem item)
    {
        if (_filterId >= 0 && (int)item.Kind != _filterId) return;
        if (_searchText.Length > 0 && !item.Text.Contains(_searchText, StringComparison.OrdinalIgnoreCase)) return;
        if (_renderedCount == 0) _log.Clear();
        else _log.AppendText("\n");
        _renderedCount++;
        var color = item.Kind switch
        {
            PilotEventKind.Decision => UiTheme.Info,
            PilotEventKind.Action => item.Text.StartsWith("✘", StringComparison.Ordinal) ? UiTheme.Bad : UiTheme.Good,
            PilotEventKind.TeamMessage => UiTheme.Gold,
            PilotEventKind.Error => UiTheme.Bad,
            _ => UiTheme.Muted,
        };
        _log.AppendText($"{UiKit.Colored(item.Time.ToString("HH:mm:ss"), UiTheme.Faint)} " +
                        $"{UiKit.Colored(L10n.T("ui.log.kind." + item.Kind), color)} " +
                        $"{UiKit.Colored(item.Text, item.Kind == PilotEventKind.Decision ? UiTheme.Text : color)}");
    }
}
