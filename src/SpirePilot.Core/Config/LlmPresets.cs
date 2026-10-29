namespace SpirePilot.Core.Config;

/// <summary>A ready-made provider template the UI offers when creating a profile.</summary>
public sealed record LlmPreset(string Id, string DisplayName, LlmProvider Provider, string BaseUrl, string Model, string ApiKeyHint);

/// <summary>
/// Known providers. Model names are only starting points — every field stays editable in the UI,
/// and any other OpenAI-compatible server works through "custom".
/// </summary>
public static class LlmPresets
{
    public static readonly IReadOnlyList<LlmPreset> All = new[]
    {
        new LlmPreset("anthropic", "Anthropic Claude", LlmProvider.Anthropic, "https://api.anthropic.com", "claude-opus-5-5", "sk-ant-..."),
        new LlmPreset("openai", "OpenAI", LlmProvider.OpenAiCompatible, "https://api.openai.com/v1", "gpt-5", "sk-..."),
        new LlmPreset("deepseek", "DeepSeek 深度求索", LlmProvider.OpenAiCompatible, "https://api.deepseek.com/v1", "deepseek-chat", "sk-..."),
        new LlmPreset("qwen", "通义千问 Qwen (DashScope)", LlmProvider.OpenAiCompatible, "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", "sk-..."),
        new LlmPreset("moonshot", "Moonshot Kimi", LlmProvider.OpenAiCompatible, "https://api.moonshot.cn/v1", "kimi-latest", "sk-..."),
        new LlmPreset("zhipu", "智谱 GLM", LlmProvider.OpenAiCompatible, "https://open.bigmodel.cn/api/paas/v4", "glm-4.6", "..."),
        new LlmPreset("siliconflow", "硅基流动 SiliconFlow", LlmProvider.OpenAiCompatible, "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", "sk-..."),
        new LlmPreset("openrouter", "OpenRouter", LlmProvider.OpenAiCompatible, "https://openrouter.ai/api/v1", "openrouter/auto", "sk-or-..."),
        new LlmPreset("gemini", "Google Gemini (OpenAI-compatible)", LlmProvider.OpenAiCompatible, "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", "AIza..."),
        new LlmPreset("ollama", "Ollama (local)", LlmProvider.OpenAiCompatible, "http://127.0.0.1:11434/v1", "qwen2.5:14b", "(none)"),
        new LlmPreset("lmstudio", "LM Studio (local)", LlmProvider.OpenAiCompatible, "http://127.0.0.1:1234/v1", "", "(none)"),
        new LlmPreset("custom", "Custom / 自定义", LlmProvider.OpenAiCompatible, "http://127.0.0.1:8000/v1", "", ""),
    };

    public static LlmPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static LlmProfile FromPreset(LlmPreset preset, string? name = null) => new()
    {
        Name = name ?? preset.Id,
        Provider = preset.Provider,
        BaseUrl = preset.BaseUrl,
        Model = preset.Model,
        // Local servers rarely support JSON mode reliably; the decision parser tolerates prose around JSON anyway.
        JsonMode = preset.Id is not ("ollama" or "lmstudio" or "custom"),
        // Claude: think more on route / reward / shop decisions, less on routine combat turns.
        ReasoningEffort = preset.Provider == LlmProvider.Anthropic ? "medium" : "",
        CombatReasoningEffort = preset.Provider == LlmProvider.Anthropic ? "low" : "",
        // Reasoning models count their thinking against this limit, so they get room for it.
        MaxTokens = preset.Id switch
        {
            "anthropic" => 16000,
            "openai" => 25000,
            "gemini" or "zhipu" or "openrouter" => 16000,
            _ => 8192,
        },
    };

    public static LlmProfile Default() => FromPreset(All[0], "default");
}
