"""Tests for the SpirePilot MCP server against a fake of the mod's HTTP API (stdlib http.server in a thread).

fixtures/*.json were serialized by the mod's own model classes with LocalHttpApi's JSON options, and
fixtures/*.txt are the C# StateFormatter's output for them (sections.json combines every screen section at once,
for formatter coverage only). Tool functions are called directly, not over stdio.
"""

import asyncio
import copy
import json
import socket
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import spirepilot_mcp as srv  # noqa: E402

FIXTURES = Path(__file__).parent / "fixtures"


def load(name: str) -> dict:
    return json.loads((FIXTURES / f"{name}.json").read_text(encoding="utf-8"))


def run(coro):
    return asyncio.run(coro)


MIKA = 76561198000000002


class FakeMod:
    """State of the emulated mod; tests mutate it directly."""

    def __init__(self):
        self.state: dict | None = load("combat")
        self.sequence = self.state["sequence"]
        self.mode = "Off"
        self.paused = False
        self.token = ""
        self.busy: list[bool] = []  # one entry per upcoming capture: True = canAct false ("enemy turn")
        self.state_calls = 0
        self.act_result = (True, "")
        self.on_act = None  # callback(action) that changes self.state after a successful act
        self.executed: list[dict] = []
        self.posts: list[tuple[str, dict | None]] = []
        self.rate_limited = False
        self.delivered = True
        self.chat = [
            {
                "time": "2026-10-29T14:02:51.1234567+08:00",
                "incoming": True,
                "delivered": True,
                "from": "Mika",
                "fromAi": True,
                "kind": "RequestPotion",
                "text": "Could you spare a potion? I need help.",
                "to": 0,
            },
            {
                "time": "2026-10-29T14:03:05.0000000+08:00",
                "incoming": False,
                "delivered": False,
                "from": "Yang",
                "fromAi": True,
                "kind": "OfferPotion",
                "text": "I have Block Potion.",
                "to": MIKA,
            },
        ]
        self.compendium = {
            "cards": [
                {
                    "key": "",
                    "id": "STRIKE_IRONCLAD",
                    "name": "Strike",
                    "upgraded": False,
                    "cost": 1,
                    "type": "Attack",
                    "rarity": "Basic",
                    "target": "AnyEnemy",
                    "description": "Deal [gold]6[/gold] damage.",
                    "keywords": [],
                },
                {
                    "key": "",
                    "id": "BASH",
                    "name": "Bash",
                    "upgraded": False,
                    "cost": 2,
                    "type": "Attack",
                    "rarity": "Basic",
                    "target": "AnyEnemy",
                    "description": "Deal 8 damage. Apply 2 Vulnerable.",
                    "keywords": [],
                },
            ],
            "relics": [{"id": "VAJRA", "name": "Vajra", "description": "Gain 1 Strength."}],
            "potions": [
                {
                    "slot": -1,
                    "id": "FIRE_POTION",
                    "name": "Fire Potion",
                    "description": "Deal 20 damage.",
                    "target": "AnyEnemy",
                    "rarity": "Common",
                    "usableNow": False,
                }
            ],
            "powers": [
                {
                    "id": "VULNERABLE_POWER",
                    "name": "Vulnerable",
                    "amount": 0,
                    "isDebuff": True,
                    "description": "Takes 50% more damage.",
                }
            ],
            "characters": [{"id": "IRONCLAD", "name": "Ironclad"}, {"id": "SILENT", "name": "Silent"}],
        }

    def status(self) -> dict:
        return {
            "state": "Off" if self.mode == "Off" else "Waiting",
            "text": "AI is off",
            "mode": self.mode,
            "paused": self.paused,
            "llmCalls": 3,
            "inputTokens": 12000,
            "outputTokens": 900,
            "cachedTokens": 8000,
            "profile": "Claude",
        }

    def snapshot(self) -> dict | None:
        if self.state is None:
            return None
        self.state_calls += 1
        self.sequence += 1
        snap = copy.deepcopy(self.state)
        snap["sequence"] = self.sequence
        if self.busy and self.busy.pop(0):
            snap["canAct"], snap["waitReason"] = False, "enemy turn"
        return snap

    def handle(self, method: str, path: str, query: dict, body, auth: str | None):
        """Mirrors LocalHttpApi.HandleAsync; returns (status, payload)."""
        if self.token and auth != "Bearer " + self.token:
            return 401, {"error": "missing or wrong bearer token"}
        if method == "POST":
            self.posts.append((path, body))
        if (method, path) == ("GET", "/status"):
            return 200, self.status()
        if (method, path) == ("GET", "/state"):
            snap = self.snapshot()
            return (503, {"error": "no game state yet"}) if snap is None else (200, snap)
        if (method, path) == ("GET", "/prompt"):
            if self.state is None:
                return 503, {"error": "no game state yet"}
            return 200, {
                "system": "You are SpirePilot.",
                "owner": "## OWNER SETTINGS\n- Route style: balanced",
                "user": "[DECISION] combat_turn\n[MENU]\na0 | Play Bash",
                "schema": {"type": "object"},
            }
        if (method, path) == ("GET", "/compendium"):
            kind = query.get("kind", ["cards"])[0]
            if kind not in self.compendium:
                return 400, {"error": "kind must be cards, relics, potions, powers or characters"}
            return 200, self.compendium[kind]
        if (method, path) == ("POST", "/act"):
            if self.mode not in ("Off", "Advisor") and not self.paused:
                return 409, {"error": "the built-in pilot is in control; set mode Off or Advisor first (POST /mode)"}
            snap = self.snapshot()
            if snap is None:
                return 503, {"error": "no game state yet"}
            if not snap["canAct"]:
                return 409, {"error": "cannot act now: " + snap.get("waitReason", "")}
            id_, key = (body or {}).get("id"), (body or {}).get("semantic_key")
            match = [
                a
                for a in snap["actions"]
                if (a["id"].lower() == id_.lower() if id_ is not None else a["semanticKey"] == key)
            ]
            if not match:
                return 404, {
                    "error": "no such legal action",
                    "legal": [{"id": a["id"], "label": a["label"]} for a in snap["actions"]],
                }
            action = match[0]
            ok, message = self.act_result
            self.executed.append(action)
            if ok and self.on_act:
                self.on_act(action)
            return (200 if ok else 422), {
                "success": ok,
                "message": message,
                "action": {"id": action["id"], "kind": action["kind"], "label": action["label"]},
            }
        if (method, path) == ("POST", "/mode"):
            modes = {m.lower(): m for m in ("FullAuto", "CombatOnly", "OutOfCombatOnly", "Advisor", "Off")}
            if str((body or {}).get("mode", "")).lower() not in modes:
                return 400, {"error": "unknown mode"}
            self.mode = modes[body["mode"].lower()]
            return 200, self.status()
        if (method, path) == ("POST", "/instruction"):
            return 200, {"ok": True}
        if (method, path) == ("GET", "/chat"):
            return 200, self.chat
        if (method, path) == ("POST", "/chat"):
            if not (body or {}).get("text", "").strip():
                return 400, {"error": "text required"}
            if self.rate_limited:
                return 429, {"error": "rate limited"}
            return 200, {"delivered": self.delivered}
        return 404, {"error": "unknown endpoint"}


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self._serve("GET")

    def do_POST(self):
        self._serve("POST")

    def _serve(self, method: str):
        url = urlsplit(self.path)
        body = None
        if method == "POST":
            raw = self.rfile.read(int(self.headers.get("Content-Length") or 0))
            body = json.loads(raw) if raw else None
        status, payload = self.server.fake.handle(
            method, url.path.rstrip("/"), parse_qs(url.query), body, self.headers.get("Authorization")
        )
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def log_message(self, *args):
        pass


