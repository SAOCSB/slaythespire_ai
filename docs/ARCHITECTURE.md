# SpirePilot architecture

SpirePilot is a Slay the Spire 2 mod (C#, net9.0, Godot 4.5.1 Mono) that lets an LLM play the game:
it reads the full game state, chooses from a menu of legal actions, executes them through the game's own
code paths, and talks to co-op teammates. One DLL, no `.pck`, `affects_gameplay: false`.

```
src/SpirePilot.Core/   game-independent logic (compiled into the mod DLL and unit-tested without the game)
  Model/               GameSnapshot (plain data), ActionOption/ActionKind, PlanSafety
  Config/              PilotSettings (+ SettingsStore JSON), LlmProfile, provider presets
  Llm/                 ILlmClient: OpenAiCompatibleClient, AnthropicClient (raw HTTP), retries, adaptation
  Agent/               PilotLoop (main loop), DecisionEngine, PromptBuilder, StateFormatter, DecisionParser,
                       FallbackPolicy/CharacterPicker, AgentMemory/PlanGuard, CharacterGuide, IGameBridge
  Team/                TeamMessage, TeamWire (binary envelope), TeamChatHub (log, inbox, rate limits)
  Route/               RoutePlanner (k-best paths on the act map with preset/custom weights)
  Text/                L10n (zh/en strings of the mod itself)
src/SpirePilot/        the mod (references sts2.dll, GodotSharp.dll, 0Harmony.dll)
  ModEntry.cs          [ModInitializer] → PilotRuntime.Initialize()
  PilotRuntime.cs      owns and wires every service; the API surface used by UI and HTTP API
  Infra/               MainThread (ProcessFrame dispatcher), ModLog, SceneUtil, Reflect
  Game/                GameContext, ScreenResolver, SnapshotBuilder, ActionSink, GameBridge, UiActions,
                       ModelText, PlayerReader, Readiness, Readers/* (one per screen family)
  Team/                TeamLink (raw-packet transport + discovery + relay), SpeechBubbles
  Ui/                  Overlay (in-game control panel)
  Api/                 LocalHttpApi (optional, 127.0.0.1)
tests/SpirePilot.Core.Tests/   xUnit tests for the core (no game needed)
tools/SpirePilot.Bench/        offline decision benchmark: fixtures (GET /state snapshots + expectations) → real LLM
tests/SpirePilot.Bench.Tests/  fixture validity and scoring tests (no API calls)
tools/mcp/                     MCP server (Python) over the local HTTP API, for Claude Desktop / Claude Code / Cursor
```

## Data flow

```
PilotLoop (worker thread)
  ├─ IGameBridge.CaptureAsync ──► main thread: SnapshotBuilder.Build
  │                                 GameContext.Capture → PlayerReader / CombatReader.FillState
  │                                 ScreenResolver.Resolve → one Reader → snapshot fields + ActionSink entries
  │                                 Readiness.Check → CanAct / WaitReason
  ├─ (cannot act, but a teammate asked something) → DecisionEngine.ReplyOnlyAsync → team messages only
  ├─ DecisionEngine.DecideAsync (worker): plan step? → owner rules → trivial? → LLM (+repair) → fallback
  │     (cancelled when the user pauses or switches mode while the model thinks)
  ├─ re-capture (waits out transient "animating"/"settling"), re-validate the chosen action by SemanticKey,
  │     re-check pause / mode / a freshly pinned route
  ├─ TeamChatHub.Send (AI messages; emotes for teammates without the mod) / map ping
  └─ IGameBridge.ExecuteAsync ──► main thread: ActionSink executor (presses the game's own buttons /
                                    calls the game's synchronized entry points, then waits until settled)
```

Rules that keep this robust:
- **Menu = only source of legal actions.** A reader registers each action together with its executor in the same
  pass (`ActionSink.Add`). The LLM only ever answers with ids from that menu.
- **Main thread only for game objects.** Readers and executors run on the main thread (via `MainThread`).
  Never block it (`.Result`, `.Wait()`), never use `ConfigureAwait(false)` inside executors.
- **Fail soft.** Each snapshot section is wrapped; a broken reader loses its section, not the snapshot.
  Null means "unreadable", empty means "none".
- **No side effects while reading.** Opening a shop or chest is an action, never part of a read.
- **Co-op safety.** Only act for the local player, always through the game's synchronized paths (action queue,
  synchronizers, real buttons). Never mutate game state directly, never cancel running actions in co-op,
  never declare INetMessage types in this assembly.
- **Owner rules are menu filters, not prompt text.** Character allow-list and ascension, "no potions for
  teammates", saved runs of disallowed characters: the engine removes the actions before any model sees the menu.
- **Things the human opened are the human's.** Popups over the pause menu / settings / viewers, abandon / quit /
  disconnect confirmations, and anything outside a run when menus are not the AI's to handle get no actions.
- **No progress means stop.** The same state + action three times within the last eight steps pauses the pilot
  (catches A,A,A and A,B,A,B cycles); three failed actions in a row pause it too.

## Game versions

Steam main branch is v0.107.1, public beta is v0.111.0; they are not API compatible. The mod compiles against
either (`-p:Sts2GameVersion=0.107|0.111`), with `#if STS2_V0107` / `#if STS2_V0111` where APIs differ
(reference: `api-diff-0107-to-0111.txt` from the research). Each build ships its own manifest
(`manifest/mod_manifest.<version>.json`) with the matching `min_game_version`.

## Snapshot conventions

