# SpirePilot · 杀戮尖塔 2 AI 领航员

让 AI 来玩《杀戮尖塔 2》(Slay the Spire 2)。SpirePilot 是一个游戏 Mod：它读取当前游戏的完整状态
（手牌、牌堆、能量、敌人意图、遗物、药水、地图、商店、事件、卡牌升级后的效果、关键词说明、Boss 说明……），
把所有**合法操作**整理成菜单交给大模型选择，再通过游戏自己的按钮和同步通道去执行。附带一个游戏内控制面板，可以：

- **控制 AI 的游玩方式**：全自动 / 只打战斗 / 只管战斗外 / 只给建议（顾问模式）/ 单步执行
- **控制游玩路线**：路线风格预设（均衡、激进刷精英、稳健、多逛商店、多走问号）、自定义各房间权重、
  一键把规划好的路线（或你选的下一个节点）“钉住”让 AI 照着走、或写一段路线要求让 AI 参考
- **限定角色**：只允许 AI 选择你勾选的角色，支持随机 / 轮换 / 固定 / 让 AI 自己选，设置进阶等级，打完自动开下一局
- **多人模式与队友交流**：AI 会根据队友的血量、药水、敌人情况主动沟通，例如向队友要药水、请队友给某个敌人上易伤/虚弱、
  问队友需不需要药水、集火某个敌人、求保护；也会回应队友的请求（接受后真的去做，比如把能对队友使用的药水用在队友身上）。
  AI 已经结束回合时队友求助也会回复，必要时“撤销结束回合”去帮忙；对没装 Mod 的队友改用游戏自带的表情，
  没装 Mod 的队友也能用表情回答 AI（点赞 = 接受、点踩 = 拒绝）。
  你自己也可以在面板里打字或用快捷按钮给队友发消息。战斗中消息以气泡形式显示在发送者角色头顶，
  战斗外以屏幕提示显示，“AI”按钮和队友页上会显示未读条数
- **自定义 AI 接口**：任何 OpenAI 兼容接口（OpenAI、DeepSeek、通义千问、Kimi、智谱 GLM、硅基流动、OpenRouter、
  Gemini、Ollama / LM Studio 本地模型……）以及 Anthropic Claude 原生接口；可改 Base URL、模型、密钥、代理、
  请求头、额外请求体参数、推理强度等，并可保存多套配置随时切换
- **本地 HTTP 接口与 MCP（可选）**：让外部 AI 工具 / 脚本（Claude Desktop、Claude Code、Cursor……）读取状态并操作（默认关闭，只监听本机）
- **离线评测**：不开游戏，用 28 道中英文决策题给你选的模型打分，方便比较不同模型 / 配置（见 [docs/BENCHMARK.md](docs/BENCHMARK.md)）

> ⚠️ 这是一个**实验性** Mod，作者无法在开发环境中实际运行游戏进行联调；所有游戏接口都按游戏的真实程序集签名编译通过，
> 并参考了多个已在实机运行的开源 Mod，但仍可能在某些界面上卡住。遇到问题请打开面板“日志”页生成诊断报告，
> 连同日志文件一起反馈；建议先按 [docs/TESTING.md](docs/TESTING.md) 的清单逐项试一遍。AI 卡住时会自动暂停，不会乱点。

---

## 1. 版本说明

《杀戮尖塔 2》目前有两个分支，接口互不兼容，请下载对应版本：

| 你的游戏分支 | 游戏版本 | 下载/编译的包 |
| --- | --- | --- |
| Steam 默认（正式分支） | 0.107.x | `SpirePilot-<版本>-game0.107.zip` |
| Steam 测试分支（public-beta） | 0.111.x | `SpirePilot-<版本>-game0.111.zip` |

不确定自己的版本？游戏主菜单角落会显示版本号。Mod 读到游戏版本后会和包对应的版本比较：

| 情况 | 例子 | 结果 |
| --- | --- | --- |
| 一致 | game0.111 包 + 游戏 0.111.x | 正常运行 |
| 游戏比最新的包还新 | game0.111 包 + 游戏 0.112 | AI 照常运行；“控制”页和屏幕提示显示黄色警告（未经测试，部分功能可能失效），请留意 SpirePilot 更新 |
| 不兼容 | game0.107 包 + 游戏 0.108 或更新；包比游戏新 | AI 不启动；“控制”页和屏幕提示显示红色提示：该装哪个包，或者还没有对应的包、请检查更新 |

