using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Godot;
using SpirePilot.Core.Config;
using SpirePilot.Core.Llm;
using SpirePilot.Core.Text;
using SpirePilot.Infra;

namespace SpirePilot.Ui.Pages;

/// <summary>
/// Tab 5: LLM profiles. Unlike the other tabs, the profile editor works on its own copy and is applied with
/// "Save" (a half-typed URL or key must never reach the running pilot). Profile list operations save immediately.
/// </summary>
internal sealed class ApiPage : Page
{
    private static readonly string[] Efforts = { "", "none", "minimal", "low", "medium", "high", "xhigh", "max" };

    private OptionButton _active = null!;
    private List<string> _profileNames = new();
    private OptionButton _preset = null!;
    private Button _delete = null!;
    private ulong _deleteArmedUntil;
    private LineEdit _rename = null!;
    private Label _listHint = null!;

    private Label _editorTitle = null!;
    private OptionButton _provider = null!;
    private LineEdit _baseUrl = null!, _apiKey = null!, _model = null!, _temperature = null!, _proxy = null!, _anthropicVersion = null!;
    private Button _showKey = null!;
    private SpinBox _maxTokens = null!, _timeout = null!;
    private OptionButton _effort = null!, _combatEffort = null!;
    private CheckButton _jsonMode = null!, _serverFallback = null!, _promptCaching = null!;
    private TextEdit _headers = null!, _body = null!;
    private Control _anthropicCard = null!;
    private Label _validation = null!;
    private Label _dirtyLabel = null!;
    private Button _test = null!;
    private Label _testResult = null!;
    private bool _testing;

    /// <summary>Name of the profile in the editor (as saved).</summary>
    private string _editName = "";
    private bool _dirty;
    private bool _loading;
    private string _effortCustom = "", _combatEffortCustom = "";

    public override string TitleKey => "ui.tab.api";
    public override bool WantsSnapshot => false;

