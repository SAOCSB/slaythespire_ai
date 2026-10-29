using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Debug;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Text;
using SpirePilot.Game;
using SpirePilot.Infra;
using SpirePilot.Ui.Pages;

namespace SpirePilot.Ui;

/// <summary>
/// The in-game control panel: a CanvasLayer (layer 128, always processing) under the scene root with a draggable,
/// resizable panel of six tabs and a small always-visible "AI" button that shows the pilot's state. Built entirely
/// from code once <see cref="NGame.Instance"/> exists; driven by <see cref="Tick"/> (main thread, every frame).
/// Only the panel and the button stop the mouse; the rest of the screen stays clickable for the game.
/// </summary>
internal static class Overlay
{
    private const ulong FastRefreshMs = 200;
    private const ulong SlowRefreshMs = 500;
    private const ulong CaptureIntervalMs = 500;

    private static Harmony? _harmony;
    private static bool _initialized;
    private static bool _built;
    private static int _buildAttempts;
    private static int _buildFailures;
    private static int _orphanFrames;

    private static CanvasLayer? _layer;
    private static Control? _host;
    private static PanelContainer? _panel;
    private static Button? _fab;
    private static StyleBoxFlat? _fabNormal, _fabHover;
    private static StyleBoxFlat? _titleDotStyle;
    private static Label? _titleStatus;
    private static Label? _footer;
    private static Button? _saveNow;
    private static readonly List<Page> Pages = new();
    private static readonly List<Button> TabButtons = new();
    private static int _tab;
    private static bool _visible;
    private static string _builtLanguage = "";
    private static int _syncedVersion = -1;

    private static string _toggleKeyName = "";
    private static Key _toggleKey = Key.F8;
    private static bool _keyWasDown;
    private static bool _mouseWasDown;

    private static Rect2 _panelRect;
    private static Rect2 _fabRect;
    private static ulong _nextFast, _nextSlow;
    private static ulong _lastCaptureMs;
    private static Color _statusColor = new(0, 0, 0, 0);

    private static bool _dragging, _resizing;
    private static Vector2 _dragOffset, _resizeStartMouse, _resizeStartSize;

    /// <summary>Called once at startup on the main thread. Creates the overlay once the game's root scene exists.</summary>
    public static void Initialize(Harmony harmony)
    {
        if (_initialized) return;
        _initialized = true;
        _harmony = harmony;
        UiStrings.Register();
        SettingsDraft.Initialize();
        Feeds.Attach(PilotRuntime.Loop, PilotRuntime.Team);
        PilotRuntime.Bridge.Captured += _ => _lastCaptureMs = Time.GetTicksMsec();
        try { InputGuard.ApplyDispatch(harmony); }
        catch (Exception ex) { ModLog.Warn("UI input guard (dispatch) failed: " + ex.Message); }
    }

    /// <summary>Called every frame on the main thread.</summary>
    public static void Tick()
    {
        if (!_initialized) return;
        UiKit.Guard("draft.tick", SettingsDraft.Tick);
        UiKit.Guard("feeds", Feeds.Pump);

        if (!_built || !SceneUtil.Valid(_layer))
        {
            _built = false;
            TryBuild();
            return;
        }

        // Detached from the tree by someone else (scene teardown): hang it back under the root.
        if (_layer!.GetParent() == null)
        {
            if (++_orphanFrames > 30 && SceneUtil.SceneRoot is { } root)
            {
                _orphanFrames = 0;
                root.CallDeferred(Node.MethodName.AddChild, _layer);
            }
            return;
        }
        _orphanFrames = 0;

        if (L10n.Language != _builtLanguage)
        {
            UiKit.Guard("rebuild", Rebuild);
            return;
        }

        UiKit.Guard("hotkey", PollHotkey);
        UiKit.Guard("focus", PollClickOutside);
        UiKit.Guard("layout", UpdateLayout);

        if (SettingsDraft.Version != _syncedVersion) UiKit.Guard("sync", SyncAll);

        foreach (var page in Pages)
            if (!page.Failed) UiKit.Guard("pump:" + page.TitleKey, page.Pump);

        var now = Time.GetTicksMsec();
        if (now >= _nextFast)
        {
            _nextFast = now + FastRefreshMs;
            bool slow = now >= _nextSlow;
            if (slow) _nextSlow = now + SlowRefreshMs;
            UiKit.Guard("status", UpdateStatus);
            if (_visible && _tab >= 0 && _tab < Pages.Count && !Pages[_tab].Failed)
            {
                var page = Pages[_tab];
                if (slow) UiKit.Guard("capture", () => MaybeCapture(page));
                UiKit.Guard("refresh:" + page.TitleKey, () => page.Refresh(slow));
            }
        }
    }