不兼容时拦截本身不会改你保存的模式（换对包后照旧），只是 AI 按“关闭”运行，HTTP 接口也拒绝操作；这期间点“关闭”
（或 `POST /mode` 设为 Off）、打开下面的开关，会像平常一样把保存的模式改成“关闭”。“控制”页的
**仍然运行（风险自负）** 开关可以强行运行：打开时模式先切到“关闭”，再自己选模式；它只对当前这个游戏版本 + 这个包有效
（保存为 `settings.json` 里的 `control.ignoreGameVersionFor`，例如 `"game 0.111, build 0.107"`），换了游戏版本或包会重新拦截。
在 0.107 的游戏上装 game0.111 的包时，游戏多半不会加载这个 Mod（包里写明了最低游戏版本 0.111），也就没有面板提示。

## 2. 安装

1. 把压缩包里的 `SpirePilot` 文件夹放进游戏目录下的 `mods` 文件夹（没有就新建）：
   - Windows：`C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\SpirePilot\`
   - Linux / Steam Deck：`~/.local/share/Steam/steamapps/common/Slay the Spire 2/mods/SpirePilot/`
   - macOS：`~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/mods/SpirePilot/`

   文件夹里应有 `SpirePilot.dll` 和 `mod_manifest.json`。**不要在 mods 文件夹里放其它 .json 文件**（游戏会把所有 json 当作 Mod 清单读取）。
2. 启动游戏，按提示同意加载 Mod（有 Mod 时游戏会使用单独的存档）。
3. 进入游戏后按 **F8**（或点右上角的 “AI” 小按钮）打开控制面板。

## 3. 第一次使用（3 分钟）

1. 打开面板 → **API** 页 → 从预设里选一个服务商（例如 DeepSeek 或 Anthropic Claude）→ 填入 API Key → 点 **测试连接**。
   - 密钥也可以写成 `env:环境变量名`，从系统环境变量读取，避免明文保存在配置里。
   - 国内访问海外服务可在“代理”里填 `http://127.0.0.1:7890` 之类的地址。
   - 本地模型（Ollama / LM Studio）不需要密钥，记得把模型上下文长度调大（Ollama 默认上下文较小）。
2. **角色** 页：勾选允许 AI 使用的角色，选择挑选方式和进阶等级。
3. **路线** 页：选一种路线风格；进入地图后这里会列出几条规划好的路线，可以点“固定此路线”。
4. **控制** 页：选择模式（建议先用 **顾问模式** 看看 AI 的思路，或者 **只打战斗**），之后再切到 **全自动**。
   在主菜单切到全自动，AI 会按你的角色设置自动开局。

## 4. 功能详解

### 控制模式
| 模式 | 说明 |
| --- | --- |
| 关闭 | AI 不做任何事 |
| 全自动 | 战斗、地图、奖励、商店、事件、篝火、开局全部由 AI 完成 |
| 只打战斗 | 只在战斗回合（含战斗中的选牌）出手，其余交给你 |
| 只管战斗外 | 地图、奖励、商店、事件等由 AI 处理，战斗你自己打 |
| 顾问模式 | AI 只显示建议（含理由），你可以自己操作或点“执行建议” |

其他选项：单步模式（每一步都等你点“单步”）、操作间隔（方便观看）、打完自动开下一局及局数上限、
一次性指令（例如“这回合先用火焰药水”，优先级最高）。
药水不只在战斗中用：在地图、奖励、商店、篝火、事件等界面，游戏允许使用的药水也会出现在菜单里（战斗外只对自己使用，联机时写成“使用药水 X → 自己”）；
奖励界面药水栏满时，AI 可以先喝掉一瓶再拿新药水，而不只是丢弃。
AI 失败时的保护：同一操作在同一局面反复无效（包括来回循环）/ 连续失败 / API 配置错误会**自动暂停**并提示原因。
配置错误包括密钥无效、地址不对、模型名写错、账户余额不足（含 HTTP 402，以及 DeepSeek、智谱 GLM、OpenRouter、Gemini、
Anthropic 各自的报错写法），这时 AI 会停下等你改设置，不会悄悄改用内置规则继续玩。
接口暂时不可用（网络错误、超时、限流）时：连续失败 3 次后暂停调用大模型 60 秒（之后每次翻倍，最长 10 分钟），
期间用内置规则兜底；冷却结束后再失败一次就重新进入冷却。点“继续”、切换模式或修改 AI 设置会立即清零重新尝试。
兜底可关闭；关闭后冷却触发时直接暂停，不会一直重试计费。超时最多重发一次（每次重发都可能计费）。
AI 思考时你点暂停或切换模式，这一步会立即取消，不会在你接手后还替你出牌或发消息。
你自己打开的界面（暂停菜单、设置、图鉴、查看队友，以及“放弃本局 / 退出 / 断开连接”的确认框）AI 一律不碰。

