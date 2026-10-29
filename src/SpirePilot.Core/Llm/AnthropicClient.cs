using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SpirePilot.Core.Config;

namespace SpirePilot.Core.Llm;

/// <summary>
/// Client for Anthropic's native Messages API (POST /v1/messages), written against raw HTTP because the
/// game loads each mod as a single assembly and cannot resolve extra SDK dependencies.
///
/// Request shape (API version 2026-10-21): no temperature/top_p/top_k; thinking left at the model default
/// (adaptive on current models); depth via output_config.effort; JSON via output_config.format json_schema;
/// the stable system prompt carries cache_control so repeated decisions reuse the cached prefix.
/// </summary>
public sealed class AnthropicClient : ILlmClient
{
    public const string DefaultVersion = "2026-10-21";
    private const string FallbackBeta = "server-side-fallback-2026-07-01";

    private readonly LlmProfile _profile;
    private bool _dropFallbacks;
    private bool _dropStructuredOutput;
    private bool _dropEffort;

    public AnthropicClient(LlmProfile profile)
    {
        _profile = profile.Clone();
        // Server-side fallbacks only exist on Anthropic's own endpoint; proxies and gateways usually reject it.
        _dropFallbacks = !_profile.AnthropicServerFallback ||
                         !_profile.BaseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    public string Describe => $"Anthropic / {_profile.Model}";

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
            catch (LlmException ex) when (ex.StatusCode == 400 && adapt < 3 && TryAdapt(ex.Message))
            {
                // retry once with the offending feature removed (older models / gateways)
            }
        }
        return Parse(body, sw.Elapsed);
    }

    private bool TryAdapt(string error)
    {
        var e = error.ToLowerInvariant();
        if (!_dropFallbacks && (e.Contains("fallback") || e.Contains("anthropic-beta")))
        {
            _dropFallbacks = true;
            return true;
        }
        if (!_dropStructuredOutput && (e.Contains("output_config.format") || e.Contains("json_schema") || e.Contains("structured")))
        {
            _dropStructuredOutput = true;
            return true;
        }
        if (!_dropEffort && e.Contains("effort"))
        {
            _dropEffort = true;
            return true;
        }
        return false;
    }

    private string MessagesUrl()
    {
        var baseUrl = _profile.BaseUrl.Trim().TrimEnd('/');
        return baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)
            ? baseUrl + "/messages"
            : baseUrl + "/v1/messages";
    }

    internal HttpRequestMessage BuildHttpRequest(LlmRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = _profile.Model,
            ["max_tokens"] = request.MaxTokens ?? _profile.MaxTokens,
        };

        if (!string.IsNullOrEmpty(request.System))
        {
            var block = new JsonObject { ["type"] = "text", ["text"] = request.System };
            if (_profile.PromptCaching) block["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            body["system"] = new JsonArray(block);
        }

        var messages = new JsonArray();
        foreach (var m in MergeConsecutive(request.Messages))
            messages.Add(new JsonObject { ["role"] = m.Role == LlmRole.User ? "user" : "assistant", ["content"] = m.Content });
        body["messages"] = messages;

        var outputConfig = new JsonObject();
        if (!string.IsNullOrWhiteSpace(_profile.ReasoningEffort) && !_dropEffort)
            outputConfig["effort"] = _profile.ReasoningEffort.Trim().ToLowerInvariant();
        if (_profile.JsonMode && request.JsonSchema != null && !_dropStructuredOutput)
            outputConfig["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = JsonNode.Parse(request.JsonSchema) };
        if (outputConfig.Count > 0) body["output_config"] = outputConfig;

        // Sampling parameters are rejected from API version 2026-10-21 on; only the legacy version accepts them.
        if (_profile.Temperature is { } t && _profile.AnthropicVersion == "2023-06-01") body["temperature"] = t;

        if (!_dropFallbacks) body["fallbacks"] = "default";
        LlmHttp.MergeExtraBody(body, _profile.ExtraBodyJson);

        var http = new HttpRequestMessage(HttpMethod.Post, MessagesUrl()) { Content = LlmHttp.JsonContent(body) };
        var key = LlmHttp.ResolveApiKey(_profile.ApiKey);
        if (key.Length > 0) http.Headers.TryAddWithoutValidation("x-api-key", key);
        http.Headers.TryAddWithoutValidation("anthropic-version",
            string.IsNullOrWhiteSpace(_profile.AnthropicVersion) ? DefaultVersion : _profile.AnthropicVersion);
        if (!_dropFallbacks) http.Headers.TryAddWithoutValidation("anthropic-beta", FallbackBeta);
        http.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        LlmHttp.ApplyHeaders(http, _profile.ExtraHeaders);
        return http;
    }

    /// <summary>The Messages API wants alternating roles; join accidental consecutive same-role messages.</summary>
    private static IEnumerable<LlmMessage> MergeConsecutive(IEnumerable<LlmMessage> messages)
    {
        LlmMessage? pending = null;
        foreach (var m in messages)
        {
            if (pending != null && pending.Role == m.Role)
            {
                pending = pending with { Content = pending.Content + "\n\n" + m.Content };
                continue;
            }
            if (pending != null) yield return pending;
            pending = m;
        }
        if (pending != null) yield return pending;
    }

    internal static LlmResponse Parse(string body, TimeSpan elapsed)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (Exception ex) { throw new LlmException("Server returned non-JSON: " + Truncate(body), inner: ex); }
        if (root?["type"]?.ToString() == "error")
            throw new LlmException("API error: " + LlmHttp.ExtractErrorMessage(body));

        string? stopReason = root?["stop_reason"]?.ToString();
        if (stopReason == "refusal")
        {
            var category = root?["stop_details"]?["category"]?.ToString();
            throw new LlmException("The model declined this request" + (category != null ? $" ({category})." : "."));
        }

        var sb = new StringBuilder();
        if (root?["content"] is JsonArray blocks)
        {
            foreach (var block in blocks)
            {
                // Only text blocks carry the answer; thinking / fallback / other block types are skipped.
                if (block?["type"]?.ToString() == "text") sb.Append(block["text"]?.ToString());
            }
        }
        var text = sb.ToString();
        if (string.IsNullOrWhiteSpace(text))
            throw new LlmException($"Empty answer (stop_reason={stopReason ?? "?"}). Try a larger max tokens value.",
                retryable: stopReason != "max_tokens");

        var usage = root?["usage"];
        return new LlmResponse
        {
            Text = text,
            InputTokens = (int?)usage?["input_tokens"],
            OutputTokens = (int?)usage?["output_tokens"],
            CachedInputTokens = (int?)usage?["cache_read_input_tokens"],
            StopReason = stopReason,
            Model = root?["model"]?.ToString(),
            Elapsed = elapsed,
        };
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
