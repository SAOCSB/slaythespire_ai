using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SpirePilot.Core.Agent;
using SpirePilot.Core.Config;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;
using SpirePilot.Infra;

namespace SpirePilot.Api;

/// <summary>
/// Optional local HTTP API (127.0.0.1 only, off by default) so external AI tools — scripts, MCP servers, other agents —
/// can read the full game state and act through the same legal-action menu the built-in pilot uses.
///
///   GET  /status              pilot status, mode, token usage, last error
///   GET  /state               fresh snapshot (state + legal actions)
///   GET  /prompt              the prompt the built-in pilot would send for the current state
///   POST /act    {"id":"a3"} or {"semantic_key":"..."}   execute a legal action (pilot must be Off / Advisor / paused)
///   POST /mode   {"mode":"FullAuto"|"CombatOnly"|"OutOfCombatOnly"|"Advisor"|"Off"}
///   POST /instruction {"text":"..."}                     one-shot instruction for the built-in pilot
///   GET  /chat                recent team chat;  POST /chat {"text":"...","to":"all","kind":"chat"}
///   GET  /compendium?kind=cards|relics|potions|powers|characters   every game model with localized text
///   GET  /diagnostics         diagnostics report (markdown; no secrets)
/// With a token configured, every request needs "Authorization: Bearer &lt;token&gt;".
/// </summary>
internal sealed class LocalHttpApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpListener _listener = new();
    private Thread? _thread;
    private volatile bool _running;

    public int Port { get; }
    public string Token { get; set; }

    public LocalHttpApi(int port, string token)
    {
        Port = port;
        Token = token ?? "";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "SpirePilot HTTP API" };
        _thread.Start();
        ModLog.Info($"HTTP API listening on http://127.0.0.1:{Port}/");
    }

    /// <summary>Requests are served on worker threads; nothing to do per frame.</summary>
    public void Pump() { }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch (Exception) { if (!_running) return; continue; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var res = ctx.Response;
        try
        {
            if (!string.IsNullOrEmpty(Token) && ctx.Request.Headers["Authorization"] != "Bearer " + Token)
            {
                await Send(res, 401, new { error = "missing or wrong bearer token" });
                return;
            }
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            var method = ctx.Request.HttpMethod.ToUpperInvariant();
            JsonNode? body = null;
            if (method == "POST")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                var text = await reader.ReadToEndAsync();
                if (text.Length > 64 * 1024) { await Send(res, 413, new { error = "body too large" }); return; }
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
                {
                    var snap = await PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
                    await Send(res, snap == null ? 503 : 200, (object?)snap ?? new { error = "no game state yet" });
                    break;
                }
                case ("GET", "/prompt"):
                {
                    var snap = await PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
                    if (snap == null) { await Send(res, 503, new { error = "no game state yet" }); break; }
                    var s = PilotRuntime.Settings;
                    var ctxObj = new DecisionContext { History = PilotRuntime.Engine.Memory.History, Memo = PilotRuntime.Engine.Memory.Memo };
                    var formatter = new StateFormatter { VerboseCards = s.Strategy.VerboseCards, IncludePiles = s.Strategy.IncludePiles };
                    await Send(res, 200, new
                    {
                        system = PromptBuilder.CoreSystem,
                        owner = PromptBuilder.BuildOwnerBlock(s, PilotRuntime.ResolveMessageLanguage(s), CharacterGuide.For(snap.LocalPlayer?.CharacterId)),
                        user = PromptBuilder.BuildUser(snap, ctxObj, formatter),
                        schema = JsonNode.Parse(DecisionParser.Schema),
                    });
                    break;
                }
                case ("GET", "/compendium"):
                {
                    var kind = (ctx.Request.QueryString["kind"] ?? "cards").ToLowerInvariant();
                    var list = await MainThread.Run(() => Compendium(kind));
                    await Send(res, list == null ? 400 : 200, (object?)list ?? new { error = "kind must be cards, relics, potions, powers or characters" });
                    break;
                }
                case ("GET", "/diagnostics"):
                {
                    var report = await MainThread.Run(Diagnostics.Build);
                    var bytes = Encoding.UTF8.GetBytes(report);
                    res.StatusCode = 200;
                    res.ContentType = "text/markdown; charset=utf-8";
                    res.ContentLength64 = bytes.Length;
                    await res.OutputStream.WriteAsync(bytes);
                    res.Close();
                    break;
                }
                case ("POST", "/act"):
                    await Act(res, body);
                    break;
                case ("POST", "/mode"):
                {
                    var mode = body?["mode"]?.ToString();
                    if (!Enum.TryParse<PilotMode>(mode, true, out var m)) { await Send(res, 400, new { error = "unknown mode" }); break; }
                    PilotRuntime.SetMode(m);
                    await Send(res, 200, StatusObject());
                    break;
                }
                case ("POST", "/instruction"):
                    PilotRuntime.Loop.SetLiveInstruction(body?["text"]?.ToString());
                    await Send(res, 200, new { ok = true });
                    break;
                case ("GET", "/chat"):
                    await Send(res, 200, PilotRuntime.Team.Recent(50).Select(e => new
                    {
                        time = e.LocalTime, incoming = e.Incoming, delivered = e.Delivered, from = e.Message.FromName,
                        fromAi = e.Message.FromAi, kind = e.Message.Kind, text = e.Message.Text, to = e.Message.To,
                    }));
                    break;
                case ("POST", "/chat"):
                {
                    var text = body?["text"]?.ToString();
                    if (string.IsNullOrWhiteSpace(text)) { await Send(res, 400, new { error = "text required" }); break; }
                    var snap = PilotRuntime.Bridge.Latest;
                    var msg = new TeamMessage
                    {
                        Text = text,
                        Kind = DecisionParser.ParseKind(body?["kind"]?.ToString()),
                        To = snap == null ? 0 : PilotLoop.ResolveRecipient(body?["to"]?.ToString(), snap),
                        Potion = body?["potion"]?.ToString(),
                        Debuff = body?["debuff"]?.ToString(),
                        Target = body?["target"]?.ToString(),
                        ReplyTo = body?["reply_to"]?.ToString(),
                    };
                    var entry = PilotRuntime.Team.Send(msg, fromAi: true);
                    await Send(res, entry == null ? 429 : 200, entry == null ? new { error = "rate limited" } : new { delivered = entry.Delivered });
                    break;
                }
                default:
                    await Send(res, 404, new { error = "unknown endpoint" });
                    break;
            }
        }
        catch (JsonException ex)
        {
            await Send(res, 400, new { error = "invalid JSON: " + ex.Message });
        }
        catch (Exception ex)
        {
            ModLog.Warn("HTTP API request failed: " + ex.Message);
            try { await Send(res, 500, new { error = ex.Message }); } catch (Exception) { }
        }
    }

    private static async Task Act(HttpListenerResponse res, JsonNode? body)
    {
        var settings = PilotRuntime.Settings;
        bool pilotActive = settings.Control.Mode is not (PilotMode.Off or PilotMode.Advisor) && !PilotRuntime.Loop.Paused;
        if (pilotActive)
        {
            await Send(res, 409, new { error = "the built-in pilot is in control; set mode Off or Advisor first (POST /mode)" });
            return;
        }
        var snap = await PilotRuntime.Bridge.CaptureAsync(CancellationToken.None);
        if (snap == null) { await Send(res, 503, new { error = "no game state yet" }); return; }
        if (!snap.CanAct) { await Send(res, 409, new { error = "cannot act now: " + snap.WaitReason }); return; }
        var id = body?["id"]?.ToString();
        var key = body?["semantic_key"]?.ToString();
        var action = id != null ? snap.FindAction(id) : key != null ? snap.Actions.FirstOrDefault(a => a.SemanticKey == key) : null;
        if (action == null) { await Send(res, 404, new { error = "no such legal action", legal = snap.Actions.Select(a => new { a.Id, a.Label }) }); return; }
        var result = await PilotRuntime.Bridge.ExecuteAsync(action, CancellationToken.None);
        await Send(res, result.Success ? 200 : 422, new { result.Success, result.Message, action = new { action.Id, action.Kind, action.Label } });
    }

    /// <summary>All models of one kind with localized text (main thread).</summary>
    private static object? Compendium(string kind)
    {
        object Safe<T>(IEnumerable<T> models, Func<T, object> map) where T : class
        {
            var list = new List<object>();
            foreach (var m in models)
            {
                try { list.Add(map(m)); }
                catch (Exception) { /* skip models that cannot render outside a run */ }
            }
            return list;
        }
        return kind switch
        {
            "cards" => Safe(MegaCrit.Sts2.Core.Models.ModelDb.AllCards, c => Game.ModelText.Card(c, "", inCombat: false)),
            "relics" => Safe(MegaCrit.Sts2.Core.Models.ModelDb.AllRelics, r => Game.ModelText.Relic(r)),
            "potions" => Safe(MegaCrit.Sts2.Core.Models.ModelDb.AllPotions, p => (object?)Game.ModelText.Potion(p, -1, inCombat: false) ?? new { }),
            "powers" => Safe(MegaCrit.Sts2.Core.Models.ModelDb.AllPowers, p => Game.ModelText.Power(p)),
            "characters" => Safe(MegaCrit.Sts2.Core.Models.ModelDb.AllCharacters, c => new { id = Game.ModelText.SafeId(c), name = Game.ModelText.Loc(c.Title) }),
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
            mode = PilotRuntime.Settings.Control.Mode,
            paused = loop.Paused,
            advice = loop.AdviceAction?.Label,
            llmCalls = engine.LlmCalls,
            inputTokens = engine.TotalInputTokens,
            outputTokens = engine.TotalOutputTokens,
            cachedTokens = engine.TotalCachedTokens,
            lastError = engine.LastLlmError,
            profile = PilotRuntime.Settings.Active?.Name,
        };
    }

    private static async Task Send(HttpListenerResponse res, int status, object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        res.StatusCode = status;
        res.ContentType = "application/json; charset=utf-8";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }
}