    protected override void BuildContent()
    {
        // ---- profile list ----
        var list = UiKit.Card(Body, L10n.T("ui.api.profiles"));
        _active = UiKit.Options(OnSelectProfile, "api.active");
        UiKit.Field(list, L10n.T("ui.api.active"), _active);
        _preset = UiKit.Options(_ => { }, "api.preset");
        UiKit.SetItems(_preset, LlmPresets.All.Select((p, i) => (p.DisplayName, i)));
        UiKit.Field(list, L10n.T("ui.api.add_preset"), UiKit.Row(_preset, UiKit.Button(L10n.T("ui.api.add"), AddFromPreset, UiTheme.ButtonKind.Small)));
        _rename = UiKit.Line("", onSubmit: _ => Rename(), where: "api.rename");
        _rename.MaxLength = 40;
        UiKit.Field(list, L10n.T("ui.api.rename"), UiKit.Row(_rename, UiKit.Button(L10n.T("ui.api.rename"), Rename, UiTheme.ButtonKind.Small)));
        _delete = UiKit.Button(L10n.T("ui.api.delete"), Delete, UiTheme.ButtonKind.Danger);
        list.AddChild(UiKit.Row(UiKit.Button(L10n.T("ui.api.duplicate"), Duplicate, UiTheme.ButtonKind.Small), _delete));
        _listHint = UiKit.Label("", UiTheme.FontSmall, UiTheme.Warn);
        _listHint.Visible = false;
        list.AddChild(_listHint);

        // ---- editor ----
        var ed = UiKit.Card(Body, null);
        _editorTitle = UiKit.Heading("");
        ed.AddChild(_editorTitle);
        _provider = UiKit.Options(OnProviderChanged, "api.provider");
        UiKit.SetItems(_provider, new[]
        {
            (L10n.T("ui.api.provider.OpenAiCompatible"), (int)LlmProvider.OpenAiCompatible),
            (L10n.T("ui.api.provider.Anthropic"), (int)LlmProvider.Anthropic),
        });
        UiKit.Field(ed, L10n.T("ui.api.provider"), _provider);
        _baseUrl = Text(ed, "ui.api.base_url", "https://api.openai.com/v1");
        _apiKey = UiKit.Line(L10n.T("ui.api.api_key_ph"), _ => OnFieldChanged(), where: "api.key");
        _apiKey.Secret = true;
        _showKey = UiKit.Button(L10n.T("ui.api.show_key"), ToggleKeyVisible, UiTheme.ButtonKind.Small);
        UiKit.Field(ed, L10n.T("ui.api.api_key"), UiKit.Row(_apiKey, _showKey));
        _model = Text(ed, "ui.api.model", "claude-opus-5-5 / gpt-5 / deepseek-chat …");
        _maxTokens = UiKit.Spin(256, 128000, 1, _ => OnFieldChanged(), "api.max_tokens", 256);
        UiKit.Field(ed, L10n.T("ui.api.max_tokens"), _maxTokens);
        _timeout = UiKit.Spin(10, 900, 1, _ => OnFieldChanged(), "api.timeout", 10);
        UiKit.Field(ed, L10n.T("ui.api.timeout"), _timeout);
        _temperature = Text(ed, "ui.api.temperature", L10n.T("ui.api.temperature_ph"));
        _effort = UiKit.Options(_ => OnFieldChanged(), "api.effort");
        UiKit.Field(ed, L10n.T("ui.api.effort"), _effort);
        _combatEffort = UiKit.Options(_ => OnFieldChanged(), "api.combat_effort");
        UiKit.Field(ed, L10n.T("ui.api.combat_effort"), _combatEffort);
        _jsonMode = UiKit.Switch(L10n.T("ui.api.json_mode"), _ => OnFieldChanged());
        ed.AddChild(_jsonMode);
        _proxy = Text(ed, "ui.api.proxy", L10n.T("ui.api.proxy_ph"));
        ed.AddChild(UiKit.Muted(L10n.T("ui.api.headers")));
        _headers = UiKit.Area("X-Title: SpirePilot", _ => OnFieldChanged(), 48, "api.headers");
        ed.AddChild(_headers);
        ed.AddChild(UiKit.Muted(L10n.T("ui.api.body")));
        _body = UiKit.Area("{\"top_p\": 0.9}", _ => OnFieldChanged(), 48, "api.body");
        ed.AddChild(_body);

        var anth = UiKit.Card(ed, L10n.T("ui.api.anthropic"));
        _anthropicCard = anth.GetParent<Control>();
        _anthropicVersion = Text(anth, "ui.api.anthropic_version", "2026-10-21");
        _serverFallback = UiKit.Switch(L10n.T("ui.api.server_fallback"), _ => OnFieldChanged());
        _promptCaching = UiKit.Switch(L10n.T("ui.api.prompt_caching"), _ => OnFieldChanged());
        anth.AddChild(_serverFallback);
        anth.AddChild(_promptCaching);

        _validation = UiKit.Label("", UiTheme.FontSmall, UiTheme.Good);
        ed.AddChild(_validation);
        _test = UiKit.Button(L10n.T("ui.api.test"), Test);
        var save = UiKit.Button(L10n.T("ui.save"), Save, UiTheme.ButtonKind.Primary);
        var revert = UiKit.Button(L10n.T("ui.api.revert"), () => Load(_editName), UiTheme.ButtonKind.Small);
        _dirtyLabel = UiKit.Label("", UiTheme.FontSmall, UiTheme.Warn, wrap: false);
        ed.AddChild(UiKit.Row(_test, save, revert, _dirtyLabel));
        _testResult = UiKit.Label("", UiTheme.FontSmall, UiTheme.Muted);
        ed.AddChild(_testResult);
        ed.AddChild(UiKit.Muted(L10n.T("ui.api.key_note")));
    }

    private LineEdit Text(VBoxContainer parent, string labelKey, string placeholder)
    {
        var e = UiKit.Line(placeholder, _ => OnFieldChanged(), where: labelKey);
        UiKit.Field(parent, L10n.T(labelKey), e);
        return e;
    }

    // ---------------------------------------------------------------- sync / load

    public override void Sync(PilotSettings s)
    {
        var names = s.Profiles.Select(p => p.Name).ToList();
        if (!names.SequenceEqual(_profileNames))
        {
            _profileNames = names;
            UiKit.SetItems(_active, names.Select((n, i) => (n, i)));
        }
        int active = names.IndexOf(s.ActiveProfile);
        if (active >= 0 && _active.Selected != active) _active.Select(active);

        // The editor always shows the active profile; unsaved edits are kept until saved or reverted.
        if (_dirty && names.Contains(_editName)) return;
        Load(s.Active?.Name ?? names.FirstOrDefault() ?? "");
    }