    /// <summary>True while one of the overlay's text fields has keyboard focus.</summary>
    public static bool IsTyping => UiKit.IsTyping;

    /// <summary>Whether <paramref name="point"/> (viewport coordinates) is over the visible panel or the AI button.</summary>
    public static bool IsOverUi(Vector2 point) =>
        _built && (_visible && _panelRect.HasPoint(point) || _fabRect.HasPoint(point));

    /// <summary>Applies a UI scale immediately (the setting itself is saved by the caller).</summary>
    public static void ApplyScale(float scale)
    {
        scale = Math.Clamp(scale, 0.5f, 3f);
        if (SceneUtil.Valid(_panel)) _panel!.Scale = new Vector2(scale, scale);
        if (SceneUtil.Valid(_fab)) _fab!.Scale = new Vector2(scale, scale);
    }

    // ---------------------------------------------------------------- build

    private static void TryBuild()
    {
        // Retry at most every few frames: NGame appears a little after mods are initialized.
        if (_buildFailures >= 5 || _buildAttempts++ % 10 != 0) return;
        try
        {
            if (DisplayServer.GetName() == "headless") return;
            var game = NGame.Instance;
            var root = SceneUtil.SceneRoot;
            if (game == null || !GodotObject.IsInstanceValid(game) || root == null) return;

            if (SceneUtil.Valid(_layer)) _layer!.QueueFree();
            _layer = new CanvasLayer { Name = "SpirePilotOverlay", Layer = 128, ProcessMode = Node.ProcessModeEnum.Always };
            _host = new Control { Name = "Host", MouseFilter = Control.MouseFilterEnum.Ignore };
            _host.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _layer.AddChild(_host);
            BuildContent();
            // Deferred: the root may be busy setting up children this frame.
            root.CallDeferred(Node.MethodName.AddChild, _layer);
            _built = true;
            _visible = PilotRuntime.Settings.Ui.ShowOnStart;
            ApplyVisibility();
            ModLog.Info("overlay ready");

            if (_harmony != null)
            {
                var harmony = _harmony;
                MainThread.Post(() =>
                {
                    try { InputGuard.ApplyInputOverrides(harmony); }
                    catch (Exception ex) { ModLog.Warn("UI input guard (_Input) failed: " + ex.Message); }
                });
            }
        }
        catch (Exception ex)
        {
            _built = false;
            _buildAttempts = 1; // back off: next try in 10 frames
            if (++_buildFailures >= 5) ModLog.Error("overlay disabled after repeated build failures: " + ex);
            else ModLog.Error("overlay build failed: " + ex);
        }
    }

    /// <summary>Rebuilds everything under the host (language change); keeps visibility, tab and position.</summary>
    private static void Rebuild()
    {
        if (!SceneUtil.Valid(_host)) return;
        var typing = UiKit.IsTyping;
        if (typing) _host!.GetViewport()?.GuiReleaseFocus();
        foreach (var child in _host!.GetChildren())
        {
            _host.RemoveChild(child);
            child.QueueFree();
        }
        BuildContent();
        ApplyVisibility();
    }

    private static void BuildContent()
    {
        _builtLanguage = L10n.Language;
        UiTheme.Rebuild();
        _host!.Theme = UiTheme.Theme;
        BuildFab();
        BuildPanel();
        _statusColor = new Color(0, 0, 0, 0);
        _syncedVersion = -1;
        SyncAll();
        _nextFast = 0;
        _nextSlow = 0;
    }

