# SpirePilot architecture

SpirePilot is a Slay the Spire 2 mod (C#, net9.0, Godot 4.5.1 Mono) that lets an LLM play the game:
it reads the full game state, chooses from a menu of legal actions, executes them through the game's own
code paths, and talks to co-op teammates. One DLL, no `.pck`, `affects_gameplay: false`.

```
src/SpirePilot.Core/   game-independent logic (compiled into the mod DLL and unit-tested without the game)
  Model/               GameSnapshot (plain data), ActionOption/ActionKind, PlanSafety
  Config/              PilotSettings (+ SettingsStore JSON), LlmProfile, provider presets, GameVersion
  Llm/                 ILlmClient: OpenAiCompatibleClient, AnthropicClient (raw HTTP), retries, adaptation
  Agent/               PilotLoop (main loop), DecisionEngine, OwnerRules, PromptBuilder, StateFormatter,
                       DecisionParser, FallbackPolicy/CharacterPicker, AgentMemory/PlanGuard, CharacterGuide,
                       IGameBridge
  Team/                TeamMessage, TeamWire (binary envelope), TeamChatHub (log, inbox, rate limits),
                       TeamDelivery (text or emote per teammate), TeamEmotes (kind ↔ vanilla reaction)
  Route/               RoutePlanner (k-best paths on the act map with preset/custom weights)
  Text/                L10n (zh/en strings of the mod itself)
src/SpirePilot/        the mod (references sts2.dll, GodotSharp.dll, 0Harmony.dll)
  ModEntry.cs          [ModInitializer] → PilotRuntime.Initialize()
  PilotRuntime.cs      owns and wires every service; the API surface used by UI and HTTP API
  Infra/               MainThread (ProcessFrame dispatcher), ModLog, SceneUtil, Reflect
  Game/                GameContext, ScreenResolver, SnapshotBuilder, ActionSink, GameBridge, UiActions,
                       ModelText, PlayerReader, Readiness, Reactions (vanilla emotes), Readers/* (one per screen family)
  Team/                TeamLink (raw-packet transport + discovery + relay), TeamNet (every game call of the link),
                       TeamPatches (Harmony hooks), SpeechBubbles
  Ui/                  Overlay (in-game control panel; team message toasts and unread counts)
  Api/                 LocalHttpApi (optional, loopback only: localhost, plus 127.0.0.1 where the OS allows it)
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
  ├─ DecisionEngine.DecideAsync (worker, on its own copy of the menu: GameSnapshot.WithOwnMenu):
  │     plan step? → owner rules → trivial? → LLM (+repair) → fallback
  │     (cancelled when the user pauses or switches mode while the model thinks)
  ├─ re-capture (waits out transient "animating"/"settling"), re-validate the chosen action by SemanticKey,
  │     re-check pause / mode / a freshly pinned route
  ├─ TeamDelivery.Send (text to teammates with the mod, the kind's emote to the others) / map ping
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
  teammates", saved runs of disallowed characters: `OwnerRules` removes the actions before
  any model sees the menu — the built-in one or an external agent (see "Owner rules and external agents").
- **Things the human opened are the human's.** Popups over the pause menu / settings / viewers, abandon / quit /
  disconnect confirmations, and anything outside a run when menus are not the AI's to handle get no actions.
- **No progress means stop.** The same state + action three times within the last eight steps pauses the pilot
  (catches A,A,A and A,B,A,B cycles); three failed actions in a row pause it too.

## Game versions

Steam main branch is v0.107.1, public beta is v0.111.0; they are not API compatible. The mod compiles against
either (`-p:Sts2GameVersion=0.107|0.111`), with `#if STS2_V0107` / `#if STS2_V0111` where APIs differ
(reference: `api-diff-0107-to-0111.txt` from the research). Each build ships its own manifest
(`manifest/mod_manifest.<version>.json`) with the matching `min_game_version`.

Version guard (`Core/Config/GameVersion`, `PilotRuntime`): from the first frame where the game's version can be read,
the runtime compares its major.minor with the branch the DLL was compiled for (`Diagnostics.CompiledFor`, the build's
`Sts2GameVersion` assembly metadata). Shipped branches: 0.107 and 0.111.

| Game vs. build | Example | Outcome |
| --- | --- | --- |
| Ok: same major.minor | 0.111 build on game 0.111.3 | runs |
| Untested: a newer game, and no newer shipped build exists | 0.111 build on game 0.112 | runs with a warning |
| Incompatible: an older game, or a newer one when a newer shipped build exists | 0.107 build on game 0.108+; 0.111 build on game 0.107 | AI blocked |

- Blocked (`VersionBlocked`): the pilot loop runs on a copy of the settings with mode Off (`LoopSettings`); the guard
  itself never writes the saved mode, so a matching build starts in it. Choosing Off (Control tab, `POST /mode`) or
  switching "Run anyway" on saves Off like any mode change; the MCP server's `set_mode("Off")` posts nothing while
  `/status` reports `versionMismatch`, since Off is already in effect. On the Control tab every mode button but Off is
  disabled; `GET /status` reports the effective mode Off and `versionMismatch`; `POST /act`, and `POST /mode` with any
  mode but Off, answer 409 `version_mismatch` (the MCP server checks `/status` before acting too). The notice names
  only shipped builds (`GameVersion.BuildFor`): the one for this game, else "no build yet, check for an update" (on a
  game newer than every build also the 0.111 build, untested).
- "Run anyway" (a Control tab switch, shown only while incompatible) saves the pairing as
  `control.ignoreGameVersionFor` (`GameVersion.OverrideKey`, e.g. `"game 0.111, build 0.107"`) and sets the mode to
  Off in the same save, so the AI starts only once the owner picks a mode. It holds for that pairing only: another game
  branch or build starts blocked again. Switching it off puts the loop back to Off.
- Untested, or run anyway: the AI runs; `GET /status` carries `versionWarning` and the Control tab an amber notice.
- The overlay shows a short 30 s toast whenever the notice changes (red blocked, amber warning); the full text is on
  the Control tab and in the AI button's tooltip. Diagnostics show `mode: Off (saved: <mode>)` while blocked and
  `game version: <notice> (run anyway: <key>|off)`. A version still unreadable after 600 frames runs unchecked, with a
  log warning.
- A newer branch built from source (`-p:Sts2GameVersion=0.112 -p:Sts2Dir=…`) compiles the 0.111 code against that
  install, reports 0.112 as its branch and ships the 0.111 manifest. NuGet reference builds exist for the shipped
  branches only (`CheckSts2GameVersion` stops any other).
- The 0.111 build on game 0.107 is not expected to load at all: its manifest's `min_game_version` is 0.111.0, which the
  game's mod loader checks first.

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
  Card keys (`play:`, `select:`, `deselect:`) carry variant suffixes when a copy differs from the plain card:
  `~c<cost>`, `~s<stars>`, `~<ENCHANT><amount>`, `~<AFFLICTION>[amount]`, `~retain`, `~sly`, `~+kw` / `~-kw`, and
  `~v<hash>` for base values that grew during the fight (never the description, whose numbers follow Strength).
  Two actions with the same key must be interchangeable (identical cards).
- `unpredictable: true` for anything whose outcome cannot be foreseen (draw / random / choices / screen changes).
- Potions outside combat (map, rewards, shop, rest site, event, treasure) are offered where the game allows the potion
  there, next to the screen's own actions (never on a choice overlay, and a potion alone never makes a screen
  actionable). They aim at the local player (`potion:<ID>@P:<own netId>`) or at no creature (`potion:<ID>@-`):
  teammates are never offered as targets outside combat, so potions for teammates exist only in combat, where
  "no potions for teammates" filters them. In co-op their labels name the target as combat labels do ("Use potion X →
  yourself"). At rewards with a full belt, "drink potion X (belt is full; frees a slot for the reward …)" sits next to
  discarding one, so a potion reward can be taken without wasting one. The rule-based fallback never drinks potions
  outside combat.
- Executors: re-check `GodotObject.IsInstanceValid` for every node they captured, perform the action through the
  game's own path, then wait for an observable effect (`UiActions.Expect`) and `UiActions.WaitSettled`. Return
  `ActionResult.Fail(reason)` instead of throwing; never report success when nothing happened.

## Threading helpers

- `MainThread.Run / RunAsync / Post / NextFrame / WaitUntil / Delay` (Infra/MainThread.cs). Waits count time only
  while frames advance (capped per frame), so a hidden or minimized window that stops processing frames does not turn
  pending actions into "timed out".
- `UiActions.Press(NClickableControl)` (checks enabled + visible, then `ForceClick`), `ClickHolder(NCardHolder)`
  (emits `Pressed`), `Expect`, `WaitSettled`, `GameBusy`.
- `SceneUtil.Descendants<T>`, `VisibleInTree`, `Usable`, `ByScreenPosition` (Infra/SceneUtil.cs).
- `Reflect.Get/Set/TryCall` for non-public members (cached, never throws, warns once when a member is missing).

## UI

The overlay is built in code (no scenes, no Node subclasses — Godot only calls overrides on script classes registered
by its source generators). It hangs a `CanvasLayer` (layer 128) under the scene root, uses signals
(`Pressed`, `TextSubmitted`, ...) and the per-frame `MainThread.Frame` event, and the game's CJK fonts.
Incoming team messages show as speech bubbles over the sender during a fight (`SpeechBubbles`) and as a short toast
on the overlay otherwise (also mid-fight when no bubble can show: bubbles off, sender not on screen; emotes never toast
mid-fight), with an unread count on the "AI" button and the Team tab (cleared when the Team tab is seen).

## Owner rules and external agents

`OwnerRules` (Core) is the one filter for the owner's hard rules: allowed characters, the owner's ascension, no
continuing a saved run of a disallowed character, no potions for teammates when giving is off.
- The engine applies it in `RuleDecision`, before the trivial-action check and before any model call. `PilotLoop`
  hands the engine its own copy of the menu (`GameSnapshot.WithOwnMenu`) and takes the chosen action from it, so the
  UI and the HTTP API never see a capture mutated under them.
- The HTTP API serves `OwnerRules.Filtered(snapshot, settings)`: `GET /state` (JSON and text) and `GET /prompt` show
  the filtered menu, and `POST /act` answers 403 `{"error": ..., "code": "owner_rule"}` for an action that is in the
  raw menu but was filtered out. `/state?format=text` (and `include=text`) also carries the route planner's
  `[ROUTES]` lines and the pinned route, built by the same `PromptBuilder` code as the prompt.
- `DecisionEngine.PreviewPrompt(snapshot, liveInstruction)` builds exactly the prompt the engine would send now
  (system, owner block, user with the team inbox, open requests, routes, pin and live instruction; a capture that
  cannot act gets a reply-only call, which leaves the instruction out) without side effects: nothing is marked read,
  expired or closed. It reads the inbox and requests through the same `PlanInbox` as a decision (expiry, ended
  fights, answers) and gives the same `[NO ACTION NEEDED]` reason (`ReplyOnlyReason`). `GET /prompt` returns it.
- Messages the player sends (Team tab, `POST /chat`) go through `PilotLoop.SendForPlayer`: the AI's delivery rule, and
  the AI remembers them like its own. A request stays open for the fight and round of the capture it was sent in, a
  "yes" to a teammate's request becomes a promise the AI keeps, the answered message is marked read (a reply to it from
  an AI decision that read it unanswered is not sent: one answer per question; a later decision may still follow up,
  e.g. take the yes back), and every kind but chat leaves a history line ("(owner → X)", "(sent via the API → X)").