    private void Load(string name)
    {
        var p = Draft.Profiles.FirstOrDefault(x => x.Name == name) ?? Draft.Active;
        if (p == null) return;
        bool switched = p.Name != _editName;
        _loading = true;
        try
        {
            _editName = p.Name;
            UiKit.SetText(_editorTitle, L10n.T("ui.api.editor", p.Name));
            Put(_rename, p.Name);
            UiKit.SelectId(_provider, (int)p.Provider);
            Put(_baseUrl, p.BaseUrl);
            Put(_apiKey, p.ApiKey);
            Put(_model, p.Model);
            _maxTokens.SetValueNoSignal(p.MaxTokens);
            _timeout.SetValueNoSignal(p.TimeoutSeconds);
            Put(_temperature, p.Temperature?.ToString("0.###", CultureInfo.InvariantCulture) ?? "");
            _effortCustom = FillEffort(_effort, p.ReasoningEffort);
            _combatEffortCustom = FillEffort(_combatEffort, p.CombatReasoningEffort);
            _jsonMode.SetPressedNoSignal(p.JsonMode);
            Put(_proxy, p.ProxyUrl);
            Put(_headers, string.Join("\n", p.ExtraHeaders.Select(kv => kv.Key + ": " + kv.Value)));
            Put(_body, p.ExtraBodyJson);
            Put(_anthropicVersion, p.AnthropicVersion);
            _serverFallback.SetPressedNoSignal(p.AnthropicServerFallback);
            _promptCaching.SetPressedNoSignal(p.PromptCaching);
        }
        finally
        {
            _loading = false;
        }
        _dirty = false;
        if (switched) UiKit.SetText(_testResult, "");
        UpdateState();
    }

    private static void Put(LineEdit e, string text)
    {
        if (e.Text != text) e.Text = text;
    }

    private static void Put(TextEdit e, string text)
    {
        if (e.Text != text) e.Text = text;
    }

    /// <summary>Fills an effort drop-down; returns the custom value when it is not one of the known ones.</summary>
    private static string FillEffort(OptionButton o, string value)
    {
        value = (value ?? "").Trim();
        var items = Efforts.Select((e, i) => (e.Length == 0 ? L10n.T("ui.api.effort_default") : e, i)).ToList();
        int index = Array.IndexOf(Efforts, value.ToLowerInvariant());
        string custom = "";
        if (index < 0)
        {
            custom = value;
            items.Add((value, Efforts.Length));
            index = Efforts.Length;
        }
        UiKit.SetItems(o, items);
        o.Select(index);
        return custom;
    }

    private static string EffortOf(OptionButton o, string custom)
    {
        int id = o.Selected >= 0 ? o.GetItemId(o.Selected) : 0;
        return id >= 0 && id < Efforts.Length ? Efforts[id] : custom;
    }

    // ---------------------------------------------------------------- editing

    /// <summary>Switching protocol also swaps an untouched default base URL for the other protocol's default.</summary>
    private void OnProviderChanged(int id)
    {
        const string openAi = "https://api.openai.com/v1", anthropic = "https://api.anthropic.com";
        var url = _baseUrl.Text.Trim().TrimEnd('/');
        if (id == (int)LlmProvider.Anthropic && (url.Length == 0 || url == openAi)) _baseUrl.Text = anthropic;
        else if (id == (int)LlmProvider.OpenAiCompatible && (url.Length == 0 || url == anthropic)) _baseUrl.Text = openAi;
        OnFieldChanged();
    }

    private void OnFieldChanged()
    {
        if (_loading) return;
        _dirty = true;
        UpdateState();
    }

    private void UpdateState()
    {
        var anthropic = _provider.Selected >= 0 && _provider.GetItemId(_provider.Selected) == (int)LlmProvider.Anthropic;
        _anthropicCard.Visible = anthropic;
        _baseUrl.PlaceholderText = anthropic ? "https://api.anthropic.com" : "https://api.openai.com/v1";
        UiKit.SetText(_dirtyLabel, _dirty ? L10n.T("ui.api.dirty") : "");

        var profile = BuildProfile(out var errors);
        var problems = new List<string>(errors);
        try
        {
            foreach (var code in LlmClientFactory.Validate(profile))
                problems.Add(L10n.Has("ui.api.invalid." + code) ? L10n.T("ui.api.invalid." + code) : code);
        }
        catch (Exception ex) { problems.Add(ex.Message); }
        if (problems.Count == 0)
        {
            UiKit.SetText(_validation, L10n.T("ui.api.ok"));
            UiKit.SetColor(_validation, UiTheme.Good);
        }
        else
        {
            UiKit.SetText(_validation, string.Join("\n", problems.Select(p => "• " + p)));
            UiKit.SetColor(_validation, UiTheme.Warn);
        }
        _test.Disabled = _testing;
    }

