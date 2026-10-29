# SpirePilot 离线决策基准 / Offline decision benchmark

[中文](#中文) · [English](#english)

---

## 中文

`tools/SpirePilot.Bench` 把一组手写的游戏快照（fixture）原样送进模组里的 `DecisionEngine`：同样的系统提示词、
同样的主人设置块和角色指南、同样的状态文本和菜单、同样的 LLM 客户端（`LlmClientFactory`）、同样的 JSON 解析和修复回合，
然后给模型的回答打分。不需要启动游戏，适合：

- 修改提示词 / 状态格式之后做回归测试；
- 用自己的 API Key 比较不同服务商、模型、推理强度（谁更会算斩杀、谁会乱回复队友）；
- 复现实战里 AI 犯过的错误，修好后防止再犯。

每个 fixture 都用全新的 `AgentMemory` 和 `TeamChatHub`，互不影响。

### 快速开始

```bash
# 只检查 fixture 文件（不需要 Key）
dotnet run --project tools/SpirePilot.Bench -- --validate

# 打印将要发给模型的提示词，不调用任何 API（不需要 Key）；规则类 fixture 照常打分
dotnet run --project tools/SpirePilot.Bench -- --dry-run --filter coop-potion

# 用模组自己的 settings.json（使用其中当前启用的 AI 配置）
dotnet run --project tools/SpirePilot.Bench -- --settings "%APPDATA%\SlayTheSpire2\mod_configs\SpirePilot\settings.json"

# 或者只写一个配置文件，每家服务商一个
dotnet run --project tools/SpirePilot.Bench -- --settings deepseek.json --repeat 3 --out bench-deepseek.md
```

单个配置文件（字段与模组设置里的 AI 配置相同，`apiKey` 可以写成 `env:变量名`，避免明文）：

```json
{ "provider": "OpenAiCompatible", "baseUrl": "https://api.deepseek.com/v1", "model": "deepseek-chat", "apiKey": "env:DEEPSEEK_API_KEY" }
```

```json
{ "provider": "Anthropic", "baseUrl": "https://api.anthropic.com", "model": "claude-opus-5-5", "apiKey": "env:ANTHROPIC_API_KEY",
  "reasoningEffort": "medium", "combatReasoningEffort": "low", "maxTokens": 16000 }
```

```json
{ "provider": "OpenAiCompatible", "baseUrl": "http://127.0.0.1:11434/v1", "model": "qwen2.5:14b", "jsonMode": false }
```

### 参数

| 参数 | 说明 |
| --- | --- |
| `--settings <文件>` | 模组的 `settings.json`，或单个 AI 配置 JSON。除 `--dry-run` / `--validate` 外必填 |
| `--profile <名字>` | 使用 settings.json 里指定名字的配置（默认用当前启用的） |
| `--keep-settings` | 同时使用文件里的路线 / 打法 / 队友设置。默认不用（全部取默认值），这样不同机器的结果可比；fixture 自己的设置覆盖始终生效 |
| `--fixtures <目录>` | fixture 目录，默认 `tools/SpirePilot.Bench/fixtures` |
| `--filter <文本>` | 只跑 id、文件名或标签包含该文本的 fixture；可重复或用逗号分隔，如 `--filter coop,zh` |
| `--repeat <n>` | 每个 fixture 跑 n 次（模型不是确定性的，比较模型时建议 3 次） |
| `--out <文件.md>` | 另外写一份 Markdown 报告（含失败项的模型思路和具体问题） |
| `--dry-run` | 只打印提示词，不发请求 |
| `--validate` | 只检查 fixture 文件 |

退出码：0 全部通过；1 有 fixture 未通过或文件无效；2 参数 / 配置错误（例如 Key 无效时会立即停止，不会白跑全部用例）。

### 结果怎么看

| 结果 | 含义 |
| --- | --- |
| `PASS` | actions[0] 在 `expect_any` 中，且其他要求（消息、计划长度、调用次数）都满足 |
| `FAIL` | 没选期望的动作，或缺少要求的队友回复 / 计划太长 / 需要修复回合等 |
| `FORBIDDEN` | 选了 `forbid` 里的动作（明确的错误，例如会被打死时结束回合） |
| `ERROR` | 没有得到可用决策（网络错误、两次都无法解析）。基准测试中禁用了规则兜底，评的是模型本身 |
| `FLAKY 2/3` | 多次运行结果不一致 |
| `DRY` | `--dry-run` 下未调用模型 |

表格里还有平均延迟和每次的输入 / 输出 token（含修复回合和重试），合计行给出缓存命中的 token。
比较服务商时请固定 fixture、`--repeat 3`、每个模型一份 `--out` 报告。

### Fixture 格式

每个 `fixtures/*.json` 是一个对象。外层字段用 snake_case，`snapshot` 与模组 HTTP 接口 `GET /state` 的输出完全相同
（camelCase、枚举为字符串；读取时大小写不敏感，允许注释和尾逗号）。

| 字段 | 说明 |
| --- | --- |
| `id` | 唯一名字，默认取文件名 |
| `rationale` | 一句话说明为什么期望的答案是对的（必填，报告里会显示） |
| `tags` | 标签，用于 `--filter`，如 `combat`、`coop`、`ironclad`、`zh` |
| `language` | `zh` / `en`：要求 AI 用哪种语言写队友消息 |
| `settings` | 部分 PilotSettings，合并到基础设置上（如 `{"route":{"preset":"Aggressive"}}`、`{"team":{"allowGivingPotions":false}}`）；不能覆盖 `profiles` |
| `live_instruction` | 主人在面板里输入的一次性指令 |
| `memo` / `history` | AI 之前的备忘和最近操作记录 |
| `inbox` | 队友消息（`id`、`kind` 如 `request_potion`、`from`、`to`、`fromName`、`fromAi`、`text`、`potion`、`debuff`、`target`、`replyTo`） |
| `expect_any` | 可接受的 actions[0]：菜单 id（`a3`）或语义键（`play:BASH+0@E:NIBBIT#0`），末尾 `*` 表示前缀（`event:1:*`）。为空表示不限 |
| `forbid` | 不能作为 actions[0] 的动作 |
| `expect_message_kind` | 必须发出这类消息之一（`accept` / `decline` / `offer_potion` ...） |
| `forbid_message_kind` | 不能发出这类消息 |
| `expect_reply_to` | 上述消息必须回复哪条收件箱消息（`reply_to`） |
| `max_messages` | 最多发几条队友消息（`0` = 不该说话） |
| `max_plan_len` | 回答里最多几个动作 id |
| `max_llm_calls` | 最多几次模型调用；`1` 表示第一次回答就必须有效（不能靠修复回合） |
| `expect_source` | 默认 `Llm`。设为 `Rule`（或 `Trivial`）表示引擎应自己按规则决定、**不能**调用模型（例如固定路线、角色限制） |
| `snapshot` | 游戏快照（`GameSnapshot`），菜单 id 必须是按顺序的 `a0..aN` |

最小例子：

```json
{
  "id": "combat-example",
  "rationale": "两张打击正好斩杀最后一个敌人，先防御会浪费斩杀所需的能量。",
  "tags": ["combat", "lethal", "ironclad", "zh"],
  "language": "zh",
  "expect_any": ["play:STRIKE_IRONCLAD+0@E:NIBBIT#0"],
  "forbid": ["play:DEFEND_IRONCLAD+0@-", "end_turn"],
  "snapshot": { "screen": "Combat", "localPlayerId": 1, "players": [ ... ], "combat": { ... }, "actions": [ ... ] }
}
```

### 添加 fixture

1. **从游戏里拿真实快照（推荐）**：在模组“日志”页开启本地 HTTP 接口，停在想要的决策点（可以把 AI 设为顾问模式），然后
   ```bash
   curl -s http://127.0.0.1:17654/state  -H "Authorization: Bearer <token>" > snap.json   # 没设 token 可省略 -H
   curl -s http://127.0.0.1:17654/prompt -H "Authorization: Bearer <token>"               # 看 AI 实际看到的提示词
   ```
   把 `snap.json` 的内容整体放进 `"snapshot"`（多出来的 `localPlayer`、`teammates` 会被忽略）。
2. **从决策记录找题目**：`mod_configs/SpirePilot/traces/trace-日期.jsonl` 记录了每一步给出的选项、AI 的选择和思路。
   用它找到 AI 犯错的地方，再在游戏里重现并用 `/state` 截取（trace 本身不含完整快照）。
3. 写好 `expect_any` / `forbid` 和一句 `rationale`。只把**明确**的错误放进 `forbid`；有多种合理答案时都放进 `expect_any`。
4. 自己算一遍数字（伤害、格挡、毒、灾厄），保证标准答案无争议。
5. 不要把正确答案总放在 `a0`：真实手牌和药水栏顺序是随机的，“总选第一个”的模型不应该得分。
6. 运行 `--validate` 和 `dotnet test tests/SpirePilot.Bench.Tests`：测试会检查每个 fixture 能被解析、期望键都在菜单里、
   id 连续，并用“理想回答”跑一遍真实引擎，确认这个 fixture 确实能通过、而错误回答会被判为 FORBIDDEN。

中文和英文名称都可以用（游戏显示的是本地化名称）；语义键里的 id 始终是英文模型 id（`BASH`、`NIBBIT`）。

### 当前 fixture

| fixture | 考察点 |
| --- | --- |
| `combat-must-block-or-potion` | 结束回合会死：必须防御 / 喝格挡药水 |
| `combat-lethal-last-enemy` | 两张打击斩杀最后一个敌人 |
| `combat-vulnerable-before-multihit` | 先痛击上易伤，再打双重打击 |
| `combat-no-block-vs-buff-intent`（中文） | 敌人不攻击时不防御 |
| `combat-boss-potion-low-hp`（中文） | Boss 战低血：火焰药水直接斩杀 |
| `combat-keep-heal-potion-high-hp` | 高血量不喝回血药水 |
| `combat-kill-attacker-over-block`（中文） | 杀掉攻击者比防御更有效 |
| `cardreward-skip-lean-deck` | 成型牌组，跳过卡牌奖励 |
| `cardreward-clear-pick`（中文） | 初始牌组明显该拿燃烧 |
| `map-low-hp-avoid-elite`（中文） | 低血量避开精英 |
| `map-aggressive-take-elite` | 激进路线 + 满血：打精英 |
| `rest-heal-before-boss` / `rest-smith-healthy`（中文） | 篝火：Boss 前回血 / 血量健康时锻造 |
| `shop-remove-curse`（中文） | 商店优先删诅咒 |
| `shop-unaffordable-not-in-menu` | 买不起的东西不在菜单里，第一次就要给出合法 id |
| `event-avoid-maxhp-loss`（中文） | 不为少量金币损失最大生命值 |
| `coop-potion-request-accept`（中文） | 队友要药水：对队友使用 AnyPlayer 药水并回复 accept |
| `coop-debuff-request-bash` | 队友要易伤：对指定敌人打痛击并 accept |
| `coop-protect-low-hp-teammate` | 队友低血将吃大招：主动给他用格挡药水 |
| `coop-no-reply-to-ai-accept` | 不回复另一个 AI 的 accept（防止无限对话） |
| `coop-owner-forbids-potion-giving`（中文） | 主人禁止给药水：decline 而不是答应 |
| `owner-instruction-overrides-pin`（中文） | 主人临时指令优先于固定路线（必须交给模型） |
| `rule-pinned-route` | 固定路线由规则执行，不调用模型 |
| `rule-character-policy` | 角色限制由规则执行，不调用模型 |
| `necrobinder-doom-lethal` | 亡灵契约师：灾厄斩杀 |
| `regent-spend-stars` | 储君：花星星打群伤收尾，不囤星 |
| `defect-evoke-lethal` | 故障机器人：双重释放激发闪电斩杀 |
| `silent-poison-then-catalyst`（中文） | 静默猎手：先上毒再催化剂 |

---

## English

`tools/SpirePilot.Bench` replays hand-written game snapshots (fixtures) through the mod's own `DecisionEngine`. That
means the same system prompt, owner block and character guide, the same state text and menu, the same LLM clients
(`LlmClientFactory`), and the same JSON parsing and repair turn. It then scores the model's answer. The game is not
needed. Use it to:

- regression-test prompt or state-format changes;
- compare providers, models and reasoning efforts with your own API key (who finds lethal, who chats back at teammates);
- turn mistakes seen in real runs into permanent regression cases.

Every fixture runs with a fresh `AgentMemory` and `TeamChatHub`.

### Quick start

```bash
dotnet run --project tools/SpirePilot.Bench -- --validate                          # check fixture files, no key
dotnet run --project tools/SpirePilot.Bench -- --dry-run --filter coop-potion      # print prompts, no API calls
dotnet run --project tools/SpirePilot.Bench -- --settings ~/path/to/mod_configs/SpirePilot/settings.json
dotnet run --project tools/SpirePilot.Bench -- --settings claude.json --repeat 3 --out bench-claude.md
```

A profile file has the same fields as an AI profile in the mod's settings. `apiKey` may be `env:NAME`:

```json
{ "provider": "Anthropic", "baseUrl": "https://api.anthropic.com", "model": "claude-opus-5-5", "apiKey": "env:ANTHROPIC_API_KEY",
  "reasoningEffort": "medium", "combatReasoningEffort": "low", "maxTokens": 16000 }
```

### Options

| Option | Meaning |
| --- | --- |
| `--settings <file>` | The mod's `settings.json` or one profile JSON. Required except with `--dry-run` / `--validate`. |
| `--profile <name>` | Profile to use from settings.json (default: the active one). |
| `--keep-settings` | Also use the file's route, strategy and team settings. By default these are mod defaults, so results compare across machines. Fixture overrides always apply. |
| `--fixtures <dir>` | Fixture directory (default `tools/SpirePilot.Bench/fixtures`). |
| `--filter <text>` | Only fixtures whose id, file name or tag contains the text. Repeat it or comma-separate values. |
| `--repeat <n>` | Attempts per fixture. Models are not deterministic, so use 3 when comparing. |
| `--out <file.md>` | Also write a markdown report, including the thought and the problems for each attempt that did not pass. |
| `--dry-run` | Print the prompts without calling any API. Rule fixtures are still scored. |
| `--validate` | Only check the fixture files. |

Exit codes:
- 0: everything passed.
- 1: a fixture failed or is invalid.
- 2: usage or configuration error. A bad key or unknown model stops the run at once.

### Reading results

| Result | Meaning |
| --- | --- |
| `PASS` | actions[0] matches `expect_any` and every other check passed (messages, plan length, call count). |
| `FAIL` | Not an expected action, or another check failed (missing reply, plan too long, a repair turn was needed). |
| `FORBIDDEN` | actions[0] is a known mistake listed in `forbid`. |
| `ERROR` | No usable decision (network error, two unparseable answers). The rule-based fallback is disabled, so the model itself is scored. |
| `FLAKY 2/3` | Attempts disagreed. |
| `DRY` | `--dry-run`: no model was asked. |

Latency and tokens are averaged per attempt. Repair turns and HTTP retries are included. The totals line also shows
cached input tokens.

### Fixture format

Envelope fields are snake_case. `snapshot` is exactly what `GET /state` returns: camelCase with string enums. Property
case does not matter, and comments and trailing commas are allowed.

| Field | Meaning |
| --- | --- |
| `id` | Unique name (defaults to the file name). |
| `rationale` | One line saying why the expected answer is right. Required. |
| `tags` | Labels for `--filter` (`combat`, `coop`, `ironclad`, `zh`, ...). |
| `language` | `zh` / `en`: the language for team messages. |
| `settings` | Partial PilotSettings merged over the base settings, e.g. `{"route":{"preset":"Aggressive"}}`. `profiles` cannot be overridden. |
| `live_instruction` | One-shot owner instruction. |
| `memo`, `history` | The AI's memo and recent history lines. |
| `inbox` | Team messages delivered before the decision. Fields: `id`, `kind` (e.g. `request_potion`), `from`, `to`, `fromName`, `fromAi`, `text`, `potion`, `debuff`, `target`, `replyTo`. |
| `expect_any` | Acceptable actions[0]: a menu id (`a3`) or a semantic key (`play:BASH+0@E:NIBBIT#0`). A trailing `*` matches a key prefix. Empty means any action. |
| `forbid` | Actions that must not be actions[0]. |
| `expect_message_kind` | At least one message of one of these kinds (`accept`, `decline`, `offer_potion`, ...). |
| `forbid_message_kind` | No message of these kinds. |
| `expect_reply_to` | The inbox message id that the expected message must answer. |
| `max_messages` | Maximum number of team messages; `0` means the AI should stay quiet. |
| `max_plan_len` | Maximum number of action ids in the answer. |
| `max_llm_calls` | Maximum number of model calls; `1` means the first answer must be valid. |
| `expect_source` | `Llm` by default. `Rule` (or `Trivial`) means the engine must decide on its own and never call the model (pinned route, character policy). |
| `snapshot` | The `GameSnapshot`. Menu ids must be `a0..aN` in order. |

### Adding fixtures

1. **Capture a real snapshot.** Enable the local HTTP API in the overlay's log tab. Stop at the decision you want
   (Advisor mode helps). Then run `curl -s http://127.0.0.1:17654/state -H "Authorization: Bearer <token>"` and paste
   the output as `"snapshot"`. Extra computed fields such as `localPlayer` and `teammates` are ignored. `GET /prompt`
   shows what the model saw.
2. **Mine the traces.** `mod_configs/SpirePilot/traces/trace-<date>.jsonl` lists the options offered at every step, the
   choice and the thought. Use the traces to find mistakes, then reproduce the situation and capture it with `/state`.
   Traces do not hold full snapshots.
3. Write `expect_any`, `forbid` and a one-line `rationale`. Only put clear mistakes in `forbid`. When several answers are
   reasonable, list them all in `expect_any`.
4. Do the arithmetic (damage, block, poison, Doom) so the expected answer is beyond dispute.
5. Don't always put the right answer at `a0`. Real hands and potion belts are in arbitrary order, and a model that
   always picks the first option should score nothing.
6. Run `--validate` and `dotnet test tests/SpirePilot.Bench.Tests`. CI runs the tests too. They check that:
   - every fixture parses;
   - every key exists in the fixture's menu;
   - ids run in order.

   They also send an "oracle" answer through the real engine to prove the fixture can pass, and check that a
   forbidden answer is reported as FORBIDDEN.

Names may be localized (the game shows localized names). Ids in semantic keys stay the English model ids (`BASH`,
`NIBBIT`). See the Chinese section above for the list of current fixtures.