### 路线控制
- 风格预设会影响路线规划器的打分，并作为 AI 的长期偏好写进提示词。
- “自定义”可调整 普通怪 / 精英 / 篝火 / 商店 / 宝箱 / 问号 的权重。规划器还会考虑血量（血少时远离精英、看重篝火）和金币（钱多时看重商店）。
- “固定此路线”：AI 会按钉住的路线走（不再调用大模型做选择）；只对当前这一局的当前幕有效，进入下一幕或新的一局自动失效。
  AI 正在思考时你刚钉住的路线也会立即生效。你给了一次性指令（或队友有请求要回应）时这一步交给大模型决定，它仍会看到钉住的路线。
- 除了规划器给出的几条路线，也可以在路线页直接选一个下一步节点钉住。
- 外部 AI（HTTP 接口 / MCP）在状态里看到同样的路线和钉住的路线，对它们来说只是建议。
- “让 AI 自己选节点”关闭后，AI 会直接走规划器的最优路线。
- 多人模式下，AI 选择节点前会在地图上 **ping** 一下，让队友看到它想去哪（游戏原生功能，没装 Mod 的队友也能看到）。

### 角色限定
- 只能从勾选的、已解锁的角色中选择；不勾选任何角色 = 允许全部已解锁角色。
- 挑选方式：随机、轮换（多局依次轮换）、固定第一个、让 AI 在允许范围内自己选。
- 进阶：填 -1 表示使用该角色已解锁的最高进阶。进阶由你决定，AI 不会自己改。
- 进入角色选择时即使游戏已默认选中某个角色，也会按你的挑选方式改选。
- 存档里的角色不在允许列表中时，AI 不会点“继续”（会提示你自行处理存档）；你“保存并退出”或联机断开后，AI 不会自动继续或开新局。
- 联机当房主时，人数不足 2 人 AI 不会准备出发。
- 这些限制（以及“不允许把药水给队友”）是从操作菜单里直接去掉的，对内置 AI 和外部 AI（HTTP 接口 / MCP）同样生效。
- 角色页的“提示词内容”开关：遗物 / 药水 / 充能球的效果说明（关掉省 token，但 AI 看不到这些效果）、战斗中的抽牌堆 / 弃牌堆、保留的历史条数。

### 多人模式与队友交流
- 游戏本身没有文字聊天。SpirePilot 通过游戏自带的联机通道发送自己的消息包，**只发给同样安装了 SpirePilot 的队友**
  （通过联机握手中的 Mod 列表识别），不会影响没装 Mod 的玩家。
- 房主也需要安装 SpirePilot，客户端之间的消息由房主转发。
- 消息类型：聊天、要药水、给药水、问要不要药水、要减益（易伤/虚弱等，可指定敌人）、集火、求保护、路线建议、接受/拒绝。
  每条消息都带有可读文字，人类队友能直接看懂；AI 队友能按结构化字段执行。
- “给药水”的实际做法是：把可以对玩家使用的药水（治疗、格挡、能量、力量……）**用在队友身上**（游戏不支持直接转交药水）。
- 敌人按游戏的战斗 id 传递，所以即使两边游戏语言不同、敌人编号不同，“给 X 上易伤”也能对上同一个敌人。
- AI 结束回合后仍会回复队友；只剩“撤销结束回合”时，只有为了帮队友（例如给药水）才会撤销。
  队友对 AI 的提议说“好”时，敌人回合里不会为此调用模型（为别的队友消息调用时也先留着这个“好”），轮到 AI 时再照做。