    /// <summary>Profile from the editor fields; <paramref name="errors"/> lists fields that cannot be parsed.</summary>
    private LlmProfile BuildProfile(out List<string> errors)
    {
        errors = new List<string>();
        var p = new LlmProfile
        {
            Name = _editName,
            Provider = (LlmProvider)Math.Max(0, _provider.Selected >= 0 ? _provider.GetItemId(_provider.Selected) : 0),
            BaseUrl = _baseUrl.Text.Trim(),
            ApiKey = _apiKey.Text.Trim(),
            Model = _model.Text.Trim(),
            MaxTokens = (int)_maxTokens.Value,
            TimeoutSeconds = (int)_timeout.Value,
            ReasoningEffort = EffortOf(_effort, _effortCustom),
            CombatReasoningEffort = EffortOf(_combatEffort, _combatEffortCustom),
            JsonMode = _jsonMode.ButtonPressed,
            ProxyUrl = _proxy.Text.Trim(),
            ExtraBodyJson = _body.Text.Trim(),
            AnthropicVersion = _anthropicVersion.Text.Trim(),
            AnthropicServerFallback = _serverFallback.ButtonPressed,
            PromptCaching = _promptCaching.ButtonPressed,
        };

        var t = _temperature.Text.Trim().Replace(',', '.');
        if (t.Length > 0)
        {
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var temp) && temp >= 0 && temp <= 2)
                p.Temperature = temp;
            else
                errors.Add(L10n.T("ui.api.invalid.temperature"));
        }

        var lines = _headers.Text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                errors.Add(L10n.T("ui.api.invalid.header", i + 1));
                continue;
            }
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace))
            {
                errors.Add(L10n.T("ui.api.invalid.header", i + 1));
                continue;
            }
            p.ExtraHeaders[name] = value;
        }

        if (p.ExtraBodyJson.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(p.ExtraBodyJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) errors.Add(L10n.T("ui.api.invalid.body", "not an object"));
            }
            catch (JsonException ex)
            {
                errors.Add(L10n.T("ui.api.invalid.body", ex.Message));
            }
        }
        return p;
    }

    private void ToggleKeyVisible()
    {
        _apiKey.Secret = !_apiKey.Secret;
        UiKit.SetText(_showKey, L10n.T(_apiKey.Secret ? "ui.api.show_key" : "ui.api.hide_key"));
    }

    private void Save()
    {
        var profile = BuildProfile(out var errors);
        if (errors.Count > 0)
        {
            UpdateState();
            return;
        }
        var name = _editName;
        Edit(s =>
        {
            int i = s.Profiles.FindIndex(x => x.Name == name);
            var copy = profile.Clone();
            copy.Name = name;
            if (i >= 0) s.Profiles[i] = copy;
            else s.Profiles.Add(copy);
        }, now: true);
        _dirty = false;
        Load(name);
        UiKit.SetText(_testResult, L10n.T("ui.api.saved"));
        UiKit.SetColor(_testResult, UiTheme.Good);
    }

    private void Test()
    {
        if (_testing) return;
        var profile = BuildProfile(out var errors);
        if (errors.Count > 0)
        {
            UpdateState();
            return;
        }
        _testing = true;
        _test.Disabled = true;
        UiKit.SetText(_testResult, L10n.T("ui.api.testing"));
        UiKit.SetColor(_testResult, UiTheme.Muted);
        var resultLabel = _testResult;
        var button = _test;
        Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            string text;
            bool ok;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 10, 900) + 5));
                var response = await LlmClientFactory.TestAsync(profile, cts.Token).ConfigureAwait(false);
                var reply = (response.Text ?? "").Trim().Replace('\n', ' ');
                if (reply.Length > 80) reply = reply[..80] + "…";
                var secs = response.Elapsed > TimeSpan.Zero ? response.Elapsed.TotalSeconds : sw.Elapsed.TotalSeconds;
                text = L10n.T("ui.api.test_ok", secs, string.IsNullOrEmpty(response.Model) ? profile.Model : response.Model, reply);
                ok = true;
            }
            catch (LlmException ex)
            {
                text = L10n.T("ui.api.test_fail", sw.Elapsed.TotalSeconds, (ex.StatusCode is { } code ? $"HTTP {code}: " : "") + ex.Message);
                ok = false;
            }
            catch (OperationCanceledException)
            {
                text = L10n.T("ui.api.test_fail", sw.Elapsed.TotalSeconds, "timeout");
                ok = false;
            }
            catch (Exception ex)
            {
                text = L10n.T("ui.api.test_fail", sw.Elapsed.TotalSeconds, ex.GetType().Name + ": " + ex.Message);
                ok = false;
            }
            MainThread.Post(() => UiKit.Guard("api.test.result", () =>
            {
                _testing = false;
                if (SceneUtil.Valid(button)) button.Disabled = false;
                if (!SceneUtil.Valid(resultLabel)) return;
                UiKit.SetText(resultLabel, text);
                UiKit.SetColor(resultLabel, ok ? UiTheme.Good : UiTheme.Bad);
            }));
        });
    }

    // ---------------------------------------------------------------- profile list

    private void OnSelectProfile(int index)
    {
        if (index < 0 || index >= _profileNames.Count) return;
        var name = _profileNames[index];
        if (_dirty && name != _editName)
        {
            ListHint(L10n.T("ui.api.unsaved_switch"));
            int back = _profileNames.IndexOf(Draft.ActiveProfile);
            if (back >= 0) _active.Select(back);
            return;
        }
        ListHint(null);
        Edit(s => s.ActiveProfile = name, now: true);
        Load(name);
    }

    private string UniqueName(string baseName)
    {
        baseName = string.IsNullOrWhiteSpace(baseName) ? "profile" : baseName.Trim();
        var names = Draft.Profiles.Select(p => p.Name).ToHashSet();
        if (!names.Contains(baseName)) return baseName;
        for (int i = 2; ; i++)
            if (!names.Contains($"{baseName}-{i}")) return $"{baseName}-{i}";
    }

    private void AddFromPreset()
    {
        if (_dirty)
        {
            ListHint(L10n.T("ui.api.unsaved_switch"));
            return;
        }
        int i = _preset.Selected >= 0 ? _preset.GetItemId(_preset.Selected) : 0;
        if (i < 0 || i >= LlmPresets.All.Count) return;
        var preset = LlmPresets.All[i];
        var name = UniqueName(preset.Id);
        var profile = LlmPresets.FromPreset(preset, name);
        Edit(s =>
        {
            s.Profiles.Add(profile.Clone());
            s.ActiveProfile = name;
        }, now: true);
        ListHint(null);
        Load(name);
    }

    private void Duplicate()
    {
        if (_dirty)
        {
            ListHint(L10n.T("ui.api.unsaved_switch"));
            return;
        }
        var source = Draft.Profiles.FirstOrDefault(p => p.Name == _editName);
        if (source == null) return;
        var name = UniqueName(_editName + "-copy");
        var copy = source.Clone();
        copy.Name = name;
        Edit(s =>
        {
            s.Profiles.Add(copy.Clone());
            s.ActiveProfile = name;
        }, now: true);
        ListHint(null);
        Load(name);
    }

    private void Delete()
    {
        if (Draft.Profiles.Count <= 1)
        {
            ListHint(L10n.T("ui.api.cant_delete_last"));
            return;
        }
        var now = Time.GetTicksMsec();
        if (now > _deleteArmedUntil)
        {
            // Two-step delete: the first press arms the button for three seconds.
            _deleteArmedUntil = now + 3000;
            UiKit.SetText(_delete, L10n.T("ui.api.delete_confirm"));
            return;
        }
        _deleteArmedUntil = 0;
        UiKit.SetText(_delete, L10n.T("ui.api.delete"));
        var name = _editName;
        Edit(s =>
        {
            s.Profiles.RemoveAll(p => p.Name == name);
            if (s.Profiles.Count == 0) s.Profiles.Add(LlmPresets.Default());
            if (s.Profiles.All(p => p.Name != s.ActiveProfile)) s.ActiveProfile = s.Profiles[0].Name;
        }, now: true);
        _dirty = false;
        ListHint(null);
        Load(Draft.ActiveProfile);
    }

    private void Rename()
    {
        var newName = _rename.Text.Trim();
        var oldName = _editName;
        if (newName == oldName) return;
        if (newName.Length == 0 || Draft.Profiles.Any(p => p.Name == newName))
        {
            ListHint(L10n.T("ui.api.name_taken"));
            return;
        }
        Edit(s =>
        {
            var p = s.Profiles.FirstOrDefault(x => x.Name == oldName);
            if (p == null) return;
            p.Name = newName;
            if (s.ActiveProfile == oldName) s.ActiveProfile = newName;
        }, now: true);
        _editName = newName;
        UiKit.SetText(_editorTitle, L10n.T("ui.api.editor", newName));
        _rename.ReleaseFocus();
        ListHint(null);
    }

    private void ListHint(string? text)
    {
        _listHint.Visible = text != null;
        UiKit.SetText(_listHint, text ?? "");
    }

    public override void Refresh(bool slow)
    {
        if (_deleteArmedUntil != 0 && Time.GetTicksMsec() > _deleteArmedUntil)
        {
            _deleteArmedUntil = 0;
            UiKit.SetText(_delete, L10n.T("ui.api.delete"));
        }
    }
}