- The pinned route is a soft rule: the engine follows it without the model unless a live instruction or a teammate's
  request needs the model, which then sees the pinned next node. For an external agent the pin is advice in the text.

## LLM failures

- Retries (`LlmHttp.WithRetries`, two at most): 408 / 409 / 425 / 429 / 5xx / 529 and network errors, with backoff
  (`Retry-After` honoured up to 60 s). A timeout is retried once at most: a model that needed longer will need it
  again, and every resend may be billed. Presets for reasoning and local models (Anthropic, OpenAI, Gemini, GLM,
  OpenRouter, Ollama, LM Studio) use a 300 s timeout, the others 120 s.
- Config errors (`LlmException.IsConfigError`: 401 / 402 / 403 / 404, and 400 / 422 whose message names a wrong
  model, a bad key or an empty balance in the common providers' wordings) become `PilotConfigException`: the pilot
  pauses with a "fix your settings" message and never falls back to the rule-based player.
- Circuit breaker (`DecisionEngine`): after 3 failed decisions in a row the model is not called for 60 s, doubling up
  to 10 min; meanwhile the rule-based fallback plays when enabled, otherwise the pilot pauses. After a cooldown the
  breaker is half-open: the first failure trips it again, a success closes it. Resume, a mode change and changed LLM
  settings reset it.
- Anthropic effort: a rejected value is remembered and the next configured one (the general effort) is used; effort
  is dropped only when that is rejected too. The UI offers only low / medium / high / xhigh / max for Anthropic.

## Team link

Raw packets over the game's transport (client→host `NetClient.SendMessageToHost`, host→client
`NetHost.SendMessageToClient`), received by Harmony prefixes on `NetHostGameService.OnPacketReceived` /
`NetClientGameService.OnPacketReceived` that consume packets starting with the `TeamWire` magic. Packets are only sent
to peers known to run SpirePilot (mod list from the handshake / hello), the host relays client↔client messages, and
every message also carries readable text so human teammates can read it. Clients find each other through the host: it
forwards each newly linked client's Hello to the other linked clients, addressed to each of them so it is answered
(also after a client leaves and rejoins), and the answers come back the same way; a client sends to any teammate
through its linked host, which checks who can receive. Inbound packets are rate-limited per sender before decoding
(and per original sender after relay); session events have their own queue that is never dropped. Every game call of
the link sits in a small non-inlined method (`TeamNet`) wrapped at the call site, so a member removed by a game update
disables one feature, not the link. Without the mod on the other side, the AI still sees teammates' full state (co-op
simulates everyone locally) and can use vanilla map pings and reaction emotes (`IGameBridge.TrySignal` →
`ReactionSynchronizer.SendLocalReaction`).

