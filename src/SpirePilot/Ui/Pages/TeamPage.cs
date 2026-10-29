using Godot;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;
using SpirePilot.Core.Text;

namespace SpirePilot.Ui.Pages;

/// <summary>Tab 4: co-op chat, quick requests, team settings and the teammates list.</summary>
internal sealed class TeamPage : Page
{
    private const int MaxRenderedEntries = 400;

    private RichTextLabel _chat = null!;
    private readonly HashSet<ChatEntry> _rendered = new(ReferenceEqualityComparer.Instance);
    private bool _chatEmpty;
    private OptionButton _to = null!;
    private readonly List<ulong> _toIds = new();
    private string _toKey = "";
    private LineEdit _input = null!;
    private Label _toHint = null!;
    private Control _emotes = null!;

    private OptionButton _potion = null!;
    private readonly List<PotionInfo> _potions = new();
    private string _potionKey = "";
    private OptionButton _enemy = null!;
    private readonly List<CreatureInfo> _enemies = new();
    private string _enemyKey = "";
    private OptionButton _debuff = null!;
    private LineEdit _debuffOther = null!;
    private Label _quickHint = null!;

    private CheckButton _autoReply = null!, _proactive = null!, _givePotions = null!, _bubbles = null!;
    private SpinBox _rate = null!;
    private OptionButton _lang = null!;
    private string _customLang = "";

    private RichTextLabel _mates = null!;
    private Label _matesHint = null!;
    private string _matesKey = "";

    private const int DebuffAny = 0, DebuffVulnerable = 1, DebuffWeak = 2, DebuffOther = 3;
    /// <summary>Id of a placeholder item ("no potions"). Not -1: Godot replaces a -1 id with the item's index.</summary>
    private const int NoItemId = 10_000;
    private const int LangAuto = 0, LangZh = 1, LangEn = 2, LangCustom = 3;

    public override string TitleKey => "ui.tab.team";

    protected override void BuildContent()
    {
        // ---- chat ----
        var chat = UiKit.Card(Body, L10n.T("ui.team.chat"));
        _chat = UiKit.Rich(200, follow: true);
        chat.AddChild(_chat);
        _to = UiKit.Options(_ => RefreshReach(), "team.to");
        UiKit.Field(chat, L10n.T("ui.team.to"), _to);
        _input = UiKit.Line(L10n.T("ui.team.input_ph"), onSubmit: _ => SendChat(), where: "team.input");
        _input.MaxLength = 400;
        chat.AddChild(UiKit.Row(_input, UiKit.Button(L10n.T("ui.team.send"), SendChat, UiTheme.ButtonKind.Primary)));
        // Teammates without SpirePilot cannot read messages: say so, and offer the game's own reaction emotes.
        _toHint = UiKit.Label("", UiTheme.FontSmall, UiTheme.Warn);
        _toHint.Visible = false;
        chat.AddChild(_toHint);
        _emotes = UiKit.Flow(
            UiKit.Muted(L10n.T("ui.team.emote")),
            EmoteButton(TeamMessageKind.Accept, "ui.team.emote.up"),
            EmoteButton(TeamMessageKind.Decline, "ui.team.emote.down"),
            EmoteButton(TeamMessageKind.RequestFocus, "ui.team.emote.alert"),
            EmoteButton(TeamMessageKind.AskNeedPotion, "ui.team.emote.question"));
        _emotes.Visible = false;
        chat.AddChild(_emotes);

        // ---- quick requests ----
        var quick = UiKit.Card(Body, L10n.T("ui.team.quick"));
        quick.AddChild(UiKit.Flow(
            QuickButton(TeamMessageKind.RequestPotion),
            QuickButton(TeamMessageKind.AskNeedPotion),
            QuickButton(TeamMessageKind.RequestBlock)));
        _potion = UiKit.Options(_ => { }, "team.potion");
        UiKit.Field(quick, L10n.T("ui.team.potion"), UiKit.Row(_potion, QuickButton(TeamMessageKind.OfferPotion)));
        _enemy = UiKit.Options(_ => { }, "team.enemy");
        UiKit.Field(quick, L10n.T("ui.team.enemy"), UiKit.Row(_enemy, QuickButton(TeamMessageKind.RequestFocus)));
        _debuff = UiKit.Options(id => _debuffOther.Visible = id == DebuffOther, "team.debuff");
        UiKit.SetItems(_debuff, new[]
        {
            (L10n.T("ui.team.debuff.Any"), DebuffAny),
            (L10n.T("ui.team.debuff.Vulnerable"), DebuffVulnerable),
            (L10n.T("ui.team.debuff.Weak"), DebuffWeak),
            (L10n.T("ui.team.debuff.Other"), DebuffOther),
        });
        _debuffOther = UiKit.Line(L10n.T("ui.team.debuff_other_ph"), where: "team.debuff_other");
        _debuffOther.Visible = false;
        _debuffOther.MaxLength = 40;
        var debuffBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, MouseFilter = Control.MouseFilterEnum.Pass };
        debuffBox.AddChild(UiKit.Row(_debuff, QuickButton(TeamMessageKind.RequestDebuff)));
        debuffBox.AddChild(_debuffOther);
        UiKit.Field(quick, L10n.T("ui.team.debuff"), debuffBox);
        _quickHint = UiKit.Muted("");
        quick.AddChild(_quickHint);

