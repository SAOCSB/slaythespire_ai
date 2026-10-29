using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Models;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;
using SpirePilot.Game;
using SpirePilot.Infra;
using SpirePilot.Team;

namespace SpirePilot.Api;

/// <summary>
/// Optional local HTTP API (loopback only, off by default) so external AI tools — scripts, MCP servers, other agents —
/// can read the full game state and act through the same legal-action menu the built-in pilot uses.
///
///   GET  /status              pilot status, effective mode, token usage, last error; "versionMismatch" while this build
///                             is incompatible with the game (the AI is off), "versionWarning" while it runs untested
///   GET  /state               fresh snapshot (state + legal actions); ?format=text → the pilot's state text, routes
///                             and menu (text/plain); ?include=text → the JSON plus that text as "text", from the same
///                             capture. The menu is the built-in pilot's: the owner's rules already removed actions.
///   GET  /prompt              exactly what the built-in pilot would send now (owner rules, inbox, open requests,
///                             routes, pin, live instruction); nothing is marked read
///   POST /act    {"semantic_key":"..."} or {"id":"a3","sequence":123}   execute a legal action (pilot must be Off /
///                             Advisor / paused). With "sequence" (the snapshot the id was read from) an id whose menu
///                             changed since is refused with 409 "stale" instead of running whatever has that id now;
///                             an action an owner rule removed is refused with 403 "owner_rule".
///   POST /mode   {"mode":"FullAuto"|"CombatOnly"|"OutOfCombatOnly"|"Advisor"|"Off"}
///   POST /instruction {"text":"..."}                     one-shot instruction for the built-in pilot
///   GET  /chat                recent team chat
///   POST /chat   {"text":"...","to":"all","kind":"chat","potion":..,"debuff":..,"target":..,"reply_to":..,"from_ai":true}
///                             → {"delivered", "id"}; in co-op, when nobody addressed runs SpirePilot, nothing is sent
///                             and the kind's vanilla emote is shown instead: {"delivered": false, "emote", "reason"}
///   GET  /compendium?kind=cards|relics|potions|powers|characters   every game model with localized text
///   GET  /diagnostics         writes a diagnostics-*.md report (as the panel's button does) and returns it (markdown;
///                             no secrets); header X-Diagnostics-File: its path (non-ASCII percent-encoded as UTF-8)
/// With a token configured, every request needs "Authorization: Bearer &lt;token&gt;". Requests from web pages are
/// refused (any Origin header, a Host other than loopback), and POST bodies must be JSON (Content-Type
/// application/json, at most 64 KB), so a browser can never drive the game.
/// Error bodies are {"error": "...", "code": "..."}; 409 codes: "pilot_active", "busy", "stale", "version_mismatch".
/// </summary>
internal sealed class LocalHttpApi : IDisposable
{
    private const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Prefix sets tried in order. On Windows, http.sys only lets elevated processes register http://127.0.0.1:port/,
    /// while http://localhost:port/ is allowed (both are loopback only); elsewhere both work together.
    /// </summary>
    private static readonly string[][] PrefixPlans =
    {
        new[] { "localhost", "127.0.0.1" },
        new[] { "localhost" },
        new[] { "127.0.0.1" },
    };

    private HttpListener? _listener;
    private Thread? _thread;
    private volatile bool _running;

    public int Port { get; }
    public string Token { get; set; }

    /// <summary>The base URL actually listened on (after <see cref="Start"/>).</summary>
    public string Url { get; private set; } = "";

    public LocalHttpApi(int port, string token)
    {
        Port = port;
        Token = token ?? "";
    }

    public void Start()
    {
        var failures = new List<string>();
        foreach (var hosts in PrefixPlans)
        {
            var listener = new HttpListener();
            try
            {
                foreach (var host in hosts) listener.Prefixes.Add($"http://{host}:{Port}/");
                listener.Start();
                _listener = listener;
                Url = $"http://{hosts[0]}:{Port}/";
                if (failures.Count > 0) ModLog.Info("HTTP API: fell back after " + string.Join("; ", failures));
                break;
            }
            catch (Exception ex)
            {
                failures.Add($"{string.Join(" + ", hosts)}: {ex.Message}");
                try { listener.Close(); } catch (Exception) { /* never started */ }
            }
        }
        if (_listener == null)
            throw new InvalidOperationException($"cannot listen on port {Port} (" + string.Join("; ", failures) + ")");
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SpirePilot HTTP API" };
        _thread.Start();
        ModLog.Info($"HTTP API listening on {string.Join(", ", _listener.Prefixes)}");
    }