Emotes (`Core/Team/TeamEmotes`, `Game/Reactions.cs`), both ways:
- Out (`TeamDelivery.Send`, the one rule for AI messages, the Team tab and `POST /chat`): teammates who run
  SpirePilot get the text, the others the kind's emote (requests "!", ask / offer potion "?", accept thumbs up,
  decline thumbs down; chat and route suggestions have none), also for "all" in a mixed team. No text goes out when
  nobody addressed reads it (for the Team tab and `/chat`, a capture that lists none of the addressees leaves that to
  the live roster). A mixed-team message costs an AI sender one budget unit. `/chat` answers
  `{"delivered", "id", "kind", "emote", "emoteLimited", "emoteFailed"}`, or `{"delivered": false, "kind", "emote",
  "emoteLimited", "emoteFailed", "reason": "no_reader"}` without a reader (`kind`: the kind sent); `emote` is null when
  nobody needed one, the kind has none, an AI sender (`from_ai`, the default) has spent its budget or showed the same
  kind to the same recipient in the last 3 minutes (`emoteLimited`), or the game could not show it (`emoteFailed`: not
  in a run, no reaction synchronizer, no frame within 2 s). A reply without a recipient goes to the asker
  (`TeamDelivery.ReplyRecipient`: an answer to a question for everyone goes to everyone, a potion offer or question
  only to the asker). A potion offer or question in answer to a potion request goes out as accept
  (`TeamDelivery.AnswerKind`, for the AI and `/chat`; so to everyone when the request was): it is the yes, its potion
  the one promised, and the asker's AI would read an offer from an AI as a counter-request needing no answer. An AI's
  offer or question in answer to anything else, sent to a teammate whose AI plays, is not kept as an open request for
  the same reason: no yes would come.
