using SpirePilot.Core.Config;

namespace SpirePilot.Core.Llm;

public static class LlmClientFactory
{
    public static ILlmClient Create(LlmProfile profile) => profile.Provider switch
    {
        LlmProvider.Anthropic => new AnthropicClient(profile),
        _ => new OpenAiCompatibleClient(profile),
    };

    /// <summary>Returns a list of problems that make the profile unusable (empty when it looks fine).</summary>
    public static List<string> Validate(LlmProfile profile)
    {
        var problems = new List<string>();
        if (!Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            problems.Add("base_url");
        if (string.IsNullOrWhiteSpace(profile.Model) && profile.Provider == LlmProvider.Anthropic)
            problems.Add("model");
        bool local = uri != null && IsLocalHost(uri);
        if (!local && string.IsNullOrEmpty(LlmHttp.ResolveApiKey(profile.ApiKey)))
            problems.Add("api_key");
        if (!string.IsNullOrWhiteSpace(profile.ProxyUrl) && !Uri.TryCreate(profile.ProxyUrl, UriKind.Absolute, out _))
            problems.Add("proxy");
        if (!string.IsNullOrWhiteSpace(profile.ExtraBodyJson))
        {
            try { if (System.Text.Json.Nodes.JsonNode.Parse(profile.ExtraBodyJson) is not System.Text.Json.Nodes.JsonObject) problems.Add("extra_body"); }
            catch (System.Text.Json.JsonException) { problems.Add("extra_body"); }
        }
        if (profile.ExtraHeaders != null && profile.ExtraHeaders.Keys.Any(k => !string.IsNullOrWhiteSpace(k) && !LlmHttp.IsValidHeaderName(k.Trim())))
            problems.Add("extra_headers");
        return problems;
    }

    /// <summary>Loopback, *.local, *.lan and private / link-local addresses: servers there often need no key.</summary>
    internal static bool IsLocalHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        var host = uri.Host;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) || !host.Contains('.'))
            return true;
        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) ||
               (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // CGNAT / Tailscale
    }

    /// <summary>Sends a tiny request to check that the endpoint, key and model work.</summary>
    public static async Task<LlmResponse> TestAsync(LlmProfile profile, CancellationToken cancellationToken)
    {
        var client = Create(profile);
        var request = new LlmRequest
        {
            System = "You are a connectivity check. Reply with a JSON object only.",
            Messages = { new LlmMessage(LlmRole.User, "Reply exactly with {\"ok\": true}") },
            JsonSchema = """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"],"additionalProperties":false}""",
            MaxTokens = Math.Min(profile.MaxTokens, 2048),
        };
        return await client.CompleteAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
