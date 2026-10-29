# SpirePilot · 杀戮尖塔 2 AI 领航员

让 AI 来玩《杀戮尖塔 2》(Slay the Spire 2)。SpirePilot 是一个游戏 Mod：它读取当前游戏的完整状态
（手牌、牌堆、能量、敌人意图、遗物、药水、地图、商店、事件……），把所有**合法操作**整理成菜单交给大模型选择，
再通过游戏自己的按钮和同步通道去执行。附带一个游戏内控制面板，可以：

- **控制 AI 的游玩方式**：全自动 / 只打战斗 / 只管战斗外 / 只给建议（顾问模式）/ 单步执行
- **控制游玩路线**：路线风格预设（均衡、激进刷精英、稳健、多逛商店、多走问号）、自定义各房间权重、
  一键把规划好的路线“钉住”让 AI 严格照着走、或写一段路线要求让 AI 参考
- **限定角色**：只允许 AI 选择你勾选的角色，支持随机 / 轮换 / 固定 / 让 AI 自己选，设置进阶等级，打完自动开下一局
- **多人模式与队友交流**：AI 会根据队友的血量、药水、敌人情况主动沟通，例如向队友要药水、请队友给某个敌人上易伤/虚弱、
  问队友需不需要药水、集火某个敌人、求保护；也会回应队友的请求（接受后真的去做，比如把能对队友使用的药水用在队友身上）。
  你自己也可以在面板里打字或用快捷按钮给队友发消息。消息会以气泡形式显示在发送者角色头顶
- **自定义 AI 接口**：任何 OpenAI 兼容接口（OpenAI、DeepSeek、通义千问、Kimi、智谱 GLM、硅基流动、OpenRouter、
  Gemini、Ollama / LM Studio 本地模型……）以及 Anthropic Claude 原生接口；可改 Base URL、模型、密钥、代理、
  请求头、额外请求体参数、推理强度等，并可保存多套配置随时切换
- **本地 HTTP 接口（可选）**：让外部 AI 工具 / 脚本读取状态并操作（默认关闭，只监听 127.0.0.1）

> ⚠️ 这是一个**实验性** Mod，作者无法在开发环境中实际运行游戏进行联调；所有游戏接口都按游戏的真实程序集签名编译通过，
> 并参考了多个已在实机运行的开源 Mod，但仍可能在某些界面上卡住。遇到问题请打开面板“日志”页，
> 附上日志文件反馈。AI 卡住时会自动暂停，不会乱点。

---

## 1. 版本说明

《杀戮尖塔 2》目前有两个分支，接口互不兼容，请下载对应版本：

| 你的游戏分支 | 游戏版本 | 下载/编译的包 |
| --- | --- | --- |
| Steam 默认（正式分支） | 0.107.x | `SpirePilot-<版本>-game0.107.zip` |
| Steam 测试分支（public-beta） | 0.111.x | `SpirePilot-<版本>-game0.111.zip` |

不确定自己的版本？游戏主菜单角落会显示版本号。

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
AI 失败时的保护：同一操作重复无效 / 连续失败 / API 配置错误（密钥、地址、模型名）会**自动暂停**并提示原因；
接口暂时不可用时会用内置规则兜底（可关闭），避免卡局。

### 路线控制
- 风格预设会影响路线规划器的打分，并作为 AI 的长期偏好写进提示词。
- “自定义”可调整 普通怪 / 精英 / 篝火 / 商店 / 宝箱 / 问号 的权重。规划器还会考虑血量（血少时远离精英、看重篝火）和金币（钱多时看重商店）。
- “固定此路线”：AI 会严格按钉住的路线走（不再调用大模型做选择）；进入下一幕后自动失效。
- “让 AI 自己选节点”关闭后，AI 会直接走规划器的最优路线。
- 多人模式下，AI 选择节点前会在地图上 **ping** 一下，让队友看到它想去哪（游戏原生功能，没装 Mod 的队友也能看到）。

### 角色限定
- 只能从勾选的、已解锁的角色中选择；不勾选任何角色 = 允许全部已解锁角色。
- 挑选方式：随机、轮换（多局依次轮换）、固定第一个、让 AI 在允许范围内自己选。
- 进阶：填 -1 表示使用该角色已解锁的最高进阶。

### 多人模式与队友交流
- 游戏本身没有文字聊天。SpirePilot 通过游戏自带的联机通道发送自己的消息包，**只发给同样安装了 SpirePilot 的队友**
  （通过联机握手中的 Mod 列表识别），不会影响没装 Mod 的玩家。
- 房主也需要安装 SpirePilot，客户端之间的消息由房主转发。
- 消息类型：聊天、要药水、给药水、问要不要药水、要减益（易伤/虚弱等，可指定敌人）、集火、求保护、路线建议、接受/拒绝。
  每条消息都带有可读文字，人类队友能直接看懂；AI 队友能按结构化字段执行。
- “给药水”的实际做法是：把可以对玩家使用的药水**用在队友身上**（游戏不支持直接转交药水）。
- 即使队友没装 Mod，AI 也能看到所有队友的状态（联机时每台机器都会模拟所有玩家），会据此做决策；也会使用地图 ping。
- 为了不刷屏：AI 每分钟消息数有上限、不重复发送相同内容、不回复“好的/不行”这类回答，两个 AI 队友不会互相无限聊天。

