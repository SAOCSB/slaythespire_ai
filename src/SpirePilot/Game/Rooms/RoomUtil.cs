using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;
using SpirePilot.Core.Model;
using SpirePilot.Core.Text;
using SpirePilot.Infra;

namespace SpirePilot.Game.Rooms;

/// <summary>
/// Helpers shared by the map / rewards / selection / treasure readers: localized strings of these screens,
/// card / relic label text, node lookup through private fields with node-path fallbacks, and settle helpers.
/// Main thread only.
/// </summary>
internal static class RoomUtil
{
    static RoomUtil()
    {
        // Strings used only by the room readers (the shared act.* labels live in Core/Text/L10n.cs).
        L10n.Add("rooms.cost", "{0}费", "{0} energy");
        L10n.Add("rooms.voted_by", "（{0} 已投此处）", "({0} voted here)");
        L10n.Add("rooms.map_voted", "已投票前往 {0}，等待队友投票。", "You voted for {0}; waiting for teammates.");
        L10n.Add("rooms.map_combat", "战斗进行中，不能选择地图节点。", "Combat is in progress: map choices are disabled.");
        L10n.Add("rooms.map_traveling", "正在前往下一个节点…", "Travelling to the next node…");
        L10n.Add("rooms.map_no_travel", "地图已打开但当前不能移动。", "The map is open but travel is not enabled right now.");
        L10n.Add("rooms.close_map", "关闭地图（返回房间）", "Close the map (back to the room)");
        L10n.Add("rooms.discard_for_reward", "丢弃药水 {0}（药水栏已满，可为奖励「{1}」腾出位置）",
            "Discard potion {0} (belt is full; frees a slot for the reward {1})");
        L10n.Add("rooms.potion_belt_full", "药水栏已满：先丢弃一瓶药水才能领取 {0}，或放弃它。",
            "Potion belt is full: discard a potion first to take {0}, or leave it.");
        L10n.Add("rooms.proceed_forfeit", "继续/离开（放弃 {0} 个未领取的奖励）", "Proceed / leave (forfeits {0} unclaimed reward(s))");
        L10n.Add("rooms.card_reward", "卡牌奖励: {0}", "Card reward: {0}");
        L10n.Add("rooms.rewards_waiting", "已离开奖励界面，等待队友领取完奖励。", "Rewards finished; waiting for teammates to finish theirs.");
        L10n.Add("rooms.skipped", "（已跳过）", "(skipped)");
        L10n.Add("rooms.linked", "（多选一：拿取后其它关联奖励作废）", "(linked: taking it forfeits the linked rewards)");
        L10n.Add("rooms.disabled", "（不可领取）", "(not claimable)");
        L10n.Add("rooms.reroll", "重新随机卡牌奖励（{0}）", "Reroll the card reward ({0})");
        L10n.Add("rooms.card_alt", "卡牌奖励选项: {0}", "Card reward option: {0}");
        L10n.Add("rooms.select_confirm", "选择 {0}（并确认）", "Select {0} (and confirm)");
        L10n.Add("rooms.confirm_count", "确认选择（已选 {0}，需要 {1}）", "Confirm selection ({0} selected, need {1})");
        L10n.Add("rooms.cancel_preview", "返回重新选择（取消预览）", "Back to the selection (cancel preview)");
        L10n.Add("rooms.choose_card", "选择卡牌 {0}", "Choose card {0}");
        L10n.Add("rooms.choose_bundle", "选择卡包: {0}", "Choose pack: {0}");
        L10n.Add("rooms.choose_relic", "选择遗物: {0}", "Choose relic: {0}");
        L10n.Add("rooms.skip", "跳过", "Skip");
        L10n.Add("rooms.skip_relic", "跳过宝箱遗物（离开）", "Skip the chest relic (leave)");
        L10n.Add("rooms.leave_chest", "不开宝箱，直接离开", "Leave without opening the chest");
        L10n.Add("rooms.relic_voted", "已选择遗物 {0}，等待队友。", "You picked {0}; waiting for teammates.");
        L10n.Add("rooms.teammate_pick", "{0} 选了 {1}", "{0} picked {1}");
        L10n.Add("rooms.picked_by", "（{0} 也选了它）", "({0} picked it)");
        L10n.Add("rooms.nothing", "（不选）", "(nothing)");
        L10n.Add("rooms.selection_wait", "选择界面尚未就绪。", "The selection screen is not ready yet.");
        L10n.Add("rooms.chest_wait", "宝箱遗物尚未就绪。", "The chest relics are not ready yet.");
        L10n.Add("rooms.bundle", "卡包 {0}: {1}", "Pack {0}: {1}");
        L10n.Add("rooms.open_map", "打开地图（本房间已结束）", "Open the map (this room is finished)");
        L10n.Add("rooms.map_close_wait", "地图盖在房间上且暂不能移动；{0} 秒后才提供关闭地图。",
            "The map is open over the room with travel disabled; closing it is offered in {0} s.");
        L10n.Add("rooms.map_after_proceed", "已从本房间离开，地图等待队友/游戏开放移动。",
            "You already left this room; the map waits for teammates / the game to enable travel.");
        L10n.Add("rooms.rewards_recover", "离开奖励界面（继续按钮未启用：恢复操作）",
            "Leave the rewards screen (proceed stays disabled: recovery)");
        L10n.Add("rooms.rewards_reopen_skipped", "重新打开被跳过的卡牌奖励（此处不允许跳过）: {0}",
            "Re-open the skipped card reward (skipping is not allowed here): {0}");
        L10n.Add("rooms.chest_opening", "宝箱正在打开…", "The chest is opening…");
    }