        // ---- teammates ----
        var mates = UiKit.Card(Body, L10n.T("ui.team.mates"));
        _matesHint = UiKit.Label("", UiTheme.FontSmall, UiTheme.Muted);
        mates.AddChild(_matesHint);
        _mates = UiKit.Rich(0);
        _mates.FitContent = true;
        _mates.ScrollActive = false;
        mates.AddChild(_mates);

        // ---- settings ----
        var settings = UiKit.Card(Body, L10n.T("ui.team.settings"));
        _autoReply = UiKit.Switch(L10n.T("ui.team.auto_reply"), on => Edit(s => s.Team.AutoReply = on));
        _proactive = UiKit.Switch(L10n.T("ui.team.proactive"), on => Edit(s => s.Team.ProactiveRequests = on));
        _givePotions = UiKit.Switch(L10n.T("ui.team.give_potions"), on => Edit(s => s.Team.AllowGivingPotions = on));
        _bubbles = UiKit.Switch(L10n.T("ui.team.bubbles"), on => Edit(s => s.Team.SpeechBubbles = on));
        foreach (var c in new Control[] { _autoReply, _proactive, _givePotions, _bubbles }) settings.AddChild(c);
        _rate = UiKit.Spin(0, 30, 1, v => Edit(s => s.Team.MaxAiMessagesPerMinute = (int)v), "team.rate");
        UiKit.Field(settings, L10n.T("ui.team.rate"), _rate);
        _lang = UiKit.Options(OnLanguage, "team.lang");
        UiKit.Field(settings, L10n.T("ui.team.lang"), _lang);