### AI 接口
- 预设：Anthropic Claude（原生 Messages API，默认 `claude-opus-5-5`）、OpenAI、DeepSeek、通义千问、Kimi、智谱 GLM、
  硅基流动、OpenRouter、Gemini（OpenAI 兼容端点）、Ollama、LM Studio、自定义。模型名都可以改。
- 推理强度可分别设置“一般决策”和“战斗回合”（战斗用低一点更快更省）。Claude 预设默认：一般 medium、战斗 low。
- Claude：自动开启提示缓存（系统提示词只计费一次）、结构化 JSON 输出、官方地址下启用服务端 fallback。
- OpenAI 兼容接口：自动适配常见差异（max_tokens / max_completion_tokens、不支持 temperature、不支持 JSON 模式等），
  会去掉本地推理模型输出里的 `<think>` 内容。
- 费用提示：每次决策大约消耗 2k–6k 输入 token（缓存命中后便宜很多），战斗中 AI 会一次规划整回合以减少调用次数。
  面板会显示累计调用次数和 token。

### 本地 HTTP 接口（给外部 AI / 脚本用，默认关闭）
在“日志”页开启，默认端口 17654，只监听 127.0.0.1，可设置 Bearer Token。

| 接口 | 说明 |
| --- | --- |
| `GET /state` | 当前完整状态 + 合法操作菜单（JSON） |
| `GET /prompt` | 内置 AI 在当前状态下会发送的提示词 |
| `POST /act {"id":"a3"}` | 执行菜单中的操作（需把内置 AI 设为关闭或顾问模式） |
| `POST /mode {"mode":"FullAuto"}` | 切换模式 |
| `POST /instruction {"text":"..."}` | 给内置 AI 一次性指令 |
| `GET /chat` / `POST /chat {"text":"...","to":"all"}` | 读取 / 发送队友消息 |
| `GET /status` | 状态、token 统计、最近错误 |

## 5. 文件位置

配置与日志保存在游戏用户数据目录下的 `mod_configs/SpirePilot/`（不在 mods 文件夹里）：
- `settings.json`：所有设置（含 API Key，请勿分享；可用 `env:` 写法避免明文）
- `spirepilot.log`：日志（上一次的日志为 `spirepilot.log.prev`）
- `traces/trace-日期.jsonl`：每一步决策记录（给了哪些选项、选了什么、结果），可在设置中关闭

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
bash scripts/package.sh            # Windows: powershell -File scripts\package.ps1
```

找到本地游戏时会直接引用游戏自己的 `sts2.dll`（`-p:Sts2RefSource=nuget` 可强制使用 NuGet 引用程序集）。
架构说明见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。

## 7. 常见问题

- **面板打不开**：确认 Mod 已加载（日志里有 `SpirePilot ready`），按 F8 或点右上角 AI 按钮；可在设置里改快捷键。
- **AI 一直“等待中”**：游戏动画/行动队列未结束、轮到队友、或打开了暂停菜单/图鉴等只给人看的界面时 AI 会等待。
- **AI 暂停并提示 API 设置有误**：检查密钥、Base URL（OpenAI 兼容接口一般要包含 `/v1`）、模型名，然后点“继续”。
- **队友收不到消息**：对方（以及房主）需要安装同版本的 SpirePilot；队友列表里会显示谁装了 Mod。
- **会影响成就/存档吗**：游戏在加载 Mod 时会自动使用单独的 Mod 存档；SpirePilot 本身不修改任何游戏数据，只通过正常操作游玩。

## English summary

SpirePilot is a Slay the Spire 2 mod that lets an LLM play the game. It reads the complete game state, offers the
model a menu of legal actions, and executes the chosen one through the game's own buttons and synchronized
multiplayer paths. An in-game panel (F8) controls the mode (full auto / combat only / out-of-combat only / advisor /
step), routing (presets, custom room weights, pinned routes), character restrictions (allow-list, pick policy,
ascension, auto-restart), co-op teammate communication (request/offer potions, request debuffs, focus fire, protection —
over a mod-to-mod channel on the game's own network, with speech bubbles), and the AI endpoint (any OpenAI-compatible
API or Anthropic's native Messages API, multiple profiles, proxy, headers, extra body). An optional localhost HTTP API
exposes state and actions to external agents. Builds exist for game 0.107 (main) and 0.111 (beta).

## License & credits

MIT（见 [LICENSE](LICENSE)）。实现时参考/学习了以下开源项目的接口用法与经验：
[STS2MCP](https://github.com/Gennadiyev/STS2MCP)（MIT）、[STS2-RitsuLib](https://github.com/BAKAOLC/STS2-RitsuLib)（MIT）、
[BaseLib-StS2](https://github.com/Alchyr/BaseLib-StS2)（MIT）、[Modding-Tutorial](https://github.com/fresh-milkshake/Modding-Tutorial)（MIT）；
设计经验参考了 [STS2-Agent](https://github.com/CharTyr/STS2-Agent)（未复制其代码）。
Slay the Spire 2 © Mega Crit。本项目与 Mega Crit 无关。
