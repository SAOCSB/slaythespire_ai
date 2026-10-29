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
        bool local = uri != null && (uri.IsLoopback || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase));
        if (!local && string.IsNullOrEmpty(LlmHttp.ResolveApiKey(profile.ApiKey)))
            problems.Add("api_key");
        if (!string.IsNullOrWhiteSpace(profile.ProxyUrl) && !Uri.TryCreate(profile.ProxyUrl, UriKind.Absolute, out _))
            problems.Add("proxy");
        return problems;
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