    /// <summary>Requests are served on worker threads; nothing to do per frame.</summary>
    public void Pump() { }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); _listener?.Close(); } catch (Exception) { }
    }

    private void Loop()
    {
        var listener = _listener!;
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (Exception) { if (!_running) return; continue; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            var req = ctx.Request;
            // CSRF / DNS rebinding: browsers always send Origin on cross-site and on POST requests; scripts and MCP
            // servers do not. A Host that is not loopback means a page reached us through a rebound name.
            if (req.Headers["Origin"] != null)
            {
                await Send(res, 403, Error("requests from web pages are not allowed", "forbidden"));
                return;
            }
            if (!LoopbackHost(req.Headers["Host"]))
            {
                await Send(res, 403, Error("only loopback host names are accepted", "forbidden"));
                return;
            }
            if (!string.IsNullOrEmpty(Token) && req.Headers["Authorization"] != "Bearer " + Token)
            {
                await Send(res, 401, Error("missing or wrong bearer token", "unauthorized"));
                return;
            }
            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = req.HttpMethod.ToUpperInvariant();
            JsonNode? body = null;
            if (method == "POST")
            {
                if (!IsJson(req.ContentType))
                {
                    await Send(res, 415, Error("Content-Type must be application/json", "unsupported_media_type"));
                    return;
                }
                if (req.ContentLength64 > MaxBodyBytes)
                {
                    await Send(res, 413, Error("body too large (64 KB max)", "too_large"));
                    return;
                }
                var text = await ReadBounded(req.InputStream, MaxBodyBytes);
                if (text == null)
                {
                    await Send(res, 413, Error("body too large (64 KB max)", "too_large"));
                    return;
                }
                body = text.Length > 0 ? JsonNode.Parse(text) : null;
            }

            switch ((method, path))
            {
                case ("GET", ""):
                    await Send(res, 200, new { mod = PilotRuntime.ModId, version = PilotRuntime.Version, endpoints = new[] { "/status", "/state", "/prompt", "/act", "/mode", "/instruction", "/chat", "/compendium", "/diagnostics" } });
                    break;
                case ("GET", "/status"):
                    await Send(res, 200, StatusObject());
                    break;
                case ("GET", "/state"):
                    await State(res, req.QueryString["format"], req.QueryString["include"]);
                    break;
                case ("GET", "/prompt"):
                {
                    var snap = await PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
                    if (snap == null) { await Send(res, 503, Error("no game state yet", "no_state")); break; }
                    var (system, owner, user) = PilotRuntime.Engine.PreviewPrompt(snap, PilotRuntime.Loop.LiveInstruction);
                    await Send(res, 200, new { system, owner, user, schema = JsonNode.Parse(DecisionParser.Schema) });
                    break;
                }
                case ("GET", "/compendium"):
                {
                    var kind = (req.QueryString["kind"] ?? "cards").ToLowerInvariant();
                    var list = await CompendiumAsync(kind);
                    await Send(res, list == null ? 400 : 200, list ?? Error("kind must be cards, relics, potions, powers or characters", "bad_request"));
                    break;
                }
                case ("GET", "/diagnostics"):
                {
                    // Writes the same diagnostics-*.md file as the panel's button: the file bug reports attach.
                    var (report, file) = await Diagnostics.WriteAsync();
                    if (file != null) res.AddHeader("X-Diagnostics-File", HeaderValue(file));
                    await SendText(res, 200, report, "text/markdown; charset=utf-8");
                    break;
                }
                case ("POST", "/act"):
                    await Act(res, body);
                    break;
                case ("POST", "/mode"):
                {
                    var mode = body?["mode"]?.ToString();
                    // Names only: Enum.TryParse also takes numbers, and "9" would become an undefined mode.
                    if (string.IsNullOrWhiteSpace(mode) || mode.Trim().Any(char.IsDigit) ||
                        !Enum.TryParse<PilotMode>(mode, true, out var m) || !Enum.IsDefined(m))
                    {
                        await Send(res, 400, Error("unknown mode (use FullAuto, CombatOnly, OutOfCombatOnly, Advisor or Off)", "bad_request"));
                        break;
                    }
                    if (m != PilotMode.Off && PilotRuntime.VersionProblemEnglish is { } problem)
                    {
                        await Send(res, 409, Error(problem, "version_mismatch"));
                        break;
                    }
                    string? saveError = null;
                    try { PilotRuntime.SetMode(m); }
                    catch (Exception ex) { saveError = ex.Message; } // the mode applies all the same
                    var status = JsonSerializer.SerializeToNode(StatusObject(), Json)!.AsObject();
                    if (saveError != null) status["saveError"] = "settings file not written: " + saveError;
                    await Send(res, 200, status);
                    break;
                }
                case ("POST", "/instruction"):
                    PilotRuntime.Loop.SetLiveInstruction(body?["text"]?.ToString());
                    await Send(res, 200, new { ok = true });
                    break;
                case ("GET", "/chat"):
                    await Send(res, 200, PilotRuntime.Team.Recent(50).Select(e => new
                    {
                        id = e.Message.Id,
                        time = e.LocalTime,
                        incoming = e.Incoming,
                        delivered = e.Delivered,
                        from = e.Message.FromName,
                        fromAi = e.Message.FromAi,
                        kind = DecisionParser.KindToWire(e.Message.Kind),
                        text = e.Message.Text,
                        to = e.Message.To,
                        replyTo = e.Message.ReplyTo,
                        target = e.Message.Target,
                        potion = e.Message.Potion,
                        debuff = e.Message.Debuff,
                    }).ToList());
                    break;
                case ("POST", "/chat"):
                    await Chat(res, body);
                    break;
                default:
                    await Send(res, 404, Error("unknown endpoint", "not_found"));
                    break;
            }
        }
        catch (JsonException ex)
        {
            try { await Send(res, 400, Error("invalid JSON: " + ex.Message, "bad_json")); } catch (Exception) { }
        }
        catch (Exception ex)
        {
            ModLog.Warn("HTTP API request failed: " + ex.Message);
            try { await Send(res, 500, Error(ex.Message, "internal")); } catch (Exception) { }
        }
    }

    // ---------------------------------------------------------------- endpoints

    private static async Task State(HttpListenerResponse res, string? format, string? include)
    {
        var raw = await PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
        if (raw == null)
        {
            await Send(res, 503, Error("no game state yet", "no_state"));
            return;
        }
        // The menu the built-in pilot chooses from: the owner's hard rules removed what it may not do.
        var settings = PilotRuntime.Settings;
        var snap = OwnerRules.Filtered(raw, settings);
        if (string.Equals(format, "text", StringComparison.OrdinalIgnoreCase))
        {
            await SendText(res, 200, StateText(snap, settings), "text/plain; charset=utf-8");
            return;
        }
        if (string.Equals(include, "text", StringComparison.OrdinalIgnoreCase))
        {
            // Same capture for both, so the ids in the text are the ids in the JSON menu.
            var node = JsonSerializer.SerializeToNode(snap, Json) as JsonObject ?? new JsonObject();
            node["text"] = StateText(snap, settings);
            await Send(res, 200, node);
            return;
        }
        await Send(res, 200, snap);
    }

    /// <summary>
    /// The state text the built-in pilot's prompt uses, its route lines on the map, then its menu ("aN | label" lines,
    /// as in [MENU]).
    /// </summary>
    private static string StateText(GameSnapshot snap, PilotSettings settings)
    {
        var sb = new StringBuilder();
        if (!snap.CanAct) sb.Append("WAITING: ").Append(snap.WaitReason ?? "the game is busy").Append('\n');
        else if (snap.Actions.Count > 0 && snap.Actions.All(a => a.Kind == ActionKind.UndoEndTurn))
            // Co-op, turn already ended: acting is possible, but only to take the end turn back.
            sb.Append("NOTE: ").Append(snap.WaitReason ?? "waiting for teammates")
              .Append(". You already ended your turn; undo it only if a teammate needs you to act this turn.\n");
        var state = Formatter(settings).Format(snap);
        sb.Append(state);
        if (state.Length > 0 && !state.EndsWith('\n')) sb.Append('\n');
        var (routes, pinnedNext) = DecisionEngine.Routes(snap, settings);
        PromptBuilder.AppendRoutes(sb, routes, pinnedNext);
        sb.Append("Menu:\n");
        foreach (var a in snap.Actions)
        {
            sb.Append(a.Id).Append(" | ").Append(a.Label);
            if (a.Kind == ActionKind.PlayCard && PlanSafety.IsBoundary(a, snap)) sb.Append(" [unpredictable]");
            sb.Append('\n');
        }
        return sb.ToString().Replace("\r\n", "\n");
    }

    private static StateFormatter Formatter(PilotSettings s) =>
        new() { VerboseCards = s.Strategy.VerboseCards, IncludePiles = s.Strategy.IncludePiles };

    private static async Task Act(HttpListenerResponse res, JsonNode? body)
    {
        var id = body?["id"]?.ToString();
        var key = body?["semantic_key"]?.ToString();
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(key))
        {
            await Send(res, 400, Error("pass \"semantic_key\" (preferred) or \"id\"", "bad_request"));
            return;
        }
        // A build for another game version reads screens wrongly: acting on them is as unsafe as running the pilot.
        if (PilotRuntime.VersionProblemEnglish is { } problem)
        {
            await Send(res, 409, Error(problem, "version_mismatch"));
            return;
        }
        long? sequence = null;
        if (body?["sequence"] is JsonNode seqNode)
        {
            if (seqNode is JsonValue v && v.TryGetValue<long>(out var n)) sequence = n;
            else if (long.TryParse(seqNode.ToString(), out var parsed)) sequence = parsed;
            else
            {
                await Send(res, 400, Error("\"sequence\" must be the snapshot's sequence number", "bad_request"));
                return;
            }
        }

        var settings = PilotRuntime.Settings;
        bool pilotActive = settings.Control.Mode is not (PilotMode.Off or PilotMode.Advisor) && !PilotRuntime.Loop.Paused;
        if (pilotActive)
        {
            await Send(res, 409, Error("the built-in pilot is in control; set mode Off or Advisor first (POST /mode)", "pilot_active"));
            return;
        }
        var snap = await PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
        if (snap == null) { await Send(res, 503, Error("no game state yet", "no_state")); return; }
        if (!snap.CanAct)
        {
            await Send(res, 409, new { error = "cannot act now: " + snap.WaitReason, code = "busy", sequence = snap.Sequence });
            return;
        }
        // The same menu GET /state serves: what an owner rule removed cannot be run from outside either.
        var menu = OwnerRules.Filtered(snap, settings);
        // Semantic keys mean the same thing in every snapshot: the preferred way to name an action.
        if (string.IsNullOrWhiteSpace(key) && sequence is { } seen && !PilotRuntime.Bridge.MenuUnchangedSince(seen, snap))
        {
            await Send(res, 409, new
            {
                error = $"stale id: the menu changed since snapshot {seen}; choose again from this menu",
                code = "stale",
                sequence = snap.Sequence,
                legal = Menu(menu),
            });
            return;
        }
        var action = OwnerRules.Find(menu, snap, key, id, out bool removedByOwner);
        if (removedByOwner)
        {
            await Send(res, 403, new { error = "the owner's settings do not allow this action", code = "owner_rule", sequence = snap.Sequence, legal = Menu(menu) });
            return;
        }
        if (action == null)
        {
            await Send(res, 404, new { error = "no such legal action", code = "no_action", sequence = snap.Sequence, legal = Menu(menu) });
            return;
        }
        var result = await PilotRuntime.Bridge.ExecuteAsync(action, CancellationToken.None);
        await Send(res, result.Success ? 200 : 422, new { result.Success, result.Message, action = new { action.Id, action.Kind, action.Label } });
    }

    private static object Menu(GameSnapshot snap) => snap.Actions.Select(a => new { a.Id, a.Label, a.SemanticKey }).ToList();

    private static async Task Chat(HttpListenerResponse res, JsonNode? body)
    {
        var text = body?["text"]?.ToString();
        if (string.IsNullOrWhiteSpace(text)) { await Send(res, 400, Error("text required", "bad_request")); return; }
        bool fromAi = true; // compatibility: callers of the first API version were AI tools
        if (body?["from_ai"] is JsonNode aiNode)
        {
            if (aiNode is JsonValue v && v.TryGetValue<bool>(out var b)) fromAi = b;
            else { await Send(res, 400, Error("\"from_ai\" must be true or false", "bad_request")); return; }
        }
        var snap = PilotRuntime.Bridge.Latest ?? await PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
        var toText = body?["to"]?.ToString();
        ulong to = 0;
        if (!IsEveryone(toText))
        {
            to = snap == null ? 0 : PilotLoop.ResolveRecipient(toText, snap);
            if (to == 0)
            {
                var names = snap?.Teammates.Select(p => string.IsNullOrEmpty(p.Name) ? p.CharacterName : p.Name).ToList() ?? new List<string>();
                await Send(res, 400, new { error = $"unknown recipient '{toText}'; use \"all\" or one of the teammates", code = "unknown_recipient", teammates = names });
                return;
            }
        }
        var kind = DecisionParser.ParseKind(body?["kind"]?.ToString());
        // Who reads it comes from the live roster: a teammate who linked after the last capture can.
        if (snap is { IsMultiplayer: true } && !TeamLink.HasReader(to))
        {
            // Nobody addressed runs SpirePilot: they would never see the text (and on a client the host drops it).
            // The kind's vanilla emote is all they can see; with the AI's budget spent (or the same emote just shown)
            // there is none, and the answer stays the same.
            string? emote = null;
            if (Reactions.Has(kind) && (!fromAi || PilotRuntime.Team.TryUseAiBudget($"emote|{kind}|{to}")) &&
                PilotRuntime.Bridge.TrySignal(kind))
                emote = Reactions.Name(kind);
            await Send(res, 200, new JsonObject { ["delivered"] = false, ["emote"] = emote, ["reason"] = "no_reader" });
            return;
        }
        var target = body?["target"]?.ToString();
        var msg = new TeamMessage
        {
            Text = text,
            Kind = kind,
            To = to,
            Potion = body?["potion"]?.ToString(),
            Debuff = body?["debuff"]?.ToString(),
            // "e0" means this client's enemy; teammates resolve the shared combat id ("Cultist (cid:12)").
            Target = snap == null ? target : PilotLoop.NormalizeTarget(target, snap),
            ReplyTo = body?["reply_to"]?.ToString(),
        };
        var entry = PilotRuntime.Team.Send(msg, fromAi);
        if (entry == null) await Send(res, 429, Error("rate limited", "rate_limited"));
        else await Send(res, 200, new { delivered = entry.Delivered, id = entry.Message.Id });
    }

    /// <summary>Recipients that mean everyone (same words as <see cref="PilotLoop.ResolveRecipient"/>).</summary>
    private static bool IsEveryone(string? to)
    {
        if (string.IsNullOrWhiteSpace(to)) return true;
        var t = to.Trim();
        return t.Equals("all", StringComparison.OrdinalIgnoreCase) || t.Equals("everyone", StringComparison.OrdinalIgnoreCase) ||
               t is "所有人" or "全体";
    }

    /// <summary>
    /// All models of one kind with localized text; null for an unknown kind. Formatting every card takes a noticeable
    /// time, so it runs on the main thread in slices of a few milliseconds spread over frames instead of freezing the game.
    /// </summary>
    private static async Task<List<object>?> CompendiumAsync(string kind)
    {
        var items = await MainThread.Run(() => CompendiumItems(kind));
        if (items == null) return null;
        var list = new List<object>(items.Count);
        for (int next = 0; next < items.Count;)
        {
            int from = next;
            next = await MainThread.Run(() =>
            {
                var sw = Stopwatch.StartNew();
                int i = from;
                do
                {
                    try { list.Add(items[i]()); }
                    catch (Exception) { /* skip models that cannot render outside a run */ }
                    i++;
                } while (i < items.Count && sw.ElapsedMilliseconds < CompendiumSliceMs);
                return i;
            });
        }
        return list;
    }

    private const int CompendiumSliceMs = 4;

    /// <summary>One formatter per model of <paramref name="kind"/> (main thread).</summary>
    private static List<Func<object>>? CompendiumItems(string kind)
    {
        static List<Func<object>> Each<T>(IEnumerable<T> models, Func<T, object> map) =>
            models.Select(m => (Func<object>)(() => map(m))).ToList();
        return kind switch
        {
            "cards" => Each(ModelDb.AllCards, c => ModelText.Card(c, "", inCombat: false)),
            "relics" => Each(ModelDb.AllRelics, r => ModelText.Relic(r)),
            "potions" => Each(ModelDb.AllPotions, p => (object?)ModelText.Potion(p, -1, inCombat: false) ?? new { }),
            "powers" => Each(ModelDb.AllPowers, p => ModelText.Power(p)),
            "characters" => Each(ModelDb.AllCharacters, c => new { id = ModelText.SafeId(c), name = ModelText.Loc(c.Title) }),
            _ => null,
        };
    }

    private static object StatusObject()
    {
        var loop = PilotRuntime.Loop;
        var engine = PilotRuntime.Engine;
        return new
        {
            state = loop.Status.State,
            text = loop.Status.Text,
            mode = PilotRuntime.EffectiveMode,
            paused = loop.Paused,
            advice = loop.AdviceAction?.Label,
            llmCalls = engine.LlmCalls,
            inputTokens = engine.TotalInputTokens,
            outputTokens = engine.TotalOutputTokens,
            cachedTokens = engine.TotalCachedTokens,
            lastError = engine.LastLlmError,
            profile = PilotRuntime.Settings.Active?.Name,
            versionMismatch = PilotRuntime.VersionProblemEnglish,
            versionWarning = PilotRuntime.VersionWarningEnglish,
        };
    }

    // ---------------------------------------------------------------- plumbing

    private static object Error(string message, string code) => new { error = message, code };

    private static bool IsJson(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return false;
        var media = contentType.Split(';')[0].Trim();
        return media.Equals("application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Host header names a loopback address (any port).</summary>
    private static bool LoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true; // HTTP/1.0 without Host: the listener already matched loopback
        var h = host.Trim();
        if (h.StartsWith('['))
        {
            int end = h.IndexOf(']');
            h = end > 0 ? h[..(end + 1)] : h;
        }
        else
        {
            int colon = h.LastIndexOf(':');
            if (colon >= 0) h = h[..colon];
        }
        return h.Equals("localhost", StringComparison.OrdinalIgnoreCase) || h == "127.0.0.1" || h == "[::1]";
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes of UTF-8; null when the body is longer.</summary>
    private static async Task<string?> ReadBounded(Stream input, int limit)
    {
        var buffer = new byte[Math.Min(limit + 1, 16 * 1024)];
        using var collected = new MemoryStream();
        while (true)
        {
            int n = await input.ReadAsync(buffer.AsMemory(0, buffer.Length));
            if (n <= 0) break;
            if (collected.Length + n > limit) return null;
            collected.Write(buffer, 0, n);
        }
        return Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
    }

    private static async Task Send(HttpListenerResponse res, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, payload.GetType(), Json);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    /// <summary>Header values must be printable ASCII: other bytes of the UTF-8 text (and '%') are percent-encoded.</summary>
    private static string HeaderValue(string text)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            if (b is >= 0x20 and < 0x7F && b != (byte)'%') sb.Append((char)b);
            else sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    private static async Task SendText(HttpListenerResponse res, int status, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }
}
