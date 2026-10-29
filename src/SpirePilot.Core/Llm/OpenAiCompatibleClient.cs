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
/// retried once without it and the choice is remembered for this client instance.
/// </summary>
public sealed class OpenAiCompatibleClient : ILlmClient
{
    private readonly LlmProfile _profile;
    private bool _useMaxCompletionTokens;
    private bool _dropTemperature;
    private bool _dropResponseFormat;
    private bool _dropReasoningEffort;
    private bool _triedTokenFieldSwap;
    private int? _maxTokensCap;

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
        for (int adapt = 0; ; adapt++)
        {
            try
            {
                body = await LlmHttp.SendAsync(_profile, () => BuildHttpRequest(request), cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (LlmException ex) when (ex.StatusCode is 400 or 422 && adapt < 6 && TryAdapt(ex.Message))
            {
                // retry with the adapted parameter set
            }
        }
        return Parse(body, sw.Elapsed);
    }

    /// <summary>Turns off the parameter a 400 error complains about. Returns false when nothing applies.</summary>
    private bool TryAdapt(string error)
    {
        var e = error.ToLowerInvariant();
        bool mentionsTokens = e.Contains("max_tokens") || e.Contains("max_completion_tokens");
        if (mentionsTokens && _maxTokensCap == null &&
            (e.Contains("too large") || e.Contains("exceed") || e.Contains("less than") || e.Contains("maximum") || e.Contains("range")))
        {
            _maxTokensCap = 2048;
            return true;
        }
        if (mentionsTokens && !_triedTokenFieldSwap)
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
        if (!_dropReasoningEffort && !string.IsNullOrEmpty(_profile.ReasoningEffort) && (e.Contains("reasoning_effort") || e.Contains("reasoning")))
        {
            _dropReasoningEffort = true;
            return true;
        }
        return false;
    }

    internal HttpRequestMessage BuildHttpRequest(LlmRequest request)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrEmpty(request.System))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var m in request.Messages)
            messages.Add(new JsonObject { ["role"] = m.Role == LlmRole.User ? "user" : "assistant", ["content"] = m.Content });

        var body = new JsonObject
        {
            ["model"] = _profile.Model,
            ["messages"] = messages,
            ["stream"] = false,
        };
        int maxTokens = request.MaxTokens ?? _profile.MaxTokens;
        if (_maxTokensCap is { } cap && maxTokens > cap) maxTokens = cap;
        body[_useMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens;
        if (_profile.Temperature is { } t && !_dropTemperature) body["temperature"] = t;
        if (_profile.JsonMode && request.JsonSchema != null && !_dropResponseFormat)
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (!string.IsNullOrWhiteSpace(_profile.ReasoningEffort) && !_dropReasoningEffort)
            body["reasoning_effort"] = _profile.ReasoningEffort.Trim();
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
        if (root?["error"] is JsonNode)
            throw new LlmException($"Server error: {LlmHttp.ExtractErrorMessage(body)}", retryable: false);

        var choice = root?["choices"]?[0];
        if (choice == null) throw new LlmException($"Response has no choices: {Truncate(body)}");
        var message = choice["message"];
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
            InputTokens = (int?)usage?["prompt_tokens"],
            OutputTokens = (int?)usage?["completion_tokens"],
            CachedInputTokens = (int?)usage?["prompt_tokens_details"]?["cached_tokens"],
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
