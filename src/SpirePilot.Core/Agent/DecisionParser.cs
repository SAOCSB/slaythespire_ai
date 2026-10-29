using System.Text.Json;
using SpirePilot.Core.Model;
using SpirePilot.Core.Team;

namespace SpirePilot.Core.Agent;

/// <summary>
/// Parses the LLM's answer into an <see cref="AgentDecision"/>. Tolerant of what models actually produce:
/// code fences, prose around the JSON, numbers instead of "a3" ids, single "action" instead of "actions",
/// snake/camel case message kinds.
/// </summary>
public static class DecisionParser
{
    /// <summary>JSON schema sent to providers that support structured output. Kept constant so it caches.</summary>
    public const string Schema = """
    {
      "type": "object",
      "properties": {
        "thought": { "type": "string" },
        "actions": { "type": "array", "items": { "type": "string" } },
        "messages": {
          "type": "array",
          "items": {
            "type": "object",
            "properties": {
              "to": { "type": "string" },
              "type": { "type": "string", "enum": ["chat", "request_potion", "offer_potion", "ask_need_potion", "request_debuff", "request_focus", "request_block", "suggest_route", "accept", "decline"] },
              "text": { "type": "string" },
              "potion": { "type": "string" },
              "debuff": { "type": "string" },
              "target": { "type": "string" },
              "reply_to": { "type": "string" }
            },
            "required": ["to", "type", "text"],
            "additionalProperties": false
          }
        },
        "memo": { "type": "string" }
      },
      "required": ["thought", "actions"],
      "additionalProperties": false
    }
    """;