- 即使队友没装 Mod，AI 也能看到所有队友的状态（联机时每台机器都会模拟所有玩家）和他们的药水、卡组，会据此做决策；状态里会标出谁没装 Mod，
  对这些队友不发文字，改用游戏自带的表情和地图 ping：请求 → “!”，询问 / 提议给药水 → “?”，接受 → 点赞，拒绝 → 点踩（聊天和路线建议没有对应表情）。
  发给“所有人”而队伍里有人装了、有人没装时，文字发给装了的队友，表情发给没装的；AI、队友页和 HTTP 接口 / MCP 都按这个规则发送。
- 反过来，没装 Mod 的队友可以用表情回答：点赞 / 点踩 = 接受 / 拒绝 AI 最近一次以表情发给他（或所有人）、还没结束的请求（请求已结束，或之后装了 Mod 的队友
  或其 AI 又发过“!” / “?”时，不对应任何请求）；
  “!” / “?” 会作为一句简短消息进入 AI 的收件箱。AI 回应过后 45 秒内，同一队友再发同样的表情只当作背景信息，不会再为它调用模型；
  AI 按计划连续出牌时这类表情不会打断出牌，但结束回合、用药水或打出效果难以预料的牌之前会先让模型看到它。
- 装了 Mod 的队友用文字回答：他们的 AI 发的表情不会进收件箱；他们自己手动发的表情，在队伍里还有没装 Mod 的人时只当作背景信息（多半是发给那位队友的），
  全队都装了 Mod 时，“!” / “?” 同样会让 AI 回应，点赞 / 点踩回答 AI 最近发给他（或所有人）、还没结束的请求。
- 你在队友页（或通过 HTTP 接口 / MCP）发出的请求、提议和回答会记进 AI 的历史，请求会出现在 AI 的“未答复的请求”里，队友的答复由 AI 照着处理；
  你已经回答过的队友消息，AI 不会再回答一次（AI 正在思考时你回答的也一样）。
- 队友消息 3 分钟后过期，请求 5 分钟后（或所在的战斗 / 回合结束后）过期。“只管战斗外”模式下战斗由你来打：这场战斗里队友发来的战斗请求
  （要格挡、减益、集火、要药水以及“!” / “?”）在战斗结束后作废，AI 不会到奖励界面才去回应；
  你在这场战斗里（队友页或 HTTP 接口）发出的请求和答应的事也随战斗结束。
- 战斗外收到的队友消息会以屏幕提示显示，“AI”按钮和队友页上显示未读条数（打开队友页后清零）；战斗中显示为角色头顶的气泡
  （关闭了气泡，或发送者不在画面上时，改为屏幕提示）。
- 为了不刷屏：AI 每分钟消息数有上限、记得自己发过的请求不会重复、不回复“好的/不行”这类回答，两个 AI 队友不会互相无限聊天
  （用“给药水”或“问要不要药水”回答要药水的请求时按“接受”发出）；
  对“所有人”的求助，一个队友接下后其他 AI 不会重复帮忙。
- 你选择“不允许把药水给队友”时，这类操作直接从 AI 的菜单里去掉，而不仅仅是提示（战斗外的药水只会对自己用，本来就没有给队友的选项）。

### AI 接口
- 预设：Anthropic Claude（原生 Messages API，默认 `claude-opus-5-5`）、OpenAI、DeepSeek、通义千问、Kimi、智谱 GLM、
  硅基流动、OpenRouter、Gemini（OpenAI 兼容端点）、Ollama、LM Studio、自定义。模型名都可以改。
- 推理强度可分别设置“一般决策”和“战斗回合”（战斗用低一点更快更省）。Claude 预设默认：一般 medium、战斗 low；
  Claude 只提供 low / medium / high / xhigh / max。某个强度值被 Claude 接口拒绝时，先改用“一般决策”的强度，仍被拒绝才不再发送强度。
- 超时：Claude、OpenAI、Gemini、智谱 GLM、OpenRouter 和本地模型（Ollama / LM Studio）的预设为 300 秒（推理模型和本地 CPU 推理可能要几分钟），其余 120 秒。
- Claude：自动开启提示缓存（系统提示词只计费一次）、结构化 JSON 输出、官方地址下启用服务端 fallback。
- OpenAI 兼容接口：自动适配常见差异（max_tokens / max_completion_tokens、输出上限过大、不支持 temperature、不支持 JSON 模式等），
  会去掉本地推理模型输出里的 `<think>` 内容。局域网内的本地服务器（192.168.x.x 等）不需要填密钥。