    private static void BuildFab()
    {
        _fabNormal = UiTheme.Box(new Color(0.07f, 0.08f, 0.10f, 0.92f), UiTheme.Faint, 8, 2, 10, 4);
        _fabHover = UiTheme.Box(new Color(0.12f, 0.13f, 0.16f, 0.96f), UiTheme.Faint, 8, 2, 10, 4);
        _fab = new Button
        {
            Name = "SpirePilotButton",
            Text = "AI",
            FocusMode = Control.FocusModeEnum.None,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            CustomMinimumSize = new Vector2(46, 30),
        };
        _fab.AddThemeStyleboxOverride("normal", _fabNormal);
        _fab.AddThemeStyleboxOverride("hover", _fabHover);
        _fab.AddThemeStyleboxOverride("pressed", _fabHover);
        _fab.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        _fab.AddThemeFontOverride("font", UiTheme.Bold);
        _fab.AddThemeFontSizeOverride("font_size", UiTheme.FontBody);
        _fab.Pressed += UiKit.Safe("fab", () => SetVisible(!_visible));
        _host!.AddChild(_fab);
    }

    private static void BuildPanel()
    {
        Pages.Clear();
        TabButtons.Clear();
        Pages.Add(new ControlPage());
        Pages.Add(new RoutePage());
        Pages.Add(new CharactersPage());
        Pages.Add(new TeamPage());
        Pages.Add(new ApiPage());
        Pages.Add(new LogPage());

        // The draft: a placement change that is not saved yet survives a rebuild.
        var ui = SettingsDraft.Value.Ui;
        _panel = new PanelContainer
        {
            Name = "SpirePilotPanel",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Position = new Vector2(ui.PanelX, ui.PanelY),
            Size = new Vector2(ui.PanelWidth, ui.PanelHeight),
            Scale = new Vector2(ui.Scale, ui.Scale),
            ClipContents = true,
        };
        var frame = UiTheme.Box(UiTheme.PanelBg, new Color(UiTheme.Gold, 0.55f), 10, 2, 2, 2);
        frame.ShadowColor = new Color(0, 0, 0, 0.5f);
        frame.ShadowSize = 14;
        frame.ShadowOffset = new Vector2(0, 4);
        _panel.AddThemeStyleboxOverride("panel", frame);

        var column = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        column.AddThemeConstantOverride("separation", 0);
        _panel.AddChild(column);

        // Title bar (drag handle).
        var title = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop, MouseDefaultCursorShape = Control.CursorShape.Move };
        var titleStyle = UiTheme.Box(UiTheme.TitleBg, UiTheme.Clear, 0, 0, 12, 7);
        titleStyle.CornerRadiusTopLeft = 9;
        titleStyle.CornerRadiusTopRight = 9;
        titleStyle.BorderWidthBottom = 1;
        titleStyle.BorderColor = new Color(UiTheme.Gold, 0.35f);
        title.AddThemeStyleboxOverride("panel", titleStyle);
        title.GuiInput += ev => UiKit.Guard("drag", () => OnTitleInput(title, ev));
        var dot = UiKit.Dot(UiTheme.Faint, out var dotStyle, 12);
        _titleDotStyle = dotStyle;
        var name = UiKit.Label(L10n.T("ui.title"), UiTheme.FontTitle, UiTheme.Gold, wrap: false);
        name.AddThemeFontOverride("font", UiTheme.Bold);
        name.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        name.MouseFilter = Control.MouseFilterEnum.Ignore;
        _titleStatus = UiKit.Label("", UiTheme.FontSmall, UiTheme.Muted, wrap: false);
        _titleStatus.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _titleStatus.ClipText = true;
        _titleStatus.MouseFilter = Control.MouseFilterEnum.Ignore;
        _titleStatus.VerticalAlignment = VerticalAlignment.Center;
        var hide = UiKit.Button("×", () => SetVisible(false), UiTheme.ButtonKind.Ghost, L10n.T("ui.hide"));
        hide.AddThemeFontSizeOverride("font_size", UiTheme.FontTitle);
        var titleRow = UiKit.Row(dot, name, _titleStatus, hide);
        titleRow.MouseFilter = Control.MouseFilterEnum.Ignore;
        title.AddChild(titleRow);
        column.AddChild(title);