- In: a guarded Harmony postfix on the game's reaction handler (`ReactionSynchronizer.HandleReactionMessage`) turns a
  teammate's emote into a message through `TeamChatHub.Receive` (dedupe, inbox caps):
  - from a teammate without the mod, thumbs up / down are Accept / Decline of the newest request this player showed
    them as an emote (`AgentMemory.LastSignalTo`), only while it is open: never an older request they may not have
    seen. "!" / "?" become a short chat line that asks the AI to react; other emotes are context only. The message id
    hashes emote, sender, a 10-second window and the request answered: a double tap is one message, a tap after a new
    question a new answer.
  - from a linked teammate playing by hand: context only while the last capture shows a teammate without the mod (or
    no roster), since the emote is most likely meant for that teammate; with everyone linked, as above, with thumbs
    answering our newest open request to them (`LastOpenTo`). A linked AI's emotes and the local player's own never
    reach the inbox.
  - a linked teammate's "!" or "?" (its AI's too) asks a teammate without the mod something: it is noted as the newest
    question shown to everyone (`AgentMemory.SeeEmote`: `NoteSignal` with an untracked id), so a thumbs up after it
    answers none of ours.

## Co-op end of turn

After the local player readies end turn while teammates still play, the snapshot can act (`WaitReason` "waiting for
teammates") with exactly one action, `undo_end_turn` (`UndoEndPlayerTurnAction` through the action queue
synchronizer). The pilot takes it only when a teammate's request needs it; it is never offered in single player.