- 自定义请求头 / 额外请求体写错（例如 JSON 格式不对）会直接提示“AI 设置有误”并暂停，而不是悄悄失败。
- 费用提示：每次决策大约消耗 2k–6k 输入 token（缓存命中后便宜很多），战斗中 AI 会一次规划整回合以减少调用次数。
  面板会显示累计调用次数和 token。

### 本地 HTTP 接口（给外部 AI / 脚本用，默认关闭）
在“日志”页开启，默认端口 17654，只监听本机：请用 `http://localhost:17654/` 访问（能注册时也监听 `127.0.0.1`，
但 Windows 上非管理员进程无法注册 `127.0.0.1`），可设置 Bearer Token。
外部 AI 看到的菜单和内置 AI 一样，已经去掉了你的规则不允许的操作（角色、进阶、存档角色、不给队友用药水）。

| 接口 | 说明 |
| --- | --- |
| `GET /state` | 当前完整状态 + 合法操作菜单（JSON，含 `sequence`） |
| `GET /state?format=text` | 内置 AI 提示词里的状态文本（地图上含路线规划器的 `[ROUTES]` 和钉住的路线）+ `Menu:`（每行 `aN \| 标签`），不能操作时首行为 `WAITING: 原因` |
| `GET /state?include=text` | 上面的 JSON，外加同一次读取生成的 `text` 字段（编号一致） |
| `GET /prompt` | 内置 AI 此刻会发送的完整提示词：`system`、`owner`、`user`（含队友消息、未答复的请求、路线和钉住的路线、一次性指令；和内置 AI 一样不含过期的消息和请求，不能操作时带 `[NO ACTION NEEDED]`、不含一次性指令）。只读，不会把消息标为已读 |
| `POST /act {"semantic_key":"end_turn"}` | 执行菜单中的操作（需把内置 AI 设为关闭或顾问模式）。推荐用 `semantic_key`；用 `{"id":"a3","sequence":123}` 时编号只在那次状态里有效：菜单变了会返回 409 `stale` 和新菜单。你的规则不允许的操作返回 403 `owner_rule`；包与游戏版本不兼容（且没开“仍然运行”）时返回 409 `version_mismatch` |
| `POST /mode {"mode":"FullAuto"}` | 切换模式（只接受模式名）；包与游戏版本不兼容（且没开“仍然运行”）时只接受 `Off`，其他返回 409 `version_mismatch`；设置文件写不进去时多一个 `saveError`（模式本次有效） |
| `POST /instruction {"text":"..."}` | 给内置 AI 一次性指令 |
| `GET /chat` / `POST /chat {"text":"...","to":"all"}` | 读取 / 发送队友消息，见下 |
| `GET /status` | 状态、实际生效的模式、token 统计、最近错误；AI 因版本不兼容被拦截时有 `versionMismatch`（英文说明），在未经测试的游戏版本上或强行运行时有 `versionWarning` |
| `GET /compendium?kind=cards` | 卡牌 / 遗物 / 药水 / 能力图鉴 |
| `GET /diagnostics` | 写一份诊断报告 `diagnostics-*.md`（和“日志”页按钮一样：版本、补丁、缺失的游戏成员、当前界面、日志尾部；不含 API Key）并返回内容，响应头 `X-Diagnostics-File` 是文件路径 |

`POST /chat`：`kind` 可选 `chat`、`request_potion`、`offer_potion`、`ask_need_potion`、`request_debuff`、`request_focus`、
`request_block`、`accept`、`decline`，可带 `potion`、`debuff`、`target`；回答某条消息时带 `reply_to`（`GET /chat` 里那条消息的 `id`），
这时 `to` 不写或写 `all` 就发给提问的人（问题是发给所有人的则发给所有人；提议 / 询问药水只发给提问的人），
回答要药水的请求时 `offer_potion` / `ask_need_potion` 按 `accept` 发出（这就是答应，所以和其他回答一样，请求是发给所有人的就发给所有人）；
`to` 写错会返回 400 和队友名单；`from_ai` 默认 true。和 AI、队友页同一个规则：文字发给装了 SpirePilot 的队友，没装的看到对应的游戏表情。
有人能收到文字时返回 `{"delivered":true,"id":"...","kind":"...","emote":<表情名或 null>,"emoteLimited":<true/false>,"emoteFailed":<true/false>}`
（`kind` 是实际发出的类型，`emote` 是没装 Mod 的队友看到的表情）；
联机时收件的队友都没装 SpirePilot：不发文字，返回 `{"delivered":false,"kind":"...","emote":<表情名或 null>,"emoteLimited":<true/false>,"emoteFailed":<true/false>,"reason":"no_reader"}`。
`emote` 为 null 表示没有显示表情（收件人都装了 Mod；聊天没有对应表情；AI 3 分钟内刚对同一对象发过同类表情或消息额度已用完，这时 `emoteLimited` 为 true；
或游戏暂时显示不了，这时 `emoteFailed` 为 true）。
`from_ai` 为 true 时，要发的文字因消息额度用完或 3 分钟内发过同样的消息被拒，会返回 429 `rate_limited`（什么都不发）。发出的请求、提议和回答会记进内置 AI 的历史（见“多人模式与队友交流”）。