        // Tabs.
        var tabsMargin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        tabsMargin.AddThemeConstantOverride("margin_left", 8);
        tabsMargin.AddThemeConstantOverride("margin_right", 8);
        tabsMargin.AddThemeConstantOverride("margin_top", 6);
        tabsMargin.AddThemeConstantOverride("margin_bottom", 4);
        var tabs = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        tabs.AddThemeConstantOverride("separation", 2);
        tabsMargin.AddChild(tabs);
        column.AddChild(tabsMargin);
        for (int i = 0; i < Pages.Count; i++)
        {
            int index = i;
            var b = UiKit.Button(L10n.T(Pages[i].TitleKey), () => SelectTab(index), UiTheme.ButtonKind.Tab);
            b.ToggleMode = true;
            b.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            b.ClipText = true;
            b.AddThemeFontSizeOverride("font_size", UiTheme.FontHeading);
            TabButtons.Add(b);
            tabs.AddChild(b);
        }

        // Pages.
        var content = new MarginContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        content.AddThemeConstantOverride("margin_left", 8);
        content.AddThemeConstantOverride("margin_right", 2);
        content.AddThemeConstantOverride("margin_top", 2);
        content.AddThemeConstantOverride("margin_bottom", 2);
        column.AddChild(content);
        foreach (var page in Pages)
        {
            page.Build();
            content.AddChild(page.Root);
        }

        // Footer with resize grip.
        var footerMargin = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        footerMargin.AddThemeConstantOverride("margin_left", 12);
        footerMargin.AddThemeConstantOverride("margin_right", 4);
        footerMargin.AddThemeConstantOverride("margin_bottom", 2);
        _footer = UiKit.Label("", UiTheme.FontSmall, UiTheme.Faint, wrap: false);
        _footer.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _footer.ClipText = true;
        _footer.VerticalAlignment = VerticalAlignment.Center;
        var grip = new TextureRect
        {
            Texture = UiTheme.GripIcon(),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
            TooltipText = L10n.T("ui.resize_tip"),
            CustomMinimumSize = new Vector2(22, 22),
            StretchMode = TextureRect.StretchModeEnum.KeepCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkEnd,
        };
        grip.GuiInput += ev => UiKit.Guard("resize", () => OnGripInput(grip, ev));
        _saveNow = UiKit.Button(L10n.T("ui.save"), SettingsDraft.Flush, UiTheme.ButtonKind.Small);
        _saveNow.Visible = false;
        footerMargin.AddChild(UiKit.Row(_footer, _saveNow, grip));
        column.AddChild(footerMargin);

