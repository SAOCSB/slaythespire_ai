using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SpirePilot.Core.Config;

namespace SpirePilot.Core.Llm;

/// <summary>
/// Client for the OpenAI chat-completions protocol, which most providers and local servers implement.
/// It adapts to common incompatibilities at runtime: when a server rejects a parameter
/// (max_tokens vs max_completion_tokens, temperature, response_format, reasoning_effort) the request is
/// retried once without it and the choice is remembered for this client instance. A "max_tokens too large"
/// error only lowers the limit for that one request (to the limit the server states when it says one).
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private readonly LlmProfile _profile;
    private bool _useMaxCompletionTokens;
    private bool _dropTemperature;
    private bool _dropResponseFormat;
    private bool _dropReasoningEffort;
    private bool _triedTokenFieldSwap;
    /// <summary>Reasoning effort of the last request built (null = none sent).</summary>
    private string? _lastEffort;

    public OpenAiCompatibleClient(LlmProfile profile)
    {
        _profile = profile.Clone();
        // OpenAI's reasoning models only accept max_completion_tokens.
        _useMaxCompletionTokens = _profile.BaseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase);
    }

    public string Describe => $"OpenAI-compatible / {_profile.Model}";

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        string body;
        int? cap = null; // per-request max_tokens limit learned from a "too large" error
        for (int adapt = 0; ; adapt++)
        {
            try
            {
                body = await LlmHttp.SendAsync(_profile, () => BuildHttpRequest(request, cap), cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (LlmException ex) when (ex.StatusCode is 400 or 422 && adapt < 6)
            {
                int requested = cap ?? request.MaxTokens ?? _profile.MaxTokens;
                if (TryLowerTokens(ex.Message, requested) is { } lower) cap = lower;
                else if (!TryAdapt(ex.Message)) throw;
            }
        }
        var response = Parse(body, sw.Elapsed);
        response.Effort = _lastEffort;
        return response;
    }

    /// <summary>
    /// For "max_tokens is too large" errors: the limit to retry this request with — the server's stated limit, the
    /// room left in the context window, or half the request. Null when the error is about something else.
    /// </summary>
    internal static int? TryLowerTokens(string error, int requested)
    {
        var e = error.ToLowerInvariant();
        bool mentionsTokens = e.Contains("max_tokens") || e.Contains("max_completion_tokens") || e.Contains("max_output_tokens");
        if (!mentionsTokens || IsUnsupportedWording(e)) return null;
        if (!(e.Contains("too large") || e.Contains("exceed") || e.Contains("less than") || e.Contains("at most") ||
              e.Contains("maximum") || e.Contains("range") || e.Contains("context length")))
            return null;

        int? lower = null;
        // vLLM / OpenAI: "maximum context length is 8192 tokens ... your request has 5000 input tokens"
        var ctx = System.Text.RegularExpressions.Regex.Match(e, @"context length is (\d+)");
        var input = System.Text.RegularExpressions.Regex.Match(e, @"(\d+) (?:input|prompt) tokens|(\d+) in the messages");
        if (ctx.Success && input.Success &&
            int.TryParse(ctx.Groups[1].Value, out var window) &&
            int.TryParse(input.Groups[1].Success ? input.Groups[1].Value : input.Groups[2].Value, out var used))
            lower = window - used - 64;
        else
        {
            // "valid range of max_tokens is [1, 65536]", "must be less than or equal to 8192"
            int best = 0;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(e, @"\d{3,7}"))
                if (int.TryParse(m.Value, out var n) && n >= 256 && n < requested && n > best) best = n;
            if (best > 0) lower = best;
        }
        lower ??= requested / 2;
        if (lower < 256 || lower >= requested) return null;
        return lower;
    }

    private static bool IsUnsupportedWording(string e) =>
        e.Contains("unsupported") || e.Contains("not supported") || e.Contains("unrecognized") || e.Contains("unknown") ||
        e.Contains("not allowed") || e.Contains("not permitted") || e.Contains("extra inputs") ||
        (e.Contains("instead") && e.Contains("max_tokens") && e.Contains("max_completion_tokens"));

    /// <summary>Turns off the parameter a 400 error complains about. Returns false when nothing applies.</summary>
    private bool TryAdapt(string error)
    {
        var e = error.ToLowerInvariant();
        bool mentionsTokens = e.Contains("max_tokens") || e.Contains("max_completion_tokens");
        // Rename only when the server says the field itself is unsupported, never for a "too large" error.
        if (mentionsTokens && !_triedTokenFieldSwap && IsUnsupportedWording(e))
        {
            _useMaxCompletionTokens = !_useMaxCompletionTokens;
            _triedTokenFieldSwap = true;
            return true;
        }
        if (!_dropTemperature && _profile.Temperature != null && e.Contains("temperature"))
        {
            _dropTemperature = true;
            return true;
        }
        if (!_dropResponseFormat && _profile.JsonMode && (e.Contains("response_format") || e.Contains("json_object") || e.Contains("json mode")))
        {
            _dropResponseFormat = true;
            return true;
        }
        if (!_dropReasoningEffort && (e.Contains("reasoning_effort") || e.Contains("reasoning")))
        {
            _dropReasoningEffort = true;
            return true;
        }
        return false;
    }

    internal HttpRequestMessage BuildHttpRequest(LlmRequest request, int? maxTokensCap = null)
    {
        var messages = new JsonArray();
        var system = request.FullSystem;
        if (!string.IsNullOrEmpty(system))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = system });
        foreach (var m in request.Messages)
            messages.Add(new JsonObject { ["role"] = m.Role == LlmRole.User ? "user" : "assistant", ["content"] = m.Content });

        var body = new JsonObject
        {
            ["model"] = _profile.Model,
            ["messages"] = messages,
            ["stream"] = false,
        };
        int maxTokens = request.MaxTokens ?? _profile.MaxTokens;
        if (maxTokensCap is { } cap && maxTokens > cap) maxTokens = cap;
        body[_useMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        if (_profile.Temperature is { } t && !_dropTemperature) body["temperature"] = t;
        if (_profile.JsonMode && request.JsonSchema != null && !_dropResponseFormat)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        var effort = string.IsNullOrWhiteSpace(request.Effort) ? _profile.ReasoningEffort : request.Effort;
        // Only send reasoning_effort when the user configured one (general or combat): many servers reject the field.
        _lastEffort = string.IsNullOrWhiteSpace(effort) || _dropReasoningEffort ? null : effort!.Trim();
        if (_lastEffort != null) body["reasoning_effort"] = _lastEffort;
        LlmHttp.MergeExtraBody(body, _profile.ExtraBodyJson);

        var http = new HttpRequestMessage(HttpMethod.Post, LlmHttp.JoinUrl(_profile.BaseUrl, "chat/completions"))
        {
            Content = LlmHttp.JsonContent(body),
        };
        var key = LlmHttp.ResolveApiKey(_profile.ApiKey);
        if (key.Length > 0) http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        LlmHttp.ApplyHeaders(http, _profile.ExtraHeaders);
        return http;
    }

    internal static LlmResponse Parse(string body, TimeSpan elapsed)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (Exception ex) { throw new LlmException($"Server returned non-JSON: {Truncate(body)}", inner: ex); }
        if (root is not JsonObject) throw new LlmException($"Unexpected response: {Truncate(body)}");
        if (root?["error"] is JsonNode)
            throw new LlmException($"Server error: {LlmHttp.ExtractErrorMessage(body)}", retryable: false);

        var choice = root?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] as JsonObject : null;
        if (choice == null) throw new LlmException($"Response has no choices: {Truncate(body)}");
        var message = choice["message"] as JsonObject;
        string text = ContentToText(message?["content"]);
        string? finish = choice["finish_reason"]?.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            var refusal = message?["refusal"]?.ToString();
            if (!string.IsNullOrWhiteSpace(refusal)) throw new LlmException($"Model refused: {refusal}");
            throw new LlmException($"Empty answer (finish_reason={finish ?? "?"}). Try a larger max tokens value.", retryable: finish != "length");
        }

        var usage = root?["usage"];
        return new LlmResponse
        {
            Text = text,
            InputTokens = LlmHttp.ReadInt(usage?["prompt_tokens"]),
            OutputTokens = LlmHttp.ReadInt(usage?["completion_tokens"]),
            CachedInputTokens = LlmHttp.ReadInt(usage?["prompt_tokens_details"]?["cached_tokens"]),
            StopReason = finish,
            Model = root?["model"]?.ToString(),
            Elapsed = elapsed,
        };
    }

    /// <summary>"content" is a string for most servers, or an array of parts for some.</summary>
    private static string ContentToText(JsonNode? content)
    {
        if (content == null) return "";
        if (content is JsonValue v) return v.ToString();
        if (content is JsonArray parts)
        {
            var sb = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonValue pv) sb.Append(pv.ToString());
                else if (part?["text"] is JsonNode tn) sb.Append(tn.ToString());
            }
            return sb.ToString();
        }
        return content.ToJsonString();
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