@pytest.fixture
def fake(monkeypatch):
    mod = FakeMod()
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.fake = mod
    thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.02}, daemon=True)
    thread.start()
    monkeypatch.setenv("SPIREPILOT_URL", f"http://127.0.0.1:{server.server_address[1]}/")
    monkeypatch.delenv("SPIREPILOT_TOKEN", raising=False)
    monkeypatch.setattr(srv, "_last_seen", None)
    monkeypatch.setattr(srv, "POLL_SECONDS", 0.01)
    yield mod
    server.shutdown()
    server.server_close()


def act_posts(mod: FakeMod) -> list:
    return [body for path, body in mod.posts if path == "/act"]


# ------------------------------------------------------------------------------------------- formatter parity


@pytest.mark.parametrize("name", ["combat", "map", "sections"])
def test_format_state_matches_csharp_state_formatter(name):
    expected = (FIXTURES / f"{name}.txt").read_text(encoding="utf-8")
    assert srv.format_state(load(name)) == expected


def test_clean_strips_markup_like_csharp():
    assert srv._clean("Deal [gold]6[/gold]  damage.\n") == "Deal 6 damage."
    assert srv._clean("Pick [or not\nat all") == "Pick [or not\nat all"
    assert srv._clean(None) == ""


# ------------------------------------------------------------------------------------------- tools


