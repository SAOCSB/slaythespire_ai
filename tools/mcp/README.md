# SpirePilot MCP 服务器

让任何支持 MCP 的客户端（Claude Desktop、Claude Code、Cursor……）通过 SpirePilot 的本地 HTTP 接口来玩《杀戮尖塔 2》。
外部模型看到的是和内置 AI 一样的紧凑状态文本和**合法操作菜单**，每次选一个操作执行，执行仍走游戏自己的按钮和同步通道。

## 准备

1. 安装并启动带 SpirePilot 的游戏，按 **F8** 打开面板 → **日志** 页 → 开启 **本地 HTTP 接口**
   （默认端口 17654，只监听 127.0.0.1；可以设置 Token）。
2. 需要 Python 3.10+，推荐 [uv](https://docs.astral.sh/uv/)。依赖只有官方 MCP Python SDK（`mcp==1.30.0`）。

环境变量：

| 变量 | 默认值 | 说明 |
| --- | --- | --- |
| `SPIREPILOT_URL` | `http://127.0.0.1:17654` | Mod 的 HTTP 接口地址（改过端口时设置） |
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
      "env": { "SPIREPILOT_URL": "http://127.0.0.1:17654", "SPIREPILOT_TOKEN": "" }
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
| `status` | 连接状态、内置 AI 的模式 / 状态 / 建议 / token 统计 |
| `set_mode(mode)` | `Off`（你来玩）/ `Advisor`（你来玩，内置 AI 给建议）/ `FullAuto` / `CombatOnly` / `OutOfCombatOnly` |
| `get_state(format, wait_seconds)` | 紧凑状态文本 + 操作菜单；`format="json"` 返回原始快照；`wait_seconds` 在敌人回合等能操作 |
| `list_actions` | 只看菜单（含操作类型和 semantic key） |
| `act(id \| semantic_key)` | 执行一个操作，返回结果 + 新状态和菜单 |
| `get_prompt(part)` | 内置 AI 的提示词；`part="system"` 适合当外部模型的系统提示 / 简报 |
| `compendium(kind, query)` | 查卡牌 / 遗物 / 药水 / 能力 / 角色（支持关键词过滤） |
| `give_instruction(text)` | 给内置 AI 一次性指令（它在玩或在顾问模式时才有用） |
| `read_chat` / `send_chat` | 多人模式的队友消息（聊天、要药水、给药水、问要不要药水、要减益、集火、求保护） |

使用要点：

- 先 `set_mode("Off")` 或 `set_mode("Advisor")` 拿到控制权：内置 AI 在玩（全自动 / 只打战斗 / 只管战斗外）时 `act` 会被拒绝（HTTP 409）。
- 循环：`get_state` → 从菜单里选一个 id → `act(id)`。菜单 id 每执行一步都会重新编号，`act` 执行前会重新读取状态：
  如果这个 id 的含义和你上次看到的菜单不一样，就**不执行**，并返回当前菜单（还会告诉你原来那个操作现在的 id）。
- 开局、选角色、地图、奖励、商店、事件、篝火、选牌、弹窗……全都在菜单里，菜单是唯一的合法操作来源。

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

测试会在线程里启动一个模拟 Mod 接口的本地 HTTP 服务器，直接调用每个工具函数（包括 409、连接被拒绝等情况）。
`tests/fixtures/*.json` 由 Mod 自己的模型类按 HTTP 接口的 JSON 选项序列化生成，`*.txt` 是 C# `StateFormatter` 对它们的输出，
用来保证 Python 版的状态文本和内置 AI 看到的完全一致。

---

## English

An MCP server that lets any MCP client (Claude Desktop, Claude Code, Cursor, ...) play Slay the Spire 2 through
SpirePilot's local HTTP API. The model sees the same compact state text and **legal-action menu** as the built-in pilot
and executes one action at a time through the game's own code paths.

**Setup.** Start the game with SpirePilot, press **F8** → **Log** tab → enable the **local HTTP API** (port 17654,
127.0.0.1 only, optional bearer token). Python 3.10+; the only dependency is the official MCP Python SDK
(`mcp==1.30.0`). Environment: `SPIREPILOT_URL` (default `http://127.0.0.1:17654`), `SPIREPILOT_TOKEN`.

```bash
# Claude Code (uv)
claude mcp add spirepilot -- uv run --directory /path/to/SpirePilot/tools/mcp spirepilot-mcp
# pip instead of uv
python -m venv tools/mcp/.venv && tools/mcp/.venv/bin/pip install ./tools/mcp
claude mcp add spirepilot -- /path/to/SpirePilot/tools/mcp/.venv/bin/spirepilot-mcp
```

For Claude Desktop (`claude_desktop_config.json`) or Cursor (`~/.cursor/mcp.json`), use the JSON block above
(`"command": "uv"`, `"args": ["run", "--directory", "/path/to/SpirePilot/tools/mcp", "spirepilot-mcp"]`).

**Tools.** `status`, `set_mode`, `get_state` (text or raw JSON, optional `wait_seconds`), `list_actions`, `act`
(by `id` or `semantic_key`), `get_prompt` (`part="system"` is a good system prompt for the external model),
`compendium(kind, query)`, `give_instruction` (for the built-in pilot), `read_chat`, `send_chat` (chat, request_potion,
offer_potion, ask_need_potion, request_debuff, request_focus, request_block).

**How to play.** Take control first with `set_mode("Off")` (or `"Advisor"`); `act` is refused (HTTP 409) while the
built-in pilot plays. Then loop `get_state` → pick one menu id → `act(id)`; `act` returns the new state and menu.
Ids are renumbered after every action, so `act` re-reads the state first and refuses an id whose meaning changed
since the menu you last saw, returning the current menu (and the new id of the action you meant). Everything,
from the main menu to card selections and popups, is in the menu. See the example session above.

**Tests.** `python -m pytest tools/mcp/tests -q` (after `pip install -e "./tools/mcp[test]"`) runs every tool against a
fake of the mod's API, including the 409 and connection-refused paths. The JSON fixtures were serialized by the mod's
own model classes, and the `.txt` files are the C# `StateFormatter` output for them, so the Python port renders
exactly what the built-in pilot sees.
