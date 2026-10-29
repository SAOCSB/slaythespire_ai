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
        catch (JsonException ex) { throw new LlmException($"ExtraBodyJson is not valid JSON: {ex.Message}", configError: true); }
        if (extra is not JsonObject obj) throw new LlmException("ExtraBodyJson must be a JSON object.", configError: true);
        foreach (var (name, value) in obj.ToList())
        {
            obj.Remove(name);
            if (value is null) body.Remove(name);
            else body[name] = value;
        }
    }

    /// <summary>
    /// Applies the user's extra headers. A header the request already has is replaced, except "anthropic-beta",
    /// whose values are merged so built-in betas survive. Content headers (Content-Type) go to the content.
    /// Malformed names ("X-Key:") are a settings error.
    /// </summary>
    public static void ApplyHeaders(HttpRequestMessage request, IDictionary<string, string>? headers)
    {
        if (headers == null) return;
        foreach (var (rawName, rawValue) in headers)
        {
            var name = rawName?.Trim() ?? "";
            var value = rawValue ?? "";
            if (name.Length == 0) continue;
            if (!IsValidHeaderName(name)) throw new LlmException($"Invalid extra header name \"{rawName}\".", configError: true);
            try
            {
                if (IsContentHeader(name))
                {
                    if (request.Content == null) continue;
                    request.Content.Headers.Remove(name);
                    request.Content.Headers.TryAddWithoutValidation(name, value);
                    continue;
                }
                if (name.Equals("anthropic-beta", StringComparison.OrdinalIgnoreCase) && request.Headers.TryGetValues(name, out var existing))
                {
                    var merged = existing.SelectMany(v => v.Split(',')).Concat(value.Split(','))
                        .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
                    value = string.Join(",", merged);
                }
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                throw new LlmException($"Extra header \"{name}\" cannot be used: {ex.Message}", configError: true);
            }
        }
    }

    private static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Expires", StringComparison.OrdinalIgnoreCase) || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Allow", StringComparison.OrdinalIgnoreCase);

    /// <summary>RFC 7230 token characters.</summary>
    internal static bool IsValidHeaderName(string name) =>
        name.Length > 0 && name.All(c => c < 127 && (char.IsLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0));

    public static StringContent JsonContent(JsonNode body) =>
        new(body.ToJsonString(), Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

    /// <summary>Sends a request built by <paramref name="build"/>, returning the response body. Maps HTTP errors to <see cref="LlmException"/>.</summary>
    public static async Task<string> SendAsync(LlmProfile profile, Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, profile.TimeoutSeconds)));
        HttpRequestMessage built;
        try { built = build(); }
        catch (LlmException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new LlmException("Could not build the request (check the profile's extra headers / body): " + ex.Message, inner: ex, configError: true);
        }
        using var request = built;
        HttpResponseMessage response;
        try
        {
            response = await ClientFor(profile).SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LlmException($"Request timed out after {profile.TimeoutSeconds}s.", retryable: true) { IsTimeout = true };
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
                throw new LlmException($"Reading the response timed out after {profile.TimeoutSeconds}s.", retryable: true) { IsTimeout = true };
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

    /// <summary>Reads a token count that may be an int, a float like 12.0, or a numeric string; null otherwise.</summary>
    public static int? ReadInt(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d))
            return (int)Math.Clamp(d, int.MinValue, int.MaxValue);
        return null;
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
        int timeouts = 0;
        for (int i = 0; ; i++)
        {
            try
            {
                return await attempt().ConfigureAwait(false);
            }
            // A timeout is retried once at most: a model that needs longer than the limit will need it again, and
            // every resend may be billed in full.
            catch (LlmException ex) when (ex.Retryable && i < maxRetries && !cancellationToken.IsCancellationRequested &&
                                          (!ex.IsTimeout || ++timeouts <= 1))
            {
                var wait = ex.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : delay;
                if (wait > TimeSpan.FromSeconds(60)) wait = TimeSpan.FromSeconds(60);
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(20).Ticks));
            }
        }
    }
}
