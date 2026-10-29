using System.Globalization;

namespace SpirePilot.Core.Text;

/// <summary>
/// Tiny string table for the mod's own UI and messages (Simplified Chinese and English).
/// Game content (card names, descriptions) is already localized by the game itself.
/// </summary>
public static class L10n
{
    /// <summary>"zh" or "en".</summary>
    public static string Language { get; private set; } = "zh";

    public static bool IsChinese => Language == "zh";

    /// <summary>
    /// Resolves the configured language. <paramref name="setting"/> is "auto", "zh" or "en";
    /// <paramref name="gameLanguage"/> is the game's language code (e.g. "zhs", "zht", "eng"), when known.
    /// </summary>
    public static void Configure(string? setting, string? gameLanguage)
    {
        var s = (setting ?? "auto").Trim().ToLowerInvariant();
        if (s is "zh" or "en")
        {
            Language = s;
            return;
        }
        var g = (gameLanguage ?? "").Trim().ToLowerInvariant();
        if (g.Length > 0)
        {
            Language = g.StartsWith("zh") || g is "zhs" or "zht" or "chs" or "cht" ? "zh" : "en";
            return;
        }
        Language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh" : "en";
    }

    /// <summary>Looks up <paramref name="key"/> and formats it with <paramref name="args"/>. Unknown keys return the key.</summary>
    public static string T(string key, params object?[] args)
    {
        string? template = null;
        if (Strings.TryGetValue(key, out var pair)) template = IsChinese ? pair.zh : pair.en;
        template ??= key;
        if (args.Length == 0) return template;
        try { return string.Format(CultureInfo.InvariantCulture, template, args); }
        catch (FormatException) { return template; }
    }

    /// <summary>Registers or overrides strings (the mod adds UI strings at startup).</summary>
    public static void Add(string key, string zh, string en) => Strings[key] = (zh, en);