    /// <summary><see cref="L10n.T"/> that guarantees this class's strings are registered first.</summary>
    public static string T(string key, params object?[] args) => L10n.T(key, args);

    // ---------------------------------------------------------------- game objects

    /// <summary>The game's current screen context, or null while it is in flux.</summary>
    public static IScreenContext? CurrentScreen()
    {
        try { return ActiveScreenContext.Instance?.GetCurrentScreen(); }
        catch (Exception) { return null; }
    }

    /// <summary>True while a confirmation popup / tutorial modal is open.</summary>
    public static bool ModalOpen()
    {
        try { return NModalContainer.Instance?.OpenModal is Node n && SceneUtil.Alive(n); }
        catch (Exception) { return false; }
    }

    /// <summary>True when <paramref name="screen"/> is no longer the screen the game shows (closed, replaced or covered).</summary>
    public static bool Left(Node? screen) => !SceneUtil.Alive(screen) || !ReferenceEquals(CurrentScreen(), screen);

    public static RunState? Run()
    {
        try { return RunManager.Instance is { IsInProgress: true } rm ? rm.DebugOnlyGetState() : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The local player of the current run (fresh lookup, for executors).</summary>
    public static Player? Me()
    {
        try { return Run() is { } run ? LocalContext.GetMe((IPlayerCollection)run) : null; }
        catch (Exception) { return null; }
    }

    public static bool CombatInProgress()
    {
        try { return CombatManager.Instance is { IsInProgress: true }; }
        catch (Exception) { return false; }
    }

    /// <summary>True in a real co-op session (host or client).</summary>
    public static bool IsMultiplayer()
    {
        try { return RunManager.Instance?.NetService?.Type is NetGameType.Host or NetGameType.Client; }
        catch (Exception) { return false; }
    }

    /// <summary>Display name of a player (Steam persona / LAN name), falling back to the character name.</summary>
    public static string PlayerName(Player player)
    {
        try
        {
            var ns = RunManager.Instance?.NetService;
            if (ns != null)
            {
                var name = PlatformUtil.GetPlayerName(ns.Platform, player.NetId);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Exception) { /* platform not ready */ }
        try
        {
            var character = ModelText.Loc(player.Character?.Title);
            if (character.Length > 0) return character;
        }
        catch (Exception) { }
        return player.NetId.ToString();
    }

    /// <summary>Teammates of the local player in slot order (empty in single player).</summary>
    public static List<Player> Teammates(RunState? run, Player? me)
    {
        var list = new List<Player>();
        if (run == null) return list;
        try
        {
            foreach (var p in run.Players)
                if (p != null && (me == null || p.NetId != me.NetId)) list.Add(p);
        }
        catch (Exception) { }
        return list;
    }

    /// <summary>
    /// A node referenced by a (usually private) field of <paramref name="owner"/>, else the first node found at one of
    /// <paramref name="paths"/> (unique-name paths like "%Confirm" come from the reference mods and are unverified).
    /// </summary>
    public static T? NodeOf<T>(Node? owner, string? field, params string[] paths) where T : Node
    {
        if (!SceneUtil.Alive(owner)) return null;
        if (field != null)
        {
            var viaField = Reflect.Get<T>(owner, field);
            if (SceneUtil.Valid(viaField)) return viaField;
        }
        foreach (var path in paths)
        {
            var viaPath = SceneUtil.Get<T>(owner, path);
            if (SceneUtil.Valid(viaPath)) return viaPath;
        }
        return null;
    }

    /// <summary>
    /// <see cref="NodeOf{T}"/> for fields that only some screen types or game versions have: an absent field is not
    /// reported as a missing member (see <see cref="Reflect.GetOptional{T}"/>).
    /// </summary>
    public static T? OptionalNodeOf<T>(Node? owner, string? field, params string[] paths) where T : Node
    {
        if (!SceneUtil.Alive(owner)) return null;
        if (field != null)
        {
            var viaField = Reflect.GetOptional<T>(owner, field);
            if (SceneUtil.Valid(viaField)) return viaField;
        }
        foreach (var path in paths)
        {
            var viaPath = SceneUtil.Get<T>(owner, path);
            if (SceneUtil.Valid(viaPath)) return viaPath;
        }
        return null;
    }

    /// <summary><see cref="UsableButton{T}"/> with optional (quiet) field lookups.</summary>
    public static T? OptionalUsableButton<T>(Node? owner, IEnumerable<string> fields, params string[] paths) where T : NClickableControl
    {
        if (!SceneUtil.Alive(owner)) return null;
        foreach (var f in fields)
        {
            var b = OptionalNodeOf<T>(owner, f);
            if (SceneUtil.Usable(b)) return b;
        }
        foreach (var p in paths)
        {
            var b = OptionalNodeOf<T>(owner, null, p);
            if (SceneUtil.Usable(b)) return b;
        }
        return null;
    }

    /// <summary>
    /// Visible in the tree and not faded out: the product of the node's and its ancestors' <c>Modulate.A</c> is above
    /// 1 %. Fade-out tweens leave overlays "visible" at alpha 0.
    /// </summary>
    public static bool Shown(CanvasItem? item)
    {
        if (!SceneUtil.VisibleInTree(item)) return false;
        try
        {
            float alpha = 1f;
            Node? n = item;
            for (int depth = 0; n != null && depth < 32; depth++, n = n.GetParent())
            {
                if (n is CanvasItem ci) alpha *= ci.Modulate.A;
                else break; // a CanvasLayer / Viewport ends the modulate chain
                if (alpha <= 0.01f) return false;
            }
            return alpha > 0.01f;
        }
        catch (Exception) { return true; }
    }

    /// <summary>True in a real co-op run: a host / client session with more than one player.</summary>
    public static bool MultiplayerRun()
    {
        if (!IsMultiplayer()) return false;
        try { return Run() is { } run && run.Players.Count > 1; }
        catch (Exception) { return false; }
    }

    /// <summary>The first usable (visible + enabled) button among the given fields / paths.</summary>
    public static T? UsableButton<T>(Node? owner, string[] fields, params string[] paths) where T : NClickableControl
    {
        if (!SceneUtil.Alive(owner)) return null;
        foreach (var f in fields)
        {
            var b = NodeOf<T>(owner, f);
            if (SceneUtil.Usable(b)) return b;
        }
        foreach (var p in paths)
        {
            var b = NodeOf<T>(owner, null, p);
            if (SceneUtil.Usable(b)) return b;
        }
        return null;
    }

    /// <summary>
    /// Whether the private TaskCompletionSource field <paramref name="field"/> of a selection screen has completed
    /// (the game accepted the player's choice). Read without calling the screen's own accessor, so nothing changes.
    /// </summary>
    public static bool CompletionDone(object? owner, string field = "_completionSource")
    {
        try
        {
            var tcs = Reflect.Get<object>(owner, field);
            if (tcs == null) return false;
            var task = tcs.GetType().GetProperty("Task")?.GetValue(tcs) as Task;
            return task?.IsCompleted == true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>True while the game waits for another decision of the local player (a selection overlay, hand selection or modal).</summary>
    public static bool AwaitingPlayerInput()
    {
        if (ModalOpen()) return true;
        var cur = CurrentScreen();
        if (cur is NCardGridSelectionScreen or NChooseACardSelectionScreen or NChooseABundleSelectionScreen or NChooseARelicSelection
            or NCardRewardSelectionScreen or NRewardsScreen)
            return true;
        return ScreenResolver.IsHandSelection();
    }

    /// <summary>
    /// Like <see cref="UiActions.WaitSettled"/>, but also stops waiting when the game asks the player for the next choice
    /// (a choice inside a running action keeps the action executor busy until it is answered).
    /// </summary>
    public static async Task SettleAsync(CancellationToken ct)
    {
        await MainThread.Frames(2);
        await MainThread.WaitUntil(() => !UiActions.GameBusy() || AwaitingPlayerInput(), UiActions.DefaultTimeout, ct);
        await MainThread.Frames(2);
    }

    // ---------------------------------------------------------------- text

    /// <summary>"STRIKE_IRONCLAD" or "STRIKE_IRONCLAD+1" (upgrade level), as used in semantic keys.</summary>
    public static string CardKey(CardModel? card)
    {
        var id = ModelText.SafeId(card);
        try
        {
            int level = card?.CurrentUpgradeLevel ?? 0;
            return level > 0 ? id + "+" + level : id;
        }
        catch (Exception) { return id; }
    }

    /// <summary>Snapshot data of a card, never throwing.</summary>
    public static CardInfo CardInfo(CardModel card, string key, bool inCombat)
    {
        try { return ModelText.Card(card, key, inCombat); }
        catch (Exception)
        {
            string name = "";
            try { name = card.Title ?? ""; } catch (Exception) { }
            return new CardInfo { Key = key, Id = ModelText.SafeId(card), Name = name };
        }
    }

    public static RelicInfo RelicInfo(RelicModel relic)
    {
        try { return ModelText.Relic(relic); }
        catch (Exception) { return new RelicInfo { Id = ModelText.SafeId(relic) }; }
    }

    /// <summary>Card name with upgrade marker, e.g. "Bash+".</summary>
    public static string CardName(CardInfo c)
    {
        var name = c.Name.Length > 0 ? c.Name : c.Id;
        return c.Upgraded && !name.EndsWith('+') ? name + "+" : name;
    }

    /// <summary>One-line card text for labels: "Bash+ [2 energy, Attack, Basic]: Deal 10 damage. Apply 3 Vulnerable."</summary>
    public static string CardText(CardInfo c, int maxDescription = 180)
    {
        var sb = new StringBuilder(CardName(c));
        var meta = new List<string>();
        if (c.Cost is { } cost) meta.Add(T("rooms.cost", cost < 0 ? "X" : cost.ToString()));
        if (c.StarCost is > 0) meta.Add(c.StarCost + "★");
        if (c.Type.Length > 0) meta.Add(c.Type);
        if (c.Rarity.Length > 0) meta.Add(c.Rarity);
        if (meta.Count > 0) sb.Append(" [").Append(string.Join(", ", meta)).Append(']');
        var desc = ModelText.Clean(c.Description);
        if (desc.Length > 0) sb.Append(": ").Append(Trim(desc, maxDescription));
        return sb.ToString();
    }

    /// <summary>"Anchor: Start each combat with 10 Block."</summary>
    public static string RelicText(RelicInfo r, int maxDescription = 180)
    {
        var name = r.Name.Length > 0 ? r.Name : r.Id;
        var desc = ModelText.Clean(r.Description);
        return desc.Length > 0 ? name + ": " + Trim(desc, maxDescription) : name;
    }

    public static string Trim(string text, int max) => text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";

    /// <summary>Text of a Godot label node (Label or RichTextLabel), cleaned of markup; "" when unavailable.</summary>
    public static string LabelText(Node? node)
    {
        try
        {
            return node switch
            {
                Label l when SceneUtil.Valid(l) => ModelText.Clean(l.Text),
                RichTextLabel r when SceneUtil.Valid(r) => ModelText.Clean(r.Text),
                _ => "",
            };
        }
        catch (Exception) { return ""; }
    }

    /// <summary>Banner text of a choose-one overlay (NCommonBanner.label), "" when unavailable.</summary>
    public static string BannerText(Node? screen)
    {
        try
        {
            var banner = OptionalNodeOf<NCommonBanner>(screen, "_banner", "%Banner", "Banner");
            return banner?.label is { } label ? LabelText(label) : "";
        }
        catch (Exception) { return ""; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GodotObject, object> FirstSeen = new();

    /// <summary>How long ago a reader / executor first saw this node (zero on first sight). For "still animating in" guards.</summary>
    public static TimeSpan Age(GodotObject? node)
    {
        if (node == null) return TimeSpan.Zero;
        try
        {
            var first = (long)FirstSeen.GetValue(node, _ => System.Diagnostics.Stopwatch.GetTimestamp());
            return System.Diagnostics.Stopwatch.GetElapsedTime(first);
        }
        catch (Exception) { return TimeSpan.Zero; }
    }

    /// <summary>
    /// Card reward / choose-a-card overlays ignore clicks for a short time after opening (private <c>_noSelectionTimeMsec</c>,
    /// value unknown). Waits until the overlay has been up for a moment and the holder's hitbox is enabled.
    /// </summary>
    public static async Task WaitClickable(Node screen, NCardHolder holder, CancellationToken ct)
    {
        var guard = TimeSpan.FromMilliseconds(600) - Age(screen);
        if (guard > TimeSpan.Zero) await MainThread.Delay(guard, ct);
        await MainThread.WaitUntil(() => !SceneUtil.Alive(holder) || holder.Hitbox == null || holder.Hitbox.IsEnabled, TimeSpan.FromSeconds(2), ct);
    }

    /// <summary>Adds <paramref name="note"/> once.</summary>
    public static void Note(GameSnapshot snap, string note)
    {
        if (!snap.Notes.Contains(note)) snap.Notes.Add(note);
    }
}