Conversation rules (Core, `DecisionEngine` / `PilotLoop` / `AgentMemory`):
- Requests, questions and human chat are actionable; accept/decline answers and AI chat are context only, and an
  AI's counter-request (a request with `reply_to`) is never actionable — two AIs cannot keep each other talking.
- The AI remembers its own open requests/offers (not repeated; a "yes" to its offer is actionable), and accepted
  requests until fulfilled (a potion used on that player) or until their round / fight ends. A "yes" read by a
  decision is a promise: still unread (the decision was advice, dropped or a fallback) it is listed once, as
  "accepted earlier" with its potion.
- While it cannot act (turn ended, enemy turn) a teammate's request still gets an answer (reply-only call); a "yes" to
  its offer neither wakes it then nor goes into that call: it stays unread, its offer open, for the first capture that
  can act (`PlanInbox`), where the yes asks for that; with only "undo end turn" on the menu the model may take the turn
  back to help, never on its own.
- Teammates without SpirePilot are marked in the state; messages to them become a vanilla reaction emote, and
  their emotes come back as accept / decline / chat (see "Team link").
- After the model reacted to a teammate's "!" or "?" (once that decision ran: `MarkHandled`), the same emote from them
  is context only for 45 s; a second "yes" from a teammate to the same question asks nothing new. A turn plan goes on
  past an unread "!" / "?" with card plays, but a next step that is a plan boundary (end turn, a potion, an
  unpredictable card) waits for the model.
- A potion question to everyone stays open until every teammate answered; each "yes" is its own promise.
- In OutOfCombatOnly the fight is the human's: its combat-bound requests (block, debuff, focus, potion requests, "!" /
  "?") are marked read once it ends (`PilotLoop.NoteHumanFight`), never answered on the rewards screen. Requests and
  promises made in a fight the AI did not play (the owner's, from the Team tab or `/chat`) end at its first decision
  after it (`AgentMemory.NoteCombat` looks at every decision outside a fight).
- Inbox is bounded (5 unread per sender, 20 total, newest 8 in a prompt) and expires on the local clock (unread
  messages after 3 min, requests after 5 min or when their fight / round ends); teammate text is one line inside a
  fence it cannot close, and cannot forge section headers.