def test_all_tools_are_registered_with_descriptions():
    tools = {t.name: t for t in run(srv.mcp.list_tools())}
    assert set(tools) == {
        "status",
        "get_state",
        "list_actions",
        "act",
        "set_mode",
        "give_instruction",
        "read_chat",
        "send_chat",
        "get_prompt",
        "compendium",
    }
    assert all(len(t.description or "") > 80 for t in tools.values())
    assert tools["get_state"].annotations.model_dump(by_alias=True)["readOnlyHint"] is True
    assert "set_mode" in srv.INSTRUCTIONS and "get_state" in srv.INSTRUCTIONS


def test_status(fake):
    text = run(srv.status())
    assert "mode Off" in text and "3 LLM calls" in text and "You can act" in text
    fake.mode = "FullAuto"
    assert 'set_mode("Off")' in run(srv.status())


def test_get_state_text_has_state_and_menu(fake):
    text = run(srv.get_state())
    assert text.startswith("Screen: Combat | Act 1 (Overgrowth) | Floor 7")
    assert "Threat: incoming 12, unblocked 12" in text
    assert "Menu (act with ONE id" in text
    assert "a0 | Play Bash → Nibbit (10 dmg, 2 Vulnerable)\n" in text
    assert "a4 | Play Pommel Strike+ → Nibbit (16 dmg, draw 2) [unpredictable]" in text
    assert text.rstrip().endswith("a10 | End turn")


def test_get_state_json_is_the_raw_snapshot(fake):
    data = json.loads(run(srv.get_state(format="json")))
    assert data["actions"][0]["semanticKey"] == "play:BASH@E:NIBBIT#0"
    assert data["players"][1]["netId"] == MIKA


def test_get_state_waits_until_the_game_is_ready(fake):
    fake.busy = [True, True, True]
    assert run(srv.get_state()).startswith("WAITING: enemy turn.")
    text = run(srv.get_state(wait_seconds=5))
    assert not text.startswith("WAITING") and fake.state_calls == 4


def test_get_state_without_game_state(fake):
    fake.state = None
    with pytest.raises(srv.SpirePilotError, match="no state yet"):
        run(srv.get_state())


def test_list_actions_shows_kinds_and_semantic_keys(fake):
    text = run(srv.list_actions())
    assert text.startswith("Screen: Combat\n")
    assert "a7 | Use potion Fire Potion → Nibbit (30 dmg)  (UsePotion; key potion:FIRE_POTION@E:NIBBIT#0)" in text


def test_act_by_id_sends_semantic_key_and_returns_new_state(fake):
    fake.on_act = lambda action: setattr(fake, "state", load("map"))
    run(srv.get_state())
    text = run(srv.act(id="A0"))
    assert act_posts(fake) == [{"semantic_key": "play:BASH@E:NIBBIT#0"}]
    assert text.startswith("Done: Play Bash → Nibbit (10 dmg, 2 Vulnerable)\n")
    assert "Screen: Map" in text and "a1 | Travel to 3,7 (Rest site)" in text
    # The returned menu becomes the reference for the next act.
    run(srv.act(id="a1"))
    assert act_posts(fake)[-1] == {"semantic_key": "map:3,7"}


def test_act_refuses_stale_id_and_points_to_the_new_one(fake):
    run(srv.get_state())  # a2 = Strike → Nibbit
    # Someone plays Bash in the game: both Bash actions disappear and the ids shift by two.
    actions = [a for a in fake.state["actions"] if not a["semanticKey"].startswith("play:BASH")]
    for i, a in enumerate(actions):
        a["id"] = f"a{i}"
    fake.state["actions"] = actions
    with pytest.raises(srv.SpirePilotError) as err:
        run(srv.act(id="a2"))
    msg = str(err.value)
    assert "Stale id: a2 was 'Play Strike → Nibbit (10 dmg)'" in msg
    assert "now a2 is 'Play Pommel Strike+ → Nibbit (16 dmg, draw 2)'" in msg
    assert "The action you meant is now a0." in msg and "Nothing was done." in msg
    assert "a0 | Play Strike → Nibbit (10 dmg)" in msg  # current menu included
    assert act_posts(fake) == []
    # The refusal showed the current menu, so its ids are valid now.
    run(srv.act(id="a0"))
    assert act_posts(fake) == [{"semantic_key": "play:STRIKE_IRONCLAD@E:NIBBIT#0"}]


