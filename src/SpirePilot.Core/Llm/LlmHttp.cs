using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SpirePilot.Core.Config;

namespace SpirePilot.Core.Llm;

/// <summary>Shared HTTP plumbing for the LLM clients: pooled clients, key resolution, retries, error mapping.</summary>
internal static class LlmHttp
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    /// <summary>One pooled HttpClient per proxy setting. Timeouts are applied per request via cancellation.</summary>
    public static HttpClient ClientFor(LlmProfile profile)
    {
        var key = profile.ProxyUrl?.Trim() ?? "";
        return Clients.GetOrAdd(key, static proxy =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.All,
            };
            if (!string.IsNullOrEmpty(proxy))
            {
                handler.Proxy = new WebProxy(proxy);
                handler.UseProxy = true;
            }
            return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        });
    }

    /// <summary>Resolves "env:NAME" keys from the environment; returns the key unchanged otherwise.</summary>
    public static string ResolveApiKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "";
        key = key.Trim();
        if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
            return Environment.GetEnvironmentVariable(key[4..].Trim())?.Trim() ?? "";
        return key;
    }

    public static string JoinUrl(string baseUrl, string path)
    {
        baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
        return baseUrl + "/" + path.TrimStart('/');
    }

    /// <summary>Merges the profile's ExtraBodyJson object into <paramref name="body"/> (top-level properties win).</summary>
    public static void MergeExtraBody(JsonObject body, string? extraBodyJson)
    {
        if (string.IsNullOrWhiteSpace(extraBodyJson)) return;
        JsonNode? extra;
        try { extra = JsonNode.Parse(extraBodyJson); }
        catch (JsonException ex) { throw new LlmException($"ExtraBodyJson is not valid JSON: {ex.Message}"); }
        if (extra is not JsonObject obj) throw new LlmException("ExtraBodyJson must be a JSON object.");
        foreach (var (name, value) in obj.ToList())
        {
            obj.Remove(name);
            if (value is null) body.Remove(name);
            else body[name] = value;
        }
    }

    public static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers == null) return;
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
    }

    public static StringContent JsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    /// <summary>Sends a request built by <paramref name="build"/>, returning the response body. Maps HTTP errors to <see cref="LlmException"/>.</summary>
    public static async Task<string> SendAsync(LlmProfile profile, Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, profile.TimeoutSeconds)));
        using var request = build();
        HttpResponseMessage response;
        try
        {
            response = await ClientFor(profile).SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"Request timed out after {profile.TimeoutSeconds}s.", retryable: true);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmException($"Network error: {ex.Message}", retryable: true, inner: ex);
        }

        using (response)
        {
            string text;
            try { text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new LlmException($"Reading the response timed out after {profile.TimeoutSeconds}s.", retryable: true);
            }

            if (response.IsSuccessStatusCode) return text;

            var status = (int)response.StatusCode;
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            if (retryAfter is { } ra && ra < TimeSpan.Zero) retryAfter = TimeSpan.Zero;
            bool retryable = status is 408 or 409 or 425 or 429 or 500 or 502 or 503 or 504 or 529;
            throw new LlmException($"HTTP {status}: {ExtractErrorMessage(text)}", status, retryable, retryAfter);
        }
    }

    /// <summary>Pulls a readable message out of an OpenAI- or Anthropic-style error body.</summary>
    public static string ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "(empty body)";
        try
        {
            var node = JsonNode.Parse(body);
            var err = node?["error"];
            var msg = err is JsonObject ? err["message"]?.GetValue<string>() : err?.GetValue<string>();
            msg ??= node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg)) return msg!;
        }
        catch (Exception) { /* not JSON */ }
        return body.Length > 500 ? body[..500] + "..." : body;
    }

    /// <summary>Runs <paramref name="attempt"/> with exponential backoff on retryable errors.</summary>
    public static async Task<T> WithRetries<T>(Func<Task<T>> attempt, int maxRetries, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1.5);
        for (int i = 0; ; i++)
        {
            try
            {
                return await attempt().ConfigureAwait(false);
            }
            catch (LlmException ex) when (ex.Retryable && i < maxRetries && !cancellationToken.IsCancellationRequested)
            {
                var wait = ex.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : delay;
                if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(20).Ticks));
            }
        }
    }
}