```bash
curl "http://localhost:17654/state?format=text"
curl -X POST http://localhost:17654/act -H "Content-Type: application/json" -d '{"semantic_key":"end_turn"}'
```

安全：带 `Origin` 头（网页发起）的请求一律拒绝；POST 必须带 `Content-Type: application/json`（否则 415），正文不超过 64 KB。
错误响应为 `{"error":"...","code":"..."}`：403 `owner_rule`（你的规则不允许这个操作）；409 的 `code` 为 `pilot_active`（内置 AI 在操作）、
`busy`（现在不能操作）、`stale`（编号已过期）或 `version_mismatch`（包与游戏版本不兼容）。

#### MCP

`tools/mcp/` 里有一个 MCP 服务器，让 Claude Desktop、Claude Code、Cursor 等 MCP 客户端通过上面的接口直接玩游戏：
先在“日志”页开启 HTTP 接口，再添加服务器，例如
`claude mcp add spirepilot -- uv run --directory /path/to/SpirePilot/tools/mcp spirepilot-mcp`。
外部模型看到的状态文本（含路线和钉住的路线）和操作菜单（已按你的规则过滤）与内置 AI 相同，钉住的路线对它只是建议；
开始前用 `set_mode` 把模式设为关闭或顾问。详见 [tools/mcp/README.md](tools/mcp/README.md)。

MCP: `tools/mcp/` has an MCP server so Claude Desktop, Claude Code, Cursor and other MCP clients can play through this API; see [tools/mcp/README.md](tools/mcp/README.md).