    public sealed class ParseResult
    {
        public AgentDecision? Decision { get; init; }
        public string? Error { get; init; }
        public bool Ok => Decision != null;
    }

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static ParseResult Parse(string text, GameSnapshot snapshot)
    {
        var json = ExtractJsonObject(StripReasoning(text));
        if (json == null) return new ParseResult { Error = "no JSON object found in the answer" };

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, DocOptions); }
        catch (JsonException ex) { return new ParseResult { Error = "invalid JSON: " + ex.Message }; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new ParseResult { Error = "answer is not a JSON object" };

            var decision = new AgentDecision
            {
                Source = DecisionSource.Llm,
                Thought = GetString(root, "thought", "reasoning", "reason") ?? "",
                Memo = NullIfBlank(GetString(root, "memo", "note")),
            };

            // actions: array of ids / numbers, or a single "action".
            var raw = new List<string>();
            if (TryGet(root, out var actions, "actions", "plan"))
            {
                if (actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in actions.EnumerateArray()) AddRawId(raw, a);
                }
                else AddRawId(raw, actions);
            }
            if (raw.Count == 0 && TryGet(root, out var single, "action", "action_id", "id")) AddRawId(raw, single);
            if (raw.Count == 0) return new ParseResult { Error = "no action chosen (\"actions\" is empty)" };

            // The first id must be legal. A plan is only trusted up to its first unknown id (order matters),
            // and it ends after any step whose outcome cannot be foreseen (draws, random effects, end turn ...).
            foreach (var r in raw)
            {
                var id = NormalizeId(r, snapshot);
                if (id == null) break;
                decision.ActionIds.Add(id);
                if (snapshot.FindAction(id) is { } chosen && PlanSafety.IsBoundary(chosen, snapshot)) break;
            }
            if (decision.ActionIds.Count == 0)
                return new ParseResult { Error = $"unknown action id \"{raw[0]}\"; choose ids from the legal action list" };

            // Remaining plan steps are remembered by semantic key so they survive index shifts.
            foreach (var id in decision.ActionIds.Skip(1))
            {
                var key = snapshot.FindAction(id)?.SemanticKey;
                if (!string.IsNullOrEmpty(key)) decision.PlanKeys.Add(key);
            }

            if (TryGet(root, out var messages, "messages", "say", "team_messages") && messages.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in messages.EnumerateArray())
                {
                    if (m.ValueKind != JsonValueKind.Object) continue;
                    var textField = GetString(m, "text", "message", "content");
                    if (string.IsNullOrWhiteSpace(textField)) continue;
                    decision.Messages.Add(new TeamMessageIntent
                    {
                        To = NullIfBlank(GetString(m, "to", "recipient")) ?? "all",
                        Kind = ParseKind(GetString(m, "type", "kind")),
                        Text = textField.Trim(),
                        Potion = NullIfBlank(GetString(m, "potion")),
                        Debuff = NullIfBlank(GetString(m, "debuff", "power")),
                        Target = NullIfBlank(GetString(m, "target")),
                        ReplyTo = NullIfBlank(GetString(m, "reply_to", "replyTo", "in_reply_to")),
                    });
                }
            }
            return new ParseResult { Decision = decision };
        }
    }

    /// <summary>Removes &lt;think&gt; / &lt;thinking&gt; blocks that local reasoning models put into the answer text.</summary>
    public static string StripReasoning(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var tag in new[] { "think", "thinking", "reasoning" })
        {
            while (true)
            {
                int start = text.IndexOf("<" + tag + ">", StringComparison.OrdinalIgnoreCase);
                if (start < 0) break;
                int end = text.IndexOf("</" + tag + ">", start, StringComparison.OrdinalIgnoreCase);
                text = end < 0 ? text[..start] : text[..start] + text[(end + tag.Length + 3)..];
            }
            // Chat templates that put the opening tag into the prompt leave only "reasoning...</think>answer".
            int orphan = text.LastIndexOf("</" + tag + ">", StringComparison.OrdinalIgnoreCase);
            if (orphan >= 0) text = text[(orphan + tag.Length + 3)..];
        }
        return text;
    }

    /// <summary>Maps "a3", "A3", "3", "a3: Play Strike" to a menu id that exists, or null.</summary>
    public static string? NormalizeId(string raw, GameSnapshot snapshot)
    {
        var s = raw.Trim().Trim('"', '\'', '`', '[', ']', '(', ')');
        int cut = s.IndexOfAny(new[] { ':', ' ', ',' });
        if (cut > 0) s = s[..cut];
        if (snapshot.FindAction(s) is { } direct) return direct.Id;
        if (int.TryParse(s, out var n) && snapshot.FindAction("a" + n) is { } numbered) return numbered.Id;
        return null;
    }

    public static TeamMessageKind ParseKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return TeamMessageKind.Chat;
        var k = raw.Replace("_", "").Replace("-", "").Trim().ToLowerInvariant();
        return k switch
        {
            "requestpotion" or "askpotion" => TeamMessageKind.RequestPotion,
            "offerpotion" or "givepotion" => TeamMessageKind.OfferPotion,
            "askneedpotion" or "needpotion" => TeamMessageKind.AskNeedPotion,
            "requestdebuff" or "askdebuff" => TeamMessageKind.RequestDebuff,
            "requestfocus" or "focus" => TeamMessageKind.RequestFocus,
            "requestblock" or "block" => TeamMessageKind.RequestBlock,
            "suggestroute" or "route" => TeamMessageKind.SuggestRoute,
            "accept" or "yes" or "ok" => TeamMessageKind.Accept,
            "decline" or "no" or "refuse" => TeamMessageKind.Decline,
            _ => TeamMessageKind.Chat,
        };
    }

    public static string KindToWire(TeamMessageKind kind) => kind switch
    {
        TeamMessageKind.RequestPotion => "request_potion",
        TeamMessageKind.OfferPotion => "offer_potion",
        TeamMessageKind.AskNeedPotion => "ask_need_potion",
        TeamMessageKind.RequestDebuff => "request_debuff",
        TeamMessageKind.RequestFocus => "request_focus",
        TeamMessageKind.RequestBlock => "request_block",
        TeamMessageKind.SuggestRoute => "suggest_route",
        TeamMessageKind.Accept => "accept",
        TeamMessageKind.Decline => "decline",
        TeamMessageKind.Hello => "hello",
        _ => "chat",
    };

    /// <summary>Finds the first balanced top-level JSON object in free text (ignores braces inside strings).</summary>
    public static string? ExtractJsonObject(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        for (int start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
        {
            int depth = 0;
            bool inString = false, escape = false;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    var candidate = text.Substring(start, i - start + 1);
                    if (LooksLikeDecision(candidate)) return candidate;
                    break;
                }
            }
        }
        return null;
    }

    private static bool LooksLikeDecision(string candidate)
    {
        try
        {
            using var d = JsonDocument.Parse(candidate, DocOptions);
            return d.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
    }

    private static void AddRawId(List<string> raw, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: raw.Add(e.GetString() ?? ""); break;
            case JsonValueKind.Number: raw.Add(e.GetRawText()); break;
            case JsonValueKind.Object when TryGet(e, out var id, "id", "action"): AddRawId(raw, id); break;
        }
    }

    private static bool TryGet(JsonElement obj, out JsonElement value, params string[] names)
    {
        foreach (var p in obj.EnumerateObject())
        {
            foreach (var n in names)
            {
                if (string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement obj, params string[] names)
    {
        if (!TryGet(obj, out var v, names)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => v.GetRawText(),
        };
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