def test_act_refuses_ids_never_shown_or_gone(fake):
    with pytest.raises(srv.SpirePilotError, match="have not seen a menu yet"):
        run(srv.act(id="a1"))
    with pytest.raises(srv.SpirePilotError, match="a99 was not in the last menu"):
        run(srv.act(id="a99"))
    fake.state["actions"] = fake.state["actions"][:3]
    with pytest.raises(srv.SpirePilotError, match="a10 no longer exists"):
        run(srv.act(id="a10"))
    assert act_posts(fake) == []


def test_act_by_semantic_key(fake):
    run(srv.act(semantic_key="end_turn"))
    assert act_posts(fake) == [{"semantic_key": "end_turn"}]
    with pytest.raises(srv.SpirePilotError, match="No legal action has semantic_key 'map:1,7'"):
        run(srv.act(semantic_key="map:1,7"))


def test_act_needs_exactly_one_argument(fake):
    with pytest.raises(srv.SpirePilotError, match="exactly one"):
        run(srv.act())
    with pytest.raises(srv.SpirePilotError, match="exactly one"):
        run(srv.act(id="a0", semantic_key="end_turn"))


def test_act_409_when_the_builtin_pilot_is_in_control(fake):
    fake.mode = "FullAuto"
    run(srv.get_state())
    with pytest.raises(srv.SpirePilotError, match=r'built-in SpirePilot AI is in control.*set_mode\("Off"\)'):
        run(srv.act(id="a0"))
    assert fake.executed == []
    fake.paused = True  # a paused pilot does not block external actions
    run(srv.act(id="a0"))
    assert len(fake.executed) == 1


def test_act_while_the_game_is_busy(fake):
    run(srv.get_state())
    fake.busy = [True]
    with pytest.raises(srv.SpirePilotError, match="Cannot act right now: enemy turn"):
        run(srv.act(id="a0"))
    assert act_posts(fake) == []


def test_act_409_from_the_mod_when_it_becomes_busy(fake):
    run(srv.get_state())
    fake.busy = [False, True]  # our check passes, the mod's own capture says busy
    with pytest.raises(srv.SpirePilotError, match="The game refused: cannot act now: enemy turn"):
        run(srv.act(id="a0"))


def test_act_failure_from_the_game(fake):
    fake.act_result = (False, "card is no longer playable")
    run(srv.get_state())
    with pytest.raises(srv.SpirePilotError, match="could not perform 'Play Bash.*card is no longer playable"):
        run(srv.act(id="a0"))


def test_set_mode(fake):
    fake.mode = "FullAuto"
    text = run(srv.set_mode("Advisor"))
    assert fake.mode == "Advisor" and "mode Advisor" in text and "You can act" in text
    assert fake.posts[-1] == ("/mode", {"mode": "Advisor"})


def test_give_instruction(fake):
    text = run(srv.give_instruction("Use the Fire Potion this turn"))
    assert fake.posts[-1] == ("/instruction", {"text": "Use the Fire Potion this turn"})
    assert "Use the Fire Potion this turn" in text and "mode is Off" in text
    fake.mode = "FullAuto"
    assert "mode is" not in run(srv.give_instruction("take the elite"))
    assert run(srv.give_instruction("")) == "Pending instruction cleared."


def test_read_chat(fake):
    run(srv.get_state())  # player names come from the last snapshot
    lines = run(srv.read_chat()).splitlines()
    assert lines[0] == "14:02:51 ← Mika (AI) → everyone | request_potion: Could you spare a potion? I need help."
    assert lines[1].startswith("14:03:05 → Yang (AI) → Mika | offer_potion: I have Block Potion.")
    assert lines[1].endswith("[not delivered: no teammate runs SpirePilot]")
    assert len(run(srv.read_chat(limit=1)).splitlines()) == 1
    fake.chat = []
    assert run(srv.read_chat()) == "No team messages yet."


def test_send_chat(fake):
    text = run(
        srv.send_chat(
            "Can you apply Vulnerable to the Nibbit?",
            kind="request_debuff",
            recipient="Mika",
            debuff="Vulnerable",
            target="e0",
        )
    )
    assert fake.posts[-1] == (
        "/chat",
        {
            "text": "Can you apply Vulnerable to the Nibbit?",
            "kind": "request_debuff",
            "to": "Mika",
            "debuff": "Vulnerable",
            "target": "e0",
        },
    )
    assert text.startswith("Sent to Mika (request_debuff)")
    fake.delivered = False
    assert "not delivered" in run(srv.send_chat("hi"))
    fake.rate_limited = True
    with pytest.raises(srv.SpirePilotError, match="limit on AI messages"):
        run(srv.send_chat("hi again"))
    with pytest.raises(srv.SpirePilotError, match="text is required"):
        run(srv.send_chat("  "))