HTTP API (English): listens on `http://localhost:<port>/` (plus `127.0.0.1` where the OS allows it; on Windows a
non-elevated process may only register `localhost`, so always use `localhost`). `GET /state` (JSON with `sequence`),
`GET /state?format=text` (the pilot's state text, with the route planner's `[ROUTES]` and the pinned route on the map,
plus a `Menu:` of `aN | label` lines, `WAITING: <reason>` first when it cannot act), `GET /state?include=text` (the
JSON plus that text as `text`, from the same capture). Every menu is the one the built-in pilot gets: the owner's rules
(allowed characters, ascension, no continuing a saved run of a disallowed character, no potions for teammates when
giving is off) are applied first. `GET /prompt` is exactly the prompt the built-in pilot would send now (`system`,
`owner`, `user` with the team inbox, open requests, routes, pin and live instruction; expired messages and requests
left out, and `[NO ACTION NEEDED]` without the live instruction while it cannot act, as for the pilot), built without
side effects.
`POST /act` takes `{"semantic_key": ...}` (preferred) or `{"id": "a3", "sequence": <snapshot sequence>}`: ids are
positions in one snapshot's menu, so when that menu changed since the given sequence the call is refused with 409
`code: "stale"` and the fresh menu. An action an owner rule removed is refused with 403 `code: "owner_rule"`. Other
409 codes: `pilot_active`, `busy`. While this build is blocked for the game version (incompatible, "Run anyway" off),
`POST /act` and `POST /mode` with any mode but `Off` answer 409 `version_mismatch`; `GET /status` then carries
`versionMismatch` (why, and the fix that exists) and reports the effective mode `Off`, while `versionWarning` is set when
the pilot runs on an untested newer game or by "Run anyway". `POST /mode` adds `saveError` when settings.json could not
be written (the mode applies for this session). `GET /diagnostics` writes a `diagnostics-*.md` file, as the Log tab's
button does, returns its text and names the file in the `X-Diagnostics-File` header. Requests with an `Origin` header
are refused; POST bodies must be `application/json` (else 415), at most 64 KB, e.g.
`curl -X POST http://localhost:17654/act -H "Content-Type: application/json" -d '{"semantic_key":"end_turn"}'`.
`POST /chat` takes `text`, `to`, `kind` (`chat`, `request_potion`, `offer_potion`, `ask_need_potion`, `request_debuff`,
`request_focus`, `request_block`, `accept`, `decline`), `potion`, `debuff`, `target`, `reply_to` (the `id` of the
message answered, from `GET /chat`) and an optional `from_ai` (default true); it rejects an unknown recipient with 400
and the teammates' names. With `reply_to` and no recipient (or `"all"`) the answer goes to the asker, or to everyone
when the question was for everyone; a potion offer or question goes to the asker only. A potion offer or question
answering a potion request goes out as `accept` (it is the yes), so like any answer it goes to everyone when the
request was for everyone. It follows the pilot's rule: in co-op the text goes to teammates who run SpirePilot and the
kind's vanilla emote to the others, also for "all" in a mixed team; the answer is `{"delivered": true, "id": ...,
"kind": ..., "emote": <name or null>, "emoteLimited": <bool>, "emoteFailed": <bool>}` (`kind`: the kind sent). When
no addressed teammate runs SpirePilot no text is sent and the answer is `{"delivered": false, "kind": ..., "emote":
<name or null>, "emoteLimited": <bool>, "emoteFailed": <bool>, "reason": "no_reader"}`. `emote` is null when nothing
was shown (nobody needed one, chat has no emote, an AI sender showed the same kind to them in the last 3 minutes or
spent its budget, which sets `emoteLimited`, or the game could not show one, which sets `emoteFailed`); text an AI
sender may not send (budget spent, or a repeat) gets 429 `rate_limited`. Requests, offers and answers sent this way
enter the built-in pilot's history (requests also its open requests), and the message answered is not answered by the
pilot again (also when the pilot was deciding on it).

## 5. 文件位置

配置与日志保存在游戏用户数据目录下的 `mod_configs/SpirePilot/`（不在 mods 文件夹里）：
- `settings.json`：所有设置（含 API Key，请勿分享；可用 `env:` 写法避免明文）
- `spirepilot.log`：日志（上一次的日志为 `spirepilot.log.prev`）
- `traces/trace-日期.jsonl`：每一步决策记录（给了哪些选项、选了什么、结果），可在设置中关闭
- `diagnostics-*.md`：诊断报告（“日志”页按钮或 `GET /diagnostics` 生成，后者在响应头 `X-Diagnostics-File` 里给出路径）

Windows 通常是 `%APPDATA%\SlayTheSpire2\mod_configs\SpirePilot\`（以游戏实际的用户目录为准，日志第一行会打印完整路径）。

## 6. 从源码编译

需要 [.NET 9 SDK](https://dotnet.microsoft.com/download)（或更新版本）。

```bash
# 只编译（使用 NuGet 上的游戏引用程序集，不需要安装游戏）
dotnet build src/SpirePilot -c Release -p:Sts2GameVersion=0.111      # 测试分支
dotnet build src/SpirePilot -c Release -p:Sts2GameVersion=0.107      # 正式分支

# 编译并直接装进游戏（自动查找 Steam 默认路径，也可手动指定）
dotnet build src/SpirePilot -c Release -p:Sts2GameVersion=0.111 -p:DeployToGame=true -p:Sts2Dir="D:/SteamLibrary/steamapps/common/Slay the Spire 2"

# 单元测试 + 打包两个版本的 zip 到 dist/
dotnet test tests/SpirePilot.Core.Tests
dotnet test tests/SpirePilot.Bench.Tests
bash scripts/package.sh            # Windows: powershell -File scripts\package.ps1

