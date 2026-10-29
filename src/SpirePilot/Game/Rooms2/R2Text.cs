using System.Globalization;
using SpirePilot.Core.Text;

namespace SpirePilot.Game.Rooms2;

/// <summary>
/// Extra zh/en strings used by the shop / event / rest site / menu / popup readers. Kept in a private read-only
/// table (instead of <see cref="L10n.Add"/>) so worker threads formatting labels never race a registration.
/// Unknown keys fall through to <see cref="L10n.T"/> (e.g. "act.buy").
/// </summary>
internal static class R2Text
{
    public static string T(string key, params object?[] args)
    {
        if (!Strings.TryGetValue(key, out var pair)) return L10n.T(key, args);
        var template = L10n.IsChinese ? pair.zh : pair.en;
        if (args.Length == 0) return template;
        try { return string.Format(CultureInfo.InvariantCulture, template, args); }
        catch (FormatException) { return template; }
    }

    private static readonly Dictionary<string, (string zh, string en)> Strings = new()
    {
        // ---- action labels ----
        ["act.shop_open"] = ("打开商店（查看商品）", "Open the shop inventory"),
        ["act.shop_leave"] = ("离开商店", "Leave the shop"),
        ["act.buy_sale"] = ("购买 {0} ({1} 金币，打折)", "Buy {0} ({1} gold, on sale)"),
        ["act.event_advance"] = ("继续对话", "Continue the dialogue"),
        ["act.event_leave"] = ("离开事件", "Leave the event"),
        ["act.rest_target"] = ("篝火: {0} → {1}", "Rest site: {0} → {1}"),
        ["act.rest_leave"] = ("离开篝火", "Leave the rest site"),
        ["act.popup_next"] = ("下一页: {0}", "Next page: {0}"),
        ["act.reveal_epoch"] = ("揭示纪元: {0}", "Reveal epoch: {0}"),
        ["act.close_epoch"] = ("关闭纪元详情", "Close the epoch details"),
        ["act.timeline_back"] = ("从时间线返回主菜单", "Back from the timeline to the main menu"),
        ["act.gameover_continue"] = ("继续（查看本局结算）", "Continue (run summary)"),
        ["act.crystal_cell"] = ("占卜格子 ({0},{1})", "Divine cell ({0},{1})"),
        ["act.crystal_tool"] = ("切换占卜工具: {0}", "Switch divination tool: {0}"),

        // ---- words ----
        ["word.yes"] = ("是", "Yes"),
        ["word.no"] = ("否", "No"),
        ["word.ok"] = ("确定", "OK"),
        ["word.lethal"] = ("⚠ 会致死", "⚠ LETHAL"),
        ["word.no_slot"] = ("（药水栏已满）", "(no free potion slot)"),
        ["word.tool_big"] = ("大范围", "big"),
        ["word.tool_small"] = ("小范围", "small"),

        // ---- notes (context lines for the AI) ----
        ["note.shop_closed"] = ("商店货架未打开：先“打开商店”才能购买，也可以直接离开。", "The shop inventory is closed: open it to buy, or leave."),
        ["note.shop_transition"] = ("商店界面正在切换，请稍候。", "The shop screen is changing; wait."),
        ["note.shop_dead"] = ("你已阵亡，不能在商店购买。", "You are dead and cannot shop."),
        ["note.shop_unreadable"] = ("无法读取商店货物。", "The shop inventory could not be read."),
        ["note.event_unreadable"] = ("无法读取当前事件。", "The current event could not be read."),
        ["note.event_dialogue"] = ("先古之民正在说话，继续对话后才会出现选项。", "The Ancient is talking; continue the dialogue to reach the choices."),
        ["note.event_no_options"] = ("事件选项尚未出现，请稍候。", "The event options are not shown yet; wait."),
        ["note.event_shared"] = ("这是共享事件：所有玩家投票决定选项。", "Shared event: all players vote on the option."),
        ["note.event_vote"] = ("{0} 投票选择了: {1}", "{0} voted for: {1}"),
        ["note.event_novote"] = ("{0} 还没有投票", "{0} has not voted yet"),
        ["note.event_voted"] = ("你已投票（{0}），等待队友投票。", "You voted ({0}); waiting for teammates."),
        ["note.rest_unreadable"] = ("无法读取篝火选项。", "The rest site options could not be read."),
        ["note.rest_chosen"] = ("你已在此篝火做出选择。", "You already used this rest site."),
        ["note.rest_mate_chosen"] = ("{0} 选择了: {1}", "{0} chose: {1}"),
        ["note.rest_mate_hover"] = ("{0} 正在考虑: {1}", "{0} is considering: {1}"),
        ["note.menu_page"] = ("主菜单的子页面（{0}）由玩家操作，AI 不会动它。", "A main-menu page is open ({0}); it is left to the human."),
        ["note.menu_intro"] = ("开场动画中，请稍候。", "Intro animation; wait."),
        ["note.menu_has_save"] = ("有存档的对局，但“继续”按钮现在不可用。", "A saved run exists but Continue is not available right now."),
        ["note.menu_run_info"] = ("存档: {0}", "Saved run: {0}"),
        ["note.timeline"] = ("时间线界面：揭示新纪元可以解锁新内容。", "Timeline: revealing new epochs unlocks content."),
        ["note.char_ready"] = ("你已准备就绪，等待其他玩家。", "You are ready; waiting for the other players."),
        ["note.char_lobby"] = ("{0}: {1}{2}", "{0}: {1}{2}"),
        ["note.char_ready_tag"] = ("（已准备）", " (ready)"),
        ["note.char_client_asc"] = ("只有房主可以修改进阶等级。", "Only the host can change the ascension level."),
        ["note.gameover_wait"] = ("等待其他玩家……", "Waiting for the other players..."),
        ["note.gameover_continued"] = ("已点击“继续”，等待结算动画结束。", "Continue was pressed; waiting for the summary to finish."),
        ["note.popup"] = ("弹窗: {0}", "Popup: {0}"),
        ["note.popup_over"] = ("（弹窗下方的界面: {0}）", "(screen under the popup: {0})"),
        ["note.popup_human"] = ("这个弹窗需要玩家本人决定，AI 不会操作。", "This popup is a decision for the human; the AI leaves it alone."),
        ["note.popup_destructive"] = ("这是危险操作的确认框，AI 只能取消。", "This confirms a destructive action; the AI may only cancel it."),
        ["note.popup_no_button"] = ("找不到可以点击的弹窗按钮。", "No usable button found on the popup."),
        ["note.crystal"] = ("水晶球占卜：剩余 {0} 次，当前工具 {1}，未揭开 {2} 格（网格 {3}×{4}）。", "Crystal sphere: {0} divinations left, tool {1}, {2} hidden cells (grid {3}x{4})."),
    };
}