        RenderAllChat();
        RefreshPickers();
    }

    private Button QuickButton(TeamMessageKind kind) =>
        UiKit.Button(L10n.T("ui.team.q." + kind), () => SendQuick(kind), UiTheme.ButtonKind.Small);

    private Button EmoteButton(TeamMessageKind kind, string key) =>
        UiKit.Button(L10n.T(key), () => Emote(kind), UiTheme.ButtonKind.Small);

    private void Emote(TeamMessageKind kind)
    {
        bool sent = false;
        try { sent = PilotRuntime.Bridge.TrySignal(kind); }
        catch (Exception) { /* reported below */ }
        UiKit.SetText(_toHint, sent ? L10n.T("ui.team.emote_sent") : L10n.T("ui.team.emote_failed"));
        UiKit.SetColor(_toHint, sent ? UiTheme.Muted : UiTheme.Warn);
        _toHint.Visible = true;
    }

    // ---------------------------------------------------------------- settings

    public override void Sync(PilotSettings s)
    {
        UiKit.SetPressed(_autoReply, s.Team.AutoReply);
        UiKit.SetPressed(_proactive, s.Team.ProactiveRequests);
        UiKit.SetPressed(_givePotions, s.Team.AllowGivingPotions);
        UiKit.SetPressed(_bubbles, s.Team.SpeechBubbles);
        UiKit.SetValue(_rate, s.Team.MaxAiMessagesPerMinute);

        var lang = (s.Team.MessageLanguage ?? "auto").Trim();
        int id = lang.ToLowerInvariant() switch
        {
            "" or "auto" => LangAuto,
            "zh" or "zhs" => LangZh,
            "en" or "eng" => LangEn,
            _ => LangCustom,
        };
        _customLang = id == LangCustom ? lang : "";
        var items = new List<(string, int)>
        {
            (L10n.T("ui.lang.auto"), LangAuto), (L10n.T("ui.lang.zh"), LangZh), (L10n.T("ui.lang.en"), LangEn),
        };
        if (id == LangCustom) items.Add((lang, LangCustom));
        UiKit.SetItems(_lang, items);
        UiKit.SelectId(_lang, id);
    }

    private void OnLanguage(int id)
    {
        var value = id switch { LangZh => "zh", LangEn => "en", LangCustom => _customLang, _ => "auto" };
        Edit(s => s.Team.MessageLanguage = value);
    }

    // ---------------------------------------------------------------- live

    public override void Pump()
    {
        if (Feeds.NewChat.Count == 0) return;
        foreach (var e in Feeds.NewChat) AppendChat(e);
        Feeds.NewChat.Clear();
        if (_rendered.Count > MaxRenderedEntries) RenderAllChat();
    }

    public override void Refresh(bool slow)
    {
        if (!slow) return;
        RefreshPickers();
        RefreshMates();
        RefreshReach();
    }

    /// <summary>
    /// Who can read a message to the selected recipient: teammates with SpirePilot only. Shows the "link unavailable"
    /// hint and the emote buttons when nobody addressed can (co-op only).
    /// </summary>
    private void RefreshReach()
    {
        if (_toHint == null || _emotes == null) return;
        var snap = Snap;
        bool unreachable = snap is { IsMultiplayer: true } && Readers(snap, SelectedRecipient()).Count == 0 && snap.Teammates.Any();
        _emotes.Visible = unreachable;
        if (unreachable)
        {
            UiKit.SetText(_toHint, L10n.T("team.link_unavailable"));
            UiKit.SetColor(_toHint, UiTheme.Warn);
        }
        _toHint.Visible = unreachable;
    }

    /// <summary>Teammates who would read a message to <paramref name="to"/> (0 = everyone).</summary>
    private static List<PlayerInfo> Readers(GameSnapshot snap, ulong to) =>
        snap.Teammates.Where(p => p.HasTeamLink && (to == 0 || p.NetId == to)).ToList();

    private void RenderAllChat()
    {
        _chat.Clear();
        _rendered.Clear();
        Feeds.NewChat.Clear();
        var log = PilotRuntime.Team.Log;
        _chatEmpty = log.Count == 0;
        if (_chatEmpty) _chat.AppendText(UiKit.Colored(L10n.T("ui.team.empty"), UiTheme.Faint));
        foreach (var e in log) AppendChat(e);
    }

    private void AppendChat(ChatEntry e)
    {
        if (!_rendered.Add(e)) return;
        if (_chatEmpty)
        {
            _chat.Clear();
            _chatEmpty = false;
        }
        else if (_rendered.Count > 1)
        {
            _chat.AppendText("\n");
        }
        _chat.AppendText(FormatEntry(e));
    }

    private string FormatEntry(ChatEntry e)
    {
        var m = e.Message;
        var time = UiKit.Colored(e.LocalTime.ToString("HH:mm"), UiTheme.Faint);
        bool systemNote = e.Incoming && !e.Delivered;
        if (systemNote) return $"{time} {UiKit.Colored(m.Text, UiTheme.Muted)}";

        var who = e.Incoming ? (string.IsNullOrEmpty(m.FromName) ? "?" : m.FromName) : L10n.T("ui.team.me");
        var line = $"{time} [b]{UiKit.Colored(who, e.Incoming ? UiTheme.Info : UiTheme.Gold)}[/b]";
        if (m.FromAi) line += " " + UiKit.Colored("[AI]", UiTheme.StateColor(Core.Agent.PilotState.Thinking));
        if (m.To != 0) line += " → " + UiKit.Esc(NameOf(m.To));
        if (m.Kind != TeamMessageKind.Chat)
        {
            var kindKey = "ui.team.q." + m.Kind;
            line += " " + UiKit.Colored("(" + (L10n.Has(kindKey) ? L10n.T(kindKey) : m.Kind.ToString()) + ")", UiTheme.Muted);
        }
        line += ": " + UiKit.Esc(m.Text);
        if (!e.Incoming && !e.Delivered) line += " " + UiKit.Colored(L10n.T("ui.team.undelivered"), UiTheme.Warn);
        return line;
    }

    private static string NameOf(ulong netId)
    {
        var p = PilotRuntime.Bridge?.Latest?.Players.FirstOrDefault(x => x.NetId == netId);
        return p == null ? netId.ToString() : string.IsNullOrEmpty(p.Name) ? p.CharacterName : p.Name;
    }

    private void RefreshPickers()
    {
        var snap = Snap;
        var mates = snap?.Teammates.ToList() ?? new List<PlayerInfo>();

        // Recipients.
        var toKey = string.Join("|", mates.Select(p => p.NetId + ":" + p.Name + ":" + p.CharacterName + ":" + p.HasTeamLink));
        if (toKey != _toKey || _to.ItemCount == 0)
        {
            _toKey = toKey;
            ulong selected = SelectedRecipient();
            _toIds.Clear();
            _to.Clear();
            _to.AddItem(L10n.T("ui.team.everyone"), 0);
            _toIds.Add(0);
            foreach (var p in mates)
            {
                var name = string.IsNullOrEmpty(p.Name) ? p.CharacterName : p.Name;
                if (!string.IsNullOrEmpty(p.CharacterName) && p.CharacterName != name) name += $" ({p.CharacterName})";
                // Visible in the picker: they only see emotes, never text.
                if (!p.HasTeamLink) name += " · " + L10n.T("ui.team.no_mod");
                _to.AddItem(name, _toIds.Count);
                _toIds.Add(p.NetId);
            }
            int index = Math.Max(0, _toIds.IndexOf(selected));
            _to.Select(index);
        }

        // My potions.
        var potions = snap?.LocalPlayer?.Potions ?? new List<PotionInfo>();
        var potionKey = string.Join("|", potions.Select(p => p.Slot + ":" + p.Id));
        if (potionKey != _potionKey || _potion.ItemCount == 0)
        {
            _potionKey = potionKey;
            _potions.Clear();
            _potions.AddRange(potions);
            _potion.Clear();
            if (_potions.Count == 0) _potion.AddItem(L10n.T("ui.team.no_potions"), NoItemId);
            for (int i = 0; i < _potions.Count; i++) _potion.AddItem(_potions[i].Name, i);
            _potion.Select(0);
        }

        // Enemies.
        var enemies = snap?.Combat?.Enemies.Where(e => e.IsAlive).ToList() ?? new List<CreatureInfo>();
        var enemyKey = string.Join("|", enemies.Select(e => e.Key + ":" + e.Name));
        if (enemyKey != _enemyKey || _enemy.ItemCount == 0)
        {
            var previous = SelectedEnemy()?.Key;
            _enemyKey = enemyKey;
            _enemies.Clear();
            _enemies.AddRange(enemies);
            _enemy.Clear();
            if (_enemies.Count == 0) _enemy.AddItem(L10n.T("ui.team.no_enemies"), NoItemId);
            for (int i = 0; i < _enemies.Count; i++) _enemy.AddItem($"{_enemies[i].Name} ({_enemies[i].Hp}/{_enemies[i].MaxHp})", i);
            _enemy.Select(Math.Max(0, _enemies.FindIndex(e => e.Key == previous)));
        }
    }

    private void RefreshMates()
    {
        var snap = Snap;
        var mates = snap?.Teammates.ToList() ?? new List<PlayerInfo>();
        var key = (snap?.IsMultiplayer ?? false) + "|" + string.Join("|", mates.Select(p =>
            $"{p.NetId}:{p.Name}:{p.CharacterName}:{p.Hp}/{p.MaxHp}:{p.IsAlive}:{p.HasTeamLink}:{p.IsAiControlled}:{string.Join(",", p.Potions.Select(x => x.Name))}"));
        if (key == _matesKey) return;
        _matesKey = key;

        _mates.Clear();
        if (snap == null || !snap.IsMultiplayer)
        {
            UiKit.SetText(_matesHint, L10n.T("ui.team.not_mp"));
            UiKit.SetColor(_matesHint, UiTheme.Muted);
            _matesHint.Visible = true;
            _mates.Visible = false;
            return;
        }
        if (mates.Count == 0)
        {
            UiKit.SetText(_matesHint, L10n.T("ui.team.no_mates"));
            UiKit.SetColor(_matesHint, UiTheme.Muted);
            _matesHint.Visible = true;
            _mates.Visible = false;
            return;
        }
        bool anyLink = mates.Any(p => p.HasTeamLink);
        _matesHint.Visible = !anyLink;
        if (!anyLink)
        {
            UiKit.SetText(_matesHint, L10n.T("team.link_unavailable"));
            UiKit.SetColor(_matesHint, UiTheme.Warn);
        }
        _mates.Visible = true;
        var lines = new List<string>();
        foreach (var p in mates)
        {
            var name = string.IsNullOrEmpty(p.Name) ? p.CharacterName : p.Name;
            var head = $"[b]{UiKit.Colored(name, p.HasTeamLink ? UiTheme.Text : UiTheme.Muted)}[/b]";
            if (!string.IsNullOrEmpty(p.CharacterName) && p.CharacterName != name) head += " · " + UiKit.Esc(p.CharacterName);
            head += " · " + UiKit.Colored($"HP {p.Hp}/{p.MaxHp}", p.MaxHp > 0 && p.Hp * 2 < p.MaxHp ? UiTheme.Warn : UiTheme.Good);
            if (!p.IsAlive) head += " " + UiKit.Colored(L10n.T("ui.team.dead"), UiTheme.Bad);
            var potions = p.Potions.Count == 0 ? L10n.T("ui.none") : string.Join(", ", p.Potions.Select(x => x.Name));
            var detail = "   " + UiKit.Colored(L10n.T("ui.team.potions", potions), UiTheme.Muted) + " · " +
                         (p.HasTeamLink ? UiKit.Colored(L10n.T("ui.team.has_mod"), UiTheme.Good) : UiKit.Colored(L10n.T("ui.team.no_mod_long"), UiTheme.Warn)) + " · " +
                         (p.IsAiControlled ? UiKit.Colored(L10n.T("ui.team.ai_ctrl"), UiTheme.Info) : UiKit.Colored(L10n.T("ui.team.human_ctrl"), UiTheme.Muted));
            lines.Add(head + "\n" + detail);
        }
        _mates.AppendText(string.Join("\n", lines));
    }

    // ---------------------------------------------------------------- sending

    private ulong SelectedRecipient()
    {
        int i = _to.Selected;
        return i >= 0 && i < _toIds.Count ? _toIds[i] : 0;
    }

    private PotionInfo? SelectedPotion()
    {
        int id = _potion.Selected >= 0 ? _potion.GetItemId(_potion.Selected) : -1;
        return id >= 0 && id < _potions.Count ? _potions[id] : null;
    }

    private CreatureInfo? SelectedEnemy()
    {
        if (_enemy == null || _enemy.Selected < 0) return null;
        int id = _enemy.GetItemId(_enemy.Selected);
        return id >= 0 && id < _enemies.Count ? _enemies[id] : null;
    }

    private void SendChat()
    {
        var text = _input.Text.Trim();
        if (text.Length == 0) return;
        Send(new TeamMessage { Kind = TeamMessageKind.Chat, Text = text });
        _input.Text = "";
        _input.ReleaseFocus();
    }

    private void SendQuick(TeamMessageKind kind)
    {
        // Refresh the pickers first so a stale enemy / potion list is never used.
        RefreshPickers();
        var msg = new TeamMessage { Kind = kind };
        switch (kind)
        {
            case TeamMessageKind.RequestPotion:
                msg.Text = L10n.T("team.q.request_potion");
                break;
            case TeamMessageKind.AskNeedPotion:
                msg.Text = L10n.T("team.q.ask_need_potion");
                break;
            case TeamMessageKind.RequestBlock:
                msg.Text = L10n.T("team.q.request_block");
                break;
            case TeamMessageKind.OfferPotion:
            {
                var potion = SelectedPotion();
                if (potion == null)
                {
                    Hint(L10n.T("ui.team.need_potion"), UiTheme.Warn);
                    return;
                }
                msg.Text = L10n.T("team.q.offer_potion", potion.Name);
                msg.Potion = string.IsNullOrEmpty(potion.Id) ? potion.Name : potion.Id;
                break;
            }
            case TeamMessageKind.RequestFocus:
            {
                var enemy = SelectedEnemy();
                if (enemy == null)
                {
                    Hint(L10n.T("ui.team.need_enemy"), UiTheme.Warn);
                    return;
                }
                msg.Text = L10n.T("team.q.request_focus", enemy.Name);
                msg.Target = EnemyRef(enemy, Snap);
                break;
            }
            case TeamMessageKind.RequestDebuff:
            {
                var enemy = SelectedEnemy();
                var enemyName = enemy?.Name ?? L10n.T("ui.team.any_enemy");
                int d = _debuff.Selected >= 0 ? _debuff.GetItemId(_debuff.Selected) : DebuffAny;
                string? debuffId = d switch
                {
                    DebuffVulnerable => "Vulnerable",
                    DebuffWeak => "Weak",
                    DebuffOther => _debuffOther.Text.Trim(),
                    _ => null,
                };
                if (string.IsNullOrEmpty(debuffId))
                {
                    msg.Text = L10n.T("team.q.request_debuff_any", enemyName);
                }
                else
                {
                    var shown = d switch
                    {
                        DebuffVulnerable => L10n.T("ui.team.debuff.Vulnerable"),
                        DebuffWeak => L10n.T("ui.team.debuff.Weak"),
                        _ => debuffId,
                    };
                    msg.Text = L10n.T("team.q.request_debuff", shown, enemyName);
                    msg.Debuff = debuffId;
                }
                if (enemy != null) msg.Target = EnemyRef(enemy, Snap);
                break;
            }
            default:
                return;
        }
        if (Send(msg))
        {
            Hint(L10n.T("ui.team.sent") + ": " + msg.Text, UiTheme.Muted);
            return;
        }
        // Nobody addressed runs SpirePilot: the closest vanilla emote is all they can see.
        bool emoted = false;
        try { emoted = PilotRuntime.Bridge.TrySignal(kind); }
        catch (Exception) { /* hint below */ }
        Hint(emoted ? L10n.T("ui.team.sent_emote") : L10n.T("team.link_unavailable"), UiTheme.Warn);
    }

    /// <summary>
    /// The enemy as teammates resolve it: name plus the game's combat id, which is the same on every client
    /// ("Cultist (cid:12)"); local keys ("e1") may differ between clients.
    /// </summary>
    private static string EnemyRef(CreatureInfo e, GameSnapshot? snap) =>
        (snap == null ? null : PilotLoop.NormalizeTarget(e.Key, snap)) ?? e.Name;

    /// <summary>Sends to the selected recipient; returns false when nobody addressed can read it (co-op).</summary>
    private bool Send(TeamMessage msg)
    {
        msg.To = SelectedRecipient();
        var entry = PilotRuntime.Team.Send(msg, fromAi: false);
        var snap = Snap;
        if (entry != null && Draft.Team.SpeechBubbles && snap is { IsMultiplayer: true })
            PilotRuntime.Bridge.ShowSpeech(snap.LocalPlayerId, msg.Text);
        RefreshReach();
        return entry is { Delivered: true } || snap is not { IsMultiplayer: true } || Readers(snap, msg.To).Count > 0;
    }

    private void Hint(string text, Color color)
    {
        UiKit.SetText(_quickHint, text);
        UiKit.SetColor(_quickHint, color);
    }
}