# 离线评测：用你的模型配置跑 28 道决策题（详见 docs/BENCHMARK.md）
dotnet run --project tools/SpirePilot.Bench -- --settings 你的配置.json
```

找到本地游戏时会直接引用游戏自己的 `sts2.dll`（`-p:Sts2RefSource=nuget` 可强制使用 NuGet 引用程序集）。
游戏出了还没有对应包的新分支（例如 0.112）时，可以对着本机安装的这个游戏编译：
`dotnet build src/SpirePilot -c Release -p:Sts2GameVersion=0.112 -p:Sts2Dir="…/Slay the Spire 2"`。它按 0.111 的代码编译，
版本检查按 0.112 处理；NuGet 引用程序集只有 0.107 和 0.111，其他版本号用 NuGet 编译会直接报错。
架构说明见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 7. 常见问题

- **面板打不开**：确认 Mod 已加载（日志里有 `SpirePilot ready`），按 F8 或点右上角 AI 按钮；可在设置里改快捷键。
- **AI 一直“等待中”**：游戏动画/行动队列未结束、轮到队友、或打开了暂停菜单/图鉴等只给人看的界面时 AI 会等待。
- **AI 暂停并提示 API 设置有误**：检查密钥、Base URL（OpenAI 兼容接口一般要包含 `/v1`）、模型名和账户余额，然后点“继续”。
- **面板提示这个包对应的游戏版本不对**：红色提示按第 1 节换成与你的游戏分支对应的包；还没有对应的包时可以等 SpirePilot 更新、从源码编译（第 6 节），
  或在“控制”页打开“仍然运行（风险自负）”。黄色的“未经测试”提示不影响使用。
- **队友收不到消息**：对方（以及房主）需要安装同版本的 SpirePilot；队友列表里会显示谁装了 Mod。没装的队友只能看到表情和地图 ping。
- **外部工具连不上 HTTP 接口**：确认已在“日志”页开启；Windows 上请用 `http://localhost:17654` 而不是 `127.0.0.1`；网页里无法直接调用（为安全起见会拒绝）。
- **会影响成就/存档吗**：游戏在加载 Mod 时会自动使用单独的 Mod 存档；SpirePilot 本身不修改任何游戏数据，只通过正常操作游玩。

## English summary

SpirePilot is a Slay the Spire 2 mod that lets an LLM play the game. It reads the complete game state, offers the model
a menu of legal actions, and executes the chosen one through the game's own buttons and synchronized multiplayer paths.
An in-game panel (F8) controls the mode (full auto / combat only / out-of-combat only / advisor / step), routing
(presets, custom room weights, routes or a chosen next node pinned to the current run), character restrictions
(allow-list, pick policy, ascension, auto-restart — enforced as menu filters for the built-in pilot and external agents
alike, never only as prompt text), co-op teammate communication (request/offer potions, request debuffs on an enemy
identified by its shared combat id, focus fire, protection — over a mod-to-mod channel on the game's own network, shown
as speech bubbles in combat and as a toast with an unread count elsewhere; vanilla reaction emotes to and from teammates
without the mod — in a mixed team the text goes to the teammates with the mod and the emote to the others — whose
thumbs up / down answer the newest open request shown to them; answers even after ending its turn, and can undo the end
of turn to help), and the AI endpoint (any OpenAI-compatible API or Anthropic's native Messages API, multiple
profiles, proxy, headers, extra body; wrong keys, model names or an empty balance stop the pilot with a "fix your
settings" message). Potions are also used outside combat (on its own character, labelled "→ yourself" in co-op) where
the game allows it. An optional localhost HTTP API and an MCP server expose state and actions to external agents, and
an offline benchmark scores a model on 28 decision fixtures. Builds exist for game 0.107 (main) and 0.111 (beta). A build on an incompatible game
(the 0.107 build on any newer game) keeps the pilot off and names only fixes that exist: the build to install, a
SpirePilot update, or a "Run anyway" switch on the Control tab that holds for that one game/build pairing. The block
itself leaves the saved mode alone, so a matching build starts in it; picking Off or switching "Run anyway" on saves
Off like any mode change. The 0.111 build on a newer, untested game runs with a warning; the 0.111 build declares 0.111 as its
minimum game version, so an older game is not expected to load it at all. Start with the in-game test plan in
[docs/TESTING.md](docs/TESTING.md).

## License & credits

MIT（见 [LICENSE](LICENSE)）。实现时参考/学习了以下开源项目的接口用法与经验：
[STS2MCP](https://github.com/Gennadiyev/STS2MCP)（MIT）、[STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)（MIT）、
[BaseLib-StS2](https://github.com/Alchyr/BaseLib-StS2)（MIT）、[Modding-Tutorial](https://github.com/fresh-milkshake/Modding-Tutorial)（MIT）；
设计经验参考了 [STS2-Agent](https://github.com/CharTyr/STS2-Agent)（未复制其代码）。
Slay the Spire 2 © Mega Crit。本项目与 Mega Crit 无关。