- Creature keys: enemies `e0..eN` (numbered in the order this client first saw them, keyed by the game's
  `Creature.CombatId`, stable during a fight, dead ones keep their number); players `p0..pN` (player slot index);
  companions/pets `o<slot>` (e.g. Osty of player slot 0 = `o0`). Keys are local to one client: team messages
  name enemies by `CombatId` ("Cultist (cid:12)"), which every client shares, and the receiver maps it back.
- Card keys: hand `h0..`, selection candidates `c0..`, card-reward choices `r0..`, shop cards `s0..`.
- Map node keys: `"col,row"` (`MapCoord`), reachable nodes in `MapInfo.Reachable`.
- Card `Type` uses the game's `CardType` names (Attack, Skill, Power, Status, Curse ...); `Target` uses `TargetType`
  names (None, Self, AnyEnemy, AllEnemies, RandomEnemy, AnyPlayer, AnyAlly, AllAllies, TargetedNoCreature, Osty).

## Action conventions

- `ActionSink.Add(kind, label, semanticKey, executor, args, unpredictable)`; ids `a0..` are assigned in order.
  Put the most relevant actions first (card plays before potions before end turn).
- Labels are localized with `L10n.T("act.*", ...)` (see Core/Text/L10n.cs) and should carry the numbers that matter
  ("Strike → Cultist (6 dmg, kills)", "Defend (5 block)", "Buy Anchor (150 gold)"). Readers may register extra
  strings with `L10n.Add` in a static constructor.
- Semantic keys identify an action across snapshots, built from model ids, never from list positions:
  `play:<CARD_ID>[+<upgrades>]@<target>` with target `E:<MONSTER_ID>#<ordinal among same id>` / `P:<netId>` / `-`;
  `potion:<POTION_ID>@<target>`, `discard_potion:<POTION_ID>`, `end_turn`, `map:<col>,<row>`,
  `reward:<kind>:<id>`, `reward:skip`, `cardreward:<CARD_ID>`, `cardreward:skip`, `shop:<kind>:<id>`, `shop:remove`,
  `shop:leave`, `rest:<option id>`, `event:<index>:<option text key or text>`, `select:<CARD_ID>[+n]`, `confirm`,
  `cancel`, `chest:open`, `chest:relic:<RELIC_ID>`, `proceed`, `popup:confirm`, `popup:dismiss`, `menu:continue`,
  `menu:new_run`, `char:<CHARACTER_ID>`, `ascension:<n>`, `embark`, `gameover:continue`, `gameover:menu`,
  `undo_end_turn`, `cardreward:reroll`, `cardreward:alt:<id>`, `deselect:<CARD_ID>[+n]`, `cancel:preview`,
  `choose:<RELIC_ID>`, `choose:bundle:<ids>`, `map:close`, `event:dialogue:<line>`; companion targets
  `O:<MONSTER_ID>@o<slot>`; duplicate otherwise-identical options get a `#n` suffix.
  Two actions with the same key must be interchangeable (identical cards).
- `unpredictable: true` for anything whose outcome cannot be foreseen (draw / random / choices / screen changes).
- Executors: re-check `GodotObject.IsInstanceValid` for every node they captured, perform the action through the
  game's own path, then wait for an observable effect (`UiActions.Expect`) and `UiActions.WaitSettled`. Return
  `ActionResult.Fail(reason)` instead of throwing; never report success when nothing happened.

## Threading helpers

- `MainThread.Run / RunAsync / Post / NextFrame / WaitUntil / Delay` (Infra/MainThread.cs).
- `UiActions.Press(NClickableControl)` (checks enabled + visible, then `ForceClick`), `ClickHolder(NCardHolder)`
  (emits `Pressed`), `Expect`, `WaitSettled`, `GameBusy`.
- `SceneUtil.Descendants<T>`, `VisibleInTree`, `Usable`, `ByScreenPosition` (Infra/SceneUtil.cs).
- `Reflect.Get/Set/TryCall` for non-public members (cached, never throws, warns once when a member is missing).

## UI

The overlay is built in code (no scenes, no Node subclasses — Godot only calls overrides on script classes registered
by its source generators). It hangs a `CanvasLayer` (layer 128) under the scene root, uses signals
(`Pressed`, `TextSubmitted`, ...) and the per-frame `MainThread.Frame` event, and the game's CJK fonts.

## Team link

Raw packets over the game's transport (client→host `NetClient.SendMessageToHost`, host→client
`NetHost.SendMessageToClient`), received by Harmony prefixes on `NetHostGameService.OnPacketReceived` /
`NetClientGameService.OnPacketReceived` that consume packets starting with the `TeamWire` magic. Packets are only
sent to peers known to run SpirePilot (mod list from the handshake / hello), the host relays client↔client
messages, and every message also carries readable text so human teammates can read it. Without the mod on the
other side, the AI still sees teammates' full state (co-op simulates everyone locally) and can use vanilla map pings.

Conversation rules (Core, `DecisionEngine` / `PilotLoop` / `AgentMemory`):
- Requests, questions and human chat are actionable; accept/decline answers and AI chat are context only, and an
  AI's counter-request (a request with `reply_to`) is never actionable — two AIs cannot keep each other talking.
- The AI remembers its own open requests/offers (not repeated; a "yes" to its offer is actionable), and accepted
  requests until fulfilled (a potion used on that player) or until their round / fight ends.
- While it cannot act (turn ended, enemy turn) a teammate's request still gets an answer (reply-only call); with
  only "undo end turn" on the menu the model may take the turn back to help, never on its own.
- Teammates without SpirePilot are marked in the state; messages to them become a vanilla reaction emote.
- Inbox is bounded (5 unread per sender, 20 total, newest 8 in a prompt) and expires on the local clock; teammate
  text is one line inside a fence it cannot close, and cannot forge section headers.