def test_get_prompt(fake):
    assert run(srv.get_prompt(part="system")) == "You are SpirePilot.\n\n## OWNER SETTINGS\n- Route style: balanced"
    user = run(srv.get_prompt(part="user"))
    assert user.startswith("[DECISION] combat_turn") and "call act" in user
    both = run(srv.get_prompt())
    assert both.startswith("You are SpirePilot.") and "[MENU]" in both
    fake.state = None
    with pytest.raises(srv.SpirePilotError, match="no state yet"):
        run(srv.get_prompt())


def test_compendium(fake):
    text = run(srv.compendium("cards"))
    assert text.splitlines() == [
        "2 cards:",
        "STRIKE_IRONCLAD | Strike [1] Attack, AnyEnemy, Basic: Deal 6 damage.",
        "BASH | Bash [2] Attack, AnyEnemy, Basic: Deal 8 damage. Apply 2 Vulnerable.",
    ]
    assert run(srv.compendium("cards", "VULNERABLE bash")).startswith("1 cards matching 'VULNERABLE bash':\nBASH")
    assert "VULNERABLE_POWER | Vulnerable (debuff): Takes 50% more damage." in run(srv.compendium("powers"))
    assert "FIRE_POTION | Fire Potion (Common) [AnyEnemy]: Deal 20 damage." in run(srv.compendium("potions"))
    assert run(srv.compendium("characters", "silent")) == "1 characters matching 'silent':\nSILENT | Silent"
    fake.compendium["relics"] = [{"id": f"R{i}", "name": "Relic"} for i in range(200)]
    text = run(srv.compendium("relics"))
    assert "showing the first 150" in text and len(text.splitlines()) == 151


def test_bearer_token(fake, monkeypatch):
    fake.token = "s3cret"
    with pytest.raises(srv.SpirePilotError, match="SPIREPILOT_TOKEN"):
        run(srv.status())
    monkeypatch.setenv("SPIREPILOT_TOKEN", "s3cret")
    assert "mode Off" in run(srv.status())


def test_proxy_environment_is_ignored(fake, monkeypatch):
    monkeypatch.setenv("HTTP_PROXY", "http://127.0.0.1:9")
    monkeypatch.setenv("http_proxy", "http://127.0.0.1:9")
    monkeypatch.setenv("NO_PROXY", "")
    assert "mode Off" in run(srv.status())


def test_old_mod_without_endpoint(fake, monkeypatch):
    original = fake.handle
    monkeypatch.setattr(
        fake,
        "handle",
        lambda method, path, *rest: (404, {"error": "unknown endpoint"})
        if path == "/compendium"
        else original(method, path, *rest),
    )
    with pytest.raises(srv.SpirePilotError, match="no GET /compendium.kind=cards endpoint; update the mod"):
        run(srv.compendium("cards"))


def closed_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


@pytest.mark.parametrize(
    "call",
    [
        lambda: srv.status(),
        lambda: srv.get_state(),
        lambda: srv.list_actions(),
        lambda: srv.act(id="a0"),
        lambda: srv.set_mode("Off"),
        lambda: srv.give_instruction("x"),
        lambda: srv.read_chat(),
        lambda: srv.send_chat("x"),
        lambda: srv.get_prompt(),
        lambda: srv.compendium("cards"),
    ],
    ids=[
        "status",
        "get_state",
        "list_actions",
        "act",
        "set_mode",
        "give_instruction",
        "read_chat",
        "send_chat",
        "get_prompt",
        "compendium",
    ],
)
def test_connection_refused_explains_how_to_enable_the_api(monkeypatch, call):
    url = f"http://127.0.0.1:{closed_port()}"
    monkeypatch.setenv("SPIREPILOT_URL", url)
    with pytest.raises(srv.SpirePilotError) as err:
        run(call())
    msg = str(err.value)
    assert msg.startswith(f"Cannot reach SpirePilot at {url}") and "Log tab" in msg and "SPIREPILOT_URL" in msg


def test_timeout_is_reported(monkeypatch):
    with socket.socket() as listener:  # accepts the connection but never answers
        listener.bind(("127.0.0.1", 0))
        listener.listen()
        monkeypatch.setenv("SPIREPILOT_URL", f"http://127.0.0.1:{listener.getsockname()[1]}")
        monkeypatch.setattr(srv, "TIMEOUT", 0.2)
        with pytest.raises(srv.SpirePilotError, match="did not answer GET /status within 0.2s"):
            run(srv.status())