        _host!.AddChild(_panel);
        _tab = Math.Clamp(_tab, 0, Pages.Count - 1);
        SelectTab(_tab);
    }

    // ---------------------------------------------------------------- state

    private static void SelectTab(int index)
    {
        _tab = Math.Clamp(index, 0, Pages.Count - 1);
        for (int i = 0; i < Pages.Count; i++)
        {
            if (Pages[i].Root != null && SceneUtil.Valid(Pages[i].Root)) Pages[i].Root.Visible = i == _tab;
            if (i < TabButtons.Count) UiKit.SetPressed(TabButtons[i], i == _tab);
        }
        _nextFast = 0;
        _nextSlow = 0;
    }

    private static void SetVisible(bool visible)
    {
        _visible = visible;
        ApplyVisibility();
        _nextFast = 0;
        _nextSlow = 0;
    }

    private static void ApplyVisibility()
    {
        if (!SceneUtil.Valid(_panel)) return;
        _panel!.Visible = _visible;
        if (!_visible)
        {
            _dragging = false;
            _resizing = false;
            if (UiKit.IsTyping) _panel.GetViewport()?.GuiReleaseFocus();
            SettingsDraft.Flush();
        }
    }

    private static void SyncAll()
    {
        _syncedVersion = SettingsDraft.Version;
        var settings = SettingsDraft.Value;
        UiKit.Syncing = true;
        try
        {
            foreach (var page in Pages)
            {
                if (page.Failed) continue;
                try { page.Sync(settings); }
                catch (Exception ex) { ThrottledLog.Warn("ui.sync:" + page.TitleKey, $"UI sync of {page.TitleKey} failed: {ex}"); }
            }
        }
        finally
        {
            UiKit.Syncing = false;
        }
        ApplyScale(settings.Ui.Scale);
    }

    private static void UpdateStatus()
    {
        var status = PilotRuntime.Loop.Status;
        var color = UiTheme.StateColor(status.State);
        var keyName = PilotRuntime.Settings.Ui.ToggleKey;
        bool recolor = color != _statusColor;
        _statusColor = color;
        if (recolor && _fabNormal != null) _fabNormal.BorderColor = color;
        if (recolor && _fabHover != null) _fabHover.BorderColor = color;
        if (recolor && _titleDotStyle != null) _titleDotStyle.BgColor = color;
        if (SceneUtil.Valid(_fab))
        {
            if (recolor)
            {
                _fab!.AddThemeColorOverride("font_color", color);
                _fab.AddThemeColorOverride("font_hover_color", color);
                _fab.AddThemeColorOverride("font_pressed_color", color);
            }
            var tip = L10n.T("ui.fab.tooltip", keyName, status.Text.Length > 0 ? status.Text : L10n.T("ui.state." + status.State));
            if (_fab!.TooltipText != tip) _fab.TooltipText = tip;
        }
        UiKit.SetText(_titleStatus, status.Text.Length > 0 ? status.Text : L10n.T("ui.state." + status.State));

        string footer;
        if (SettingsDraft.SaveError is { } err) footer = L10n.T("ui.footer.save_failed", err);
        else if (SettingsDraft.Dirty) footer = L10n.T("ui.footer.pending");
        else if (SettingsDraft.LastSavedMs != 0 && Time.GetTicksMsec() - SettingsDraft.LastSavedMs < 2500) footer = L10n.T("ui.footer.saved");
        else footer = L10n.T("ui.footer.hint", keyName);
        UiKit.SetText(_footer, footer);
        UiKit.SetColor(_footer, SettingsDraft.SaveError != null ? UiTheme.Bad : UiTheme.Faint);
        if (SceneUtil.Valid(_saveNow)) _saveNow!.Visible = SettingsDraft.Dirty || SettingsDraft.SaveError != null;
    }

    /// <summary>
    /// Keeps the snapshot fresh while a live page is visible, at most every 0.5 s. The pilot captures on its own when
    /// it is waiting / advising; while it thinks or acts we stay away, so an action it picked is never superseded by
    /// our snapshot between its re-validation and its execution.
    /// </summary>
    private static void MaybeCapture(Page page)
    {
        if (!page.WantsSnapshot) return;
        var now = Time.GetTicksMsec();
        if (now - _lastCaptureMs < CaptureIntervalMs) return;
        var state = PilotRuntime.Loop.Status.State;
        if (state is PilotState.Thinking or PilotState.Acting) return;
        _lastCaptureMs = now;
        _ = PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
    }

    // ---------------------------------------------------------------- input

    private static void PollHotkey()
    {
        var name = PilotRuntime.Settings.Ui.ToggleKey ?? "F8";
        if (name != _toggleKeyName)
        {
            _toggleKeyName = name;
            _toggleKey = ParseKey(name);
        }
        bool down = _toggleKey != Key.None && (Input.IsKeyPressed(_toggleKey) || Input.IsPhysicalKeyPressed(_toggleKey));
        if (down && !_keyWasDown)
        {
            bool typingKey = (long)_toggleKey < (long)Key.Special;
            if (!(typingKey && UiKit.IsTyping) && !DevConsoleOpen()) SetVisible(!_visible);
        }
        _keyWasDown = down;
    }

    private static Key ParseKey(string name)
    {
        name = (name ?? "").Trim();
        if (Enum.TryParse<Key>(name, ignoreCase: true, out var key) && key != Key.None) return key;
        if (name.Length > 0) ThrottledLog.Warn("ui.key", $"UI: unknown toggle key '{name}', using F8");
        return Key.F8;
    }

    private static bool DevConsoleOpen()
    {
        try
        {
            var console = NDevConsole.Instance;
            return console != null && GodotObject.IsInstanceValid(console) && console.IsVisibleInTree();
        }
        catch (Exception) { return false; }
    }

    /// <summary>Clicking anywhere outside the panel ends typing, so game hotkeys work again.</summary>
    private static void PollClickOutside()
    {
        bool down = Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right);
        if (down && !_mouseWasDown && UiKit.IsTyping && SceneUtil.Valid(_host))
        {
            var mouse = _host!.GetGlobalMousePosition();
            if (!IsOverUi(mouse)) _host.GetViewport()?.GuiReleaseFocus();
        }
        _mouseWasDown = down;
    }

    private static void OnTitleInput(Control title, InputEvent ev)
    {
        if (!SceneUtil.Valid(_panel) || !SceneUtil.Valid(_host)) return;
        switch (ev)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed)
                {
                    _dragging = true;
                    _dragOffset = _host!.GetGlobalMousePosition() - _panel!.Position;
                }
                else if (_dragging)
                {
                    _dragging = false;
                    SavePlacement();
                }
                title.AcceptEvent();
                break;
            case InputEventMouseMotion when _dragging:
                _panel!.Position = ClampPosition(_host!.GetGlobalMousePosition() - _dragOffset);
                title.AcceptEvent();
                break;
        }
    }

    private static void OnGripInput(Control grip, InputEvent ev)
    {
        if (!SceneUtil.Valid(_panel) || !SceneUtil.Valid(_host)) return;
        switch (ev)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } mb:
                if (mb.Pressed)
                {
                    _resizing = true;
                    _resizeStartMouse = _host!.GetGlobalMousePosition();
                    _resizeStartSize = _panel!.Size;
                }
                else if (_resizing)
                {
                    _resizing = false;
                    SavePlacement();
                }
                grip.AcceptEvent();
                break;
            case InputEventMouseMotion when _resizing:
                var scale = Math.Max(0.1f, _panel!.Scale.X);
                var delta = (_host!.GetGlobalMousePosition() - _resizeStartMouse) / scale;
                var size = _resizeStartSize + delta;
                _panel.Size = new Vector2(Math.Clamp(size.X, 320, 1600), Math.Clamp(size.Y, 300, 1400));
                grip.AcceptEvent();
                break;
        }
    }

    private static void SavePlacement()
    {
        if (!SceneUtil.Valid(_panel)) return;
        var pos = _panel!.Position;
        var size = _panel.Size;
        SettingsDraft.Edit(s =>
        {
            s.Ui.PanelX = (float)Math.Round(pos.X);
            s.Ui.PanelY = (float)Math.Round(pos.Y);
            s.Ui.PanelWidth = (float)Math.Round(size.X);
            s.Ui.PanelHeight = (float)Math.Round(size.Y);
        });
    }

    // ---------------------------------------------------------------- layout

    private static Vector2 ViewportSize()
    {
        var vp = _host?.GetViewport();
        return vp != null ? vp.GetVisibleRect().Size : new Vector2(1920, 1080);
    }

    /// <summary>Keeps at least the title bar on screen.</summary>
    private static Vector2 ClampPosition(Vector2 pos)
    {
        var vp = ViewportSize();
        var scale = SceneUtil.Valid(_panel) ? _panel!.Scale.X : 1f;
        var width = (SceneUtil.Valid(_panel) ? _panel!.Size.X : 400) * scale;
        float minX = Math.Min(0, 80 - width);
        float maxX = Math.Max(minX, vp.X - 80);
        return new Vector2(Math.Clamp(pos.X, minX, maxX), Math.Clamp(pos.Y, 0, Math.Max(0, vp.Y - 40)));
    }

    private static void UpdateLayout()
    {
        var vp = ViewportSize();
        if (SceneUtil.Valid(_fab))
        {
            // Right edge, about a third down: clear of the top bar, the hand and the piles (check in game).
            var s = _fab!.Scale.X;
            var size = _fab.Size * s;
            _fab.Position = new Vector2(vp.X - size.X - 6, Mathf.Round(vp.Y * 0.30f));
            _fabRect = new Rect2(_fab.Position, size);
        }
        if (SceneUtil.Valid(_panel))
        {
            if (!_dragging)
            {
                var clamped = ClampPosition(_panel!.Position);
                if (clamped != _panel.Position) _panel.Position = clamped;
            }
            _panelRect = _panel!.Visible ? _panel.GetGlobalRect() : new Rect2();
        }
    }
}
