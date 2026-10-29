# SpirePilot MCP 服务器

让任何支持 MCP 的客户端（Claude Desktop、Claude Code、Cursor……）通过 SpirePilot 的本地 HTTP 接口来玩《杀戮尖塔 2》。
外部模型看到的是和内置 AI 一样的紧凑状态文本和**合法操作菜单**（已按你的规则过滤），每次选一个操作执行，执行仍走游戏自己的按钮和同步通道。

## 准备

1. 安装并启动带 SpirePilot 的游戏，按 **F8** 打开面板 → **日志** 页 → 开启 **本地 HTTP 接口**
   （默认端口 17654，只监听本机，地址用 `http://localhost:17654`；可以设置 Token）。
2. 需要 Python 3.10+，推荐 [uv](https://docs.astral.sh/uv/)。依赖只有官方 MCP Python SDK（`mcp==1.30.0`）。

环境变量：

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `SPIREPILOT_URL` | `http://localhost:17654` | Mod 的 HTTP 接口地址（改过端口时设置；请用 `localhost`，Windows 上非管理员运行的游戏只注册了 `localhost`，`127.0.0.1` 会被拒绝） |
| `SPIREPILOT_TOKEN` | 空 | 在 Mod 里设置了 Token 时填同样的值 |

## 安装与配置

**用 uv（推荐，不用手动建环境）**：把下面的 `/path/to/SpirePilot` 换成本仓库的绝对路径。

```bash
# Claude Code
claude mcp add spirepilot -- uv run --directory /path/to/SpirePilot/tools/mcp spirepilot-mcp
# 设置了 Token 时
claude mcp add spirepilot -e SPIREPILOT_TOKEN=你的token -- uv run --directory /path/to/SpirePilot/tools/mcp spirepilot-mcp
```

Claude Desktop（`claude_desktop_config.json`，设置 → 开发者 → 编辑配置）或 Cursor（`~/.cursor/mcp.json`）：

```json
{
  "mcpServers": {
    "spirepilot": {
      "command": "uv",
      "args": ["run", "--directory", "/path/to/SpirePilot/tools/mcp", "spirepilot-mcp"],
      "env": { "SPIREPILOT_URL": "http://localhost:17654", "SPIREPILOT_TOKEN": "" }
    }
  }
}
```

Windows 路径写成 `"C:\\Users\\you\\SpirePilot\\tools\\mcp"`。

**用 pip**：

```bash
python -m venv tools/mcp/.venv
tools/mcp/.venv/bin/pip install ./tools/mcp          # Windows: tools\mcp\.venv\Scripts\pip install .\tools\mcp
claude mcp add spirepilot -- /path/to/SpirePilot/tools/mcp/.venv/bin/spirepilot-mcp
```

Claude Desktop / Cursor 里把 `"command"` 设为 `.venv` 里的 `spirepilot-mcp`（Windows 为 `spirepilot-mcp.exe`），`"args": []`。

## 工具

| 工具 | 作用 |
| --- | --- |
| `status` | 连接状态、内置 AI 的模式 / 状态 / 建议 / token 统计；包与游戏版本不兼容时转述 Mod 的说明，游戏比包新（未经测试）时只提示、不拦截 |
| `set_mode(mode)` | `Off`（你来玩）/ `Advisor`（你来玩，内置 AI 给建议）/ `FullAuto` / `CombatOnly` / `OutOfCombatOnly`；设置文件写不进去时会提醒（模式本次有效，下次启动恢复旧模式） |
| `get_state(format, wait_seconds)` | 紧凑状态文本 + 操作菜单；`format="json"` 返回原始快照；`wait_seconds` 在敌人回合等能操作 |
| `list_actions` | 只看菜单（含操作类型和 semantic key） |
| `act(id \| semantic_key)` | 执行一个操作，返回结果 + 新状态和菜单 |
| `get_prompt(part)` | 内置 AI 此刻会发送的完整提示词（含队友消息、未答复的请求、路线和钉住的路线、一次性指令；和内置 AI 一样不含 3 分钟前的队友消息和 5 分钟前的请求；只读，不会把消息标为已读）；`part="system"` 适合当外部模型的系统提示 / 简报。操作时仍先 `get_state`，再用它菜单里的 id 调 `act` |
| `compendium(kind, query)` | 查卡牌 / 遗物 / 药水 / 能力 / 角色（支持关键词过滤） |
| `give_instruction(text)` | 给内置 AI 一次性指令（它在玩或在顾问模式时才有用） |
| `read_chat` / `send_chat` | 多人模式的队友消息（聊天、要药水、给药水、问要不要药水、要减益、集火、求保护、接受 / 拒绝）。`read_chat` 显示每条消息的 id（`#4f2a9c01d2e3`，回答别的消息时还有 `re #…`）；回答请求用 `send_chat(kind="accept" 或 "decline", reply_to="4f2a9c01d2e3")`，对方的 AI 才会照做。带 `reply_to` 且收件人为默认的 `all` 时，回答发给提问的人（问题是发给所有人的则发给所有人；提议 / 询问药水只发给提问的人）；收件人写错时返回队友名单 |

使用要点：

- 先 `set_mode("Off")` 或 `set_mode("Advisor")` 拿到控制权：内置 AI 在玩（全自动 / 只打战斗 / 只管战斗外）时 `act` 会被拒绝（HTTP 409）。
- 循环：`get_state` → 从菜单里选一个 id → `act(id)`。菜单 id 每执行一步都会重新编号，`act` 执行前会重新读取状态：
  如果这个 id 的含义和你上次看到的菜单不一样，就**不执行**，并返回当前菜单（还会告诉你原来那个操作现在的 id）。
- 开局、选角色、地图、奖励、商店、事件、篝火、选牌、弹窗……全都在菜单里，菜单是唯一的合法操作来源。
- 菜单已经去掉了你的规则不允许的操作（未勾选的角色、进阶、存档角色不允许时的“继续”、不允许给队友用药水）。
  如果规则在模型读完菜单后才改，这个操作会从菜单里消失；`act` 仍按它的 semantic key 交给 Mod 判断，
  Mod 回答 403 `owner_rule`，模型就会被告知是你的规则不允许（而不是局面变了），什么都不执行，并附上当前菜单。
- 包与游戏版本不兼容时（例如在 0.111 的游戏上装了 game0.107 的包），`status` 转述 Mod 的说明（该装哪个包、还没有对应的包，
  或在面板“控制”页打开“仍然运行”），`set_mode` 开不了内置 AI（HTTP 409 `version_mismatch`），`act` 也不执行；
  `set_mode("Off")` 这时什么都不发（本来就是关闭），所以不会改掉你保存的模式；
  你在面板上打开“仍然运行”后一切照常。游戏比最新的包还新时（未经测试），`status` 只多一行警告，不拦截。
- 地图上的状态会列出路线规划器的几条路线和你钉住的路线。钉住的路线对外部模型只是建议（工具说明里会让它照着走），内置 AI 则会自动照着走。
- 没装 SpirePilot 的队友看不到文字，改为看到对应的游戏表情：发给“所有人”而队伍里有人装了、有人没装时，文字发给装了的，
  表情发给没装的，`send_chat` 会说明他们看到了哪个表情（同类表情 3 分钟内刚对他们显示过时不再显示）。收件的队友都没装时不发文字，`send_chat` 说明对方看到的表情，或者什么也没看到
  （聊天没有对应表情；同类表情几分钟内刚对他发过、AI 消息额度用完或游戏暂时显示不了表情）。
  没装 Mod 的队友点赞 / 点踩会以“接受 / 拒绝”出现在 `read_chat` 里（只回答最近一次以表情发给他、还没结束的请求），“!” / “?” 是一行简短消息。
- 你用 `send_chat` 发出的请求、提议和回答会记进内置 AI 的历史，请求会出现在它的“未答复的请求”里；用 `reply_to` 回答过的消息，内置 AI 不会再回答一次。

## 示例会话

```text
你：用 SpirePilot 帮我把这一层打完。
→ status                      mode FullAuto, built-in AI Thinking … The built-in AI is in control
→ set_mode("Off")             mode Off … You can act: get_state, then act(id).
→ get_prompt(part="system")   （读一次内置 AI 的规则与策略）
→ get_state                   Screen: Combat | Act 1 (Overgrowth) | Floor 7 …
                              Threat: incoming 12, unblocked 12
                              Menu: a0 | Play Bash → Nibbit (10 dmg, 2 Vulnerable) … a10 | End turn
→ act(id="a0")                Done: Play Bash → Nibbit …（新状态 + 新菜单）
→ act(id="a2")                Done: Play Strike → Nibbit …
→ act(id="a5")                Done: End turn … WAITING: enemy turn.
→ get_state(wait_seconds=15)  Combat: round 3 (your turn) …
```

## 测试

```bash
python -m venv tools/mcp/.venv
tools/mcp/.venv/bin/pip install -e "./tools/mcp[test]"
tools/mcp/.venv/bin/python -m pytest tools/mcp/tests -q
```

测试会在线程里启动一个模拟 Mod 接口的本地 HTTP 服务器，直接调用每个工具函数（包括 403 `owner_rule`、409、版本不兼容 / 未经测试、
混合队伍的表情和 3 分钟内的重复表情、单人时发给所有人、队友名写错、设置文件写不进去、消息未送达、连接被拒绝等情况）。
状态文本由 Mod 自己生成（`GET /state?include=text`，和内置 AI 用的是同一个 `StateFormatter`），MCP 服务器直接显示它。
Python 版 `format_state()` 只是给没有 `include=text` 的旧版 Mod 用的后备：`tests/fixtures/*.json` 由 Mod 自己的模型类序列化生成，
`*.txt` 是当时的 C# `StateFormatter` 对它们的输出，测试保证后备和那时的格式一致；之后 C# 端新增的内容（药水和意图说明、能力效果行等）后备里没有。

---

## English

An MCP server that lets any MCP client (Claude Desktop, Claude Code, Cursor, ...) play Slay the Spire 2 through
SpirePilot's local HTTP API. The model sees the same compact state text and **legal-action menu** as the built-in pilot
and executes one action at a time through the game's own code paths.

**Setup.** Start the game with SpirePilot, press **F8** → **Log** tab → enable the **local HTTP API** (port 17654,
this computer only, optional bearer token). Python 3.10+; the only dependency is the official MCP Python SDK
(`mcp==1.30.0`). Environment: `SPIREPILOT_URL` (default `http://localhost:17654`; keep `localhost`: on Windows a game
that is not run as administrator registers only `localhost`, so `127.0.0.1` is refused), `SPIREPILOT_TOKEN`.

```bash
# Claude Code (uv)
claude mcp add spirepilot -- uv run --directory /path/to/SpirePilot/tools/mcp spirepilot-mcp
# pip instead of uv
python -m venv tools/mcp/.venv && tools/mcp/.venv/bin/pip install ./tools/mcp
claude mcp add spirepilot -- /path/to/SpirePilot/tools/mcp/.venv/bin/spirepilot-mcp
```

For Claude Desktop (`claude_desktop_config.json`) or Cursor (`~/.cursor/mcp.json`), use the JSON block above
(`"command": "uv"`, `"args": ["run", "--directory", "/path/to/SpirePilot/tools/mcp", "spirepilot-mcp"]`).

**Tools.** `status` (also relays the mod's game-version notice), `set_mode` (warns when the settings file could not be
written: the mode applies now, the next start uses the old one), `get_state` (text or raw JSON, optional
`wait_seconds`), `list_actions`, `act` (by `id` or `semantic_key`; refused while the build is blocked for this game
version), `get_prompt` (exactly what the built-in pilot would send now, inbox, open requests, routes, pin and live
instruction included, with the same expiry: no team messages older than 3 minutes, no requests older than 5; read
without side effects; `part="system"` is a good system prompt for the external model; to act, still call `get_state`
and `act` with an id from its menu), `compendium(kind, query)`, `give_instruction` (for the built-in pilot), `read_chat`
(shows each message's id as `#4f2a9c01d2e3` and the id it answers as `re #…`), `send_chat` (chat, request_potion,
offer_potion, ask_need_potion, request_debuff, request_focus, request_block, and accept / decline with `reply_to` = the
id of the message answered, e.g. `"4f2a9c01d2e3"`, so a teammate's AI acts on the answer; with `reply_to` and the
default recipient `"all"` the answer goes to the asker, or to everyone when the question was for everyone, a potion
offer or question to the asker only; an unknown recipient is refused with the teammates' names).

**How to play.** Take control first with `set_mode("Off")` (or `"Advisor"`); `act` is refused (HTTP 409) while the
built-in pilot plays. Then loop `get_state` → pick one menu id → `act(id)`; `act` returns the new state and menu. Ids
are renumbered after every action, so `act` re-reads the state first and refuses an id whose meaning changed since the
menu you last saw, returning the current menu (and the new id of the action you meant). Everything, from the main menu
to card selections and popups, is in the menu. See the example session above. The menu already leaves out what the
owner's rules forbid (characters, ascension, continuing a saved run of a disallowed character, potions for teammates).
If the owner changes them after the model read the menu, the action is simply gone from the menu; `act` still sends it
by its semantic key so the mod can answer 403 `owner_rule`, and the model is told the owner's rules forbid it (not that
the state changed), with the current menu; nothing is done. When the installed build is incompatible with the game
version (e.g. the game0.107 build on game 0.111), `status` relays the mod's message (the build to install, that none
exists yet, or "Run anyway" on the panel's Control tab), `set_mode` cannot start the built-in pilot (409
`version_mismatch`) and `act` refuses to play, until the owner switches "Run anyway" on. While blocked,
`set_mode("Off")` posts nothing (Off is already in effect), so the owner's saved mode is kept. On a game newer than the
newest build (untested) `status` adds a warning line and nothing is blocked. On the map the state lists the route
planner's paths and the owner's pinned route: advice for the external model, which the built-in pilot follows on its
own. Teammates without SpirePilot cannot read text and see the kind's game emote instead: in a mixed team, also for
"all", the text goes to the teammates with the mod and the emote to the others, and `send_chat` names the emote (the
same kind is not shown to them again within 3 minutes). When nobody addressed runs the mod, no text is sent, and
`send_chat` says which emote they saw or that they saw nothing (chat has no emote; an emote is also skipped when the
same kind went to them a few minutes ago, the AI message budget is spent, or the game cannot show one right now). Their
thumbs up and down show up in `read_chat` as accept and decline of the newest request shown to them as an emote while
it is still open, "!" and "?" as a short chat line. Requests, offers and answers sent with `send_chat` enter the
built-in pilot's history (requests also its open requests), and a message answered with `reply_to` is not answered by
the built-in pilot again.

**Tests.** `python -m pytest tools/mcp/tests -q` (after `pip install -e "./tools/mcp[test]"`) runs every tool against a
fake of the mod's API, including the 403 `owner_rule`, 409, incompatible and untested game versions, mixed-team emotes
and their 3-minute repeat check, "all" in solo, unknown recipients, an unwritable settings file, undelivered chat and
connection-refused paths. The state text comes
from the mod itself (`GET /state?include=text`, rendered by the pilot's own `StateFormatter`). The Python
`format_state()` is only a fallback for mod builds without `include=text`: the JSON fixtures were serialized by the mod's
own model classes and the `.txt` files are the C# `StateFormatter` output of that time, so the fallback matches that
format and lacks what the C# side added since (potion and intent descriptions, the power-effects line and more).