    public static bool Has(string key) => Strings.ContainsKey(key);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string zh, string en)> Strings = new()
    {
        // ---- action labels (shown to the LLM and in the UI) ----
        ["act.play"] = ("打出 {0}", "Play {0}"),
        ["act.play_target"] = ("打出 {0} → {1}", "Play {0} → {1}"),
        ["act.end_turn"] = ("结束回合", "End turn"),
        ["act.use_potion"] = ("使用药水 {0}", "Use potion {0}"),
        ["act.use_potion_target"] = ("使用药水 {0} → {1}", "Use potion {0} → {1}"),
        ["act.discard_potion"] = ("丢弃药水 {0}", "Discard potion {0}"),
        ["act.select_card"] = ("选择 {0}", "Select {0}"),
        ["act.deselect_card"] = ("取消选择 {0}", "Deselect {0}"),
        ["act.confirm"] = ("确认选择", "Confirm selection"),
        ["act.cancel"] = ("取消/跳过", "Cancel / skip"),
        ["act.choose_option"] = ("选择 {0}", "Choose {0}"),
        ["act.map_node"] = ("前往 {0} ({1})", "Travel to {0} ({1})"),
        ["act.claim_reward"] = ("领取奖励: {0}", "Claim reward: {0}"),
        ["act.pick_card"] = ("拿取卡牌 {0}", "Take card {0}"),
        ["act.skip_cards"] = ("跳过卡牌奖励", "Skip card reward"),
        ["act.buy"] = ("购买 {0} ({1} 金币)", "Buy {0} ({1} gold)"),
        ["act.buy_removal"] = ("购买删牌服务 ({0} 金币)", "Buy card removal ({0} gold)"),
        ["act.event_option"] = ("事件选项: {0}", "Event option: {0}"),
        ["act.rest_option"] = ("篝火: {0}", "Rest site: {0}"),
        ["act.open_chest"] = ("打开宝箱", "Open chest"),
        ["act.proceed"] = ("继续/离开", "Proceed / leave"),
        ["act.dismiss"] = ("关闭弹窗: {0}", "Dismiss popup: {0}"),
        ["act.continue_run"] = ("继续存档中的游戏", "Continue saved run"),
        ["act.new_run"] = ("开始新游戏", "Start a new run"),
        ["act.select_character"] = ("选择角色 {0}", "Select character {0}"),
        ["act.set_ascension"] = ("设置进阶 {0}", "Set ascension {0}"),
        ["act.embark"] = ("出发", "Embark"),
        ["act.return_menu"] = ("返回主菜单", "Return to main menu"),

        // ---- map node types ----
        ["node.Monster"] = ("普通怪", "Monster"),
        ["node.Elite"] = ("精英", "Elite"),
        ["node.RestSite"] = ("篝火", "Rest site"),
        ["node.Shop"] = ("商店", "Shop"),
        ["node.Treasure"] = ("宝箱", "Treasure"),
        ["node.Event"] = ("问号事件", "Unknown / event"),
        ["node.Boss"] = ("Boss", "Boss"),
        ["node.Ancient"] = ("先古之民", "Ancient"),
        ["node.Unknown"] = ("未知", "Unknown"),

        // ---- quick team messages ----
        ["team.q.request_potion"] = ("能给我一瓶药水吗？我现在需要支援。", "Could you spare a potion? I need help."),
        ["team.q.request_potion_named"] = ("能给我用一下「{0}」吗？", "Could you use \"{0}\" on me?"),
        ["team.q.ask_need_potion"] = ("有人需要药水吗？我可以给你用。", "Does anyone need a potion? I can use one on you."),
        ["team.q.offer_potion"] = ("我有「{0}」，需要的话我给你用。", "I have \"{0}\" — say the word and I'll use it on you."),
        ["team.q.request_debuff"] = ("能给「{1}」上一层「{0}」吗？", "Can you apply {0} to {1}?"),
        ["team.q.request_debuff_any"] = ("能给「{0}」上点减益（易伤/虚弱）吗？", "Can you debuff {0} (Vulnerable / Weak)?"),
        ["team.q.request_focus"] = ("我们一起集火「{0}」吧。", "Let's focus {0}."),
        ["team.q.request_block"] = ("我这回合扛不住，能帮我挡一下吗？", "I can't survive this turn, can you protect me?"),
        ["team.q.suggest_route"] = ("我建议走这里：{0}", "I suggest we go here: {0}"),
        ["team.q.accept"] = ("好的！", "OK!"),
        ["team.q.decline"] = ("这次不行，抱歉。", "Not this time, sorry."),
        ["team.emote_signal"] = ("{0} 发出了表情 {1}", "{0} signalled {1}"),

        // ---- status / notes ----
        ["status.off"] = ("AI 已关闭", "AI is off"),
        ["status.thinking"] = ("AI 思考中…", "AI is thinking…"),
        ["status.waiting"] = ("等待中: {0}", "Waiting: {0}"),
        ["status.acting"] = ("执行: {0}", "Acting: {0}"),
        ["status.paused"] = ("已暂停", "Paused"),
        ["status.error"] = ("错误: {0}", "Error: {0}"),
        ["status.step_ready"] = ("待执行（按“单步”执行）: {0}", "Ready (press Step): {0}"),
        ["status.advice"] = ("建议: {0}", "Suggestion: {0}"),
        ["status.config_error"] = ("AI 设置有误，已暂停：{0}（请在“API 设置”中修改后点继续）", "AI settings problem, paused: {0} (fix it in the API tab, then resume)"),
        ["status.no_allowed_character"] = ("允许的角色（{0}）都还没解锁，已暂停。请在“角色”页调整。", "None of the allowed characters ({0}) is unlocked; paused. Adjust the Characters tab."),
        ["status.llm_unavailable"] = ("AI 连续多次出错，已暂停：{0}（检查网络/接口后点继续）", "The AI failed several times in a row, paused: {0} (check the connection, then resume)"),
        ["status.saved_run_not_allowed"] = ("存档里的角色（{0}）不在允许列表中：AI 不会继续这局，请手动处理存档", "The saved run's character ({0}) is not allowed: the AI will not continue it; handle the save yourself"),
        ["status.version_mismatch"] = ("这个 SpirePilot 是为游戏 {0} 编译的，当前游戏是 {1}：AI 已关闭。请安装 {2} 版本的 SpirePilot。", "This SpirePilot build is for game {0}, but the game is {1}: the AI is off. Install the {2} build of SpirePilot."),
        ["status.stuck"] = ("同一操作重复执行但游戏没有变化，已暂停：{0}", "Same action repeated without effect, paused: {0}"),
        ["ui.run_started"] = ("AI 开始了新的一局。", "The AI started a new run."),
        ["team.link_unavailable"] = ("队友通信不可用：对方没有安装 SpirePilot，消息只显示在本地。", "Team link unavailable: teammates without SpirePilot can't receive messages (shown locally only)."),
        ["team.rate_limited"] = ("AI 消息过于频繁，已暂缓发送。", "AI messages rate-limited; message dropped."),
    };
}
