namespace SpirePilot.Core.Llm;

public enum LlmRole
{
    User,
    Assistant,
}

public sealed record LlmMessage(LlmRole Role, string Content);

/// <summary>A provider-neutral completion request.</summary>
public sealed class LlmRequest
{
    /// <summary>Stable system prompt (rules, output format, game knowledge). Cacheable; never changes.</summary>
    public string System { get; set; } = "";
    /// <summary>Second system part that changes rarely (owner settings, current character guide). Cached separately.</summary>
    public string SystemSuffix { get; set; } = "";
    public List<LlmMessage> Messages { get; set; } = new();
    /// <summary>JSON schema the answer must follow, when the provider supports structured output. Null = free text.</summary>
    public string? JsonSchema { get; set; }
    /// <summary>Overrides the profile's max tokens for this request.</summary>
    public int? MaxTokens { get; set; }
    /// <summary>Overrides the profile's reasoning effort for this request (e.g. "low" for routine combat turns).</summary>
    public string? Effort { get; set; }

    /// <summary>System prompt as one string, for providers without separate system blocks.</summary>
    public string FullSystem => string.IsNullOrEmpty(SystemSuffix) ? System : System + "\n\n" + SystemSuffix;
}

public sealed class LlmResponse
{
    public string Text { get; init; } = "";
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public int? CachedInputTokens { get; init; }
    public string? StopReason { get; init; }
    /// <summary>Model that actually produced the answer, when reported.</summary>
    public string? Model { get; init; }
    public TimeSpan Elapsed { get; init; }
    /// <summary>
    /// The reasoning effort the answered request was sent with (null = none). It differs from the configured one when
    /// the server rejected that value and the client fell back.
    /// </summary>
    public string? Effort { get; set; }
}

/// <summary>Raised for failures the caller may show to the user. <see cref="Retryable"/> tells the retry loop what to do.</summary>
public sealed class LlmException : Exception
{
    public int? StatusCode { get; }
    public bool Retryable { get; }
    public TimeSpan? RetryAfter { get; }

    private readonly bool _configError;
    /// <summary>The request ran out of time (the provider may still be generating — and billing — the answer).</summary>
    public bool IsTimeout { get; init; }

    public LlmException(string message, int? statusCode = null, bool retryable = false, TimeSpan? retryAfter = null, Exception? inner = null,
        bool configError = false)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Retryable = retryable;
        RetryAfter = retryAfter;
        _configError = configError;
    }

    /// <summary>
    /// The request can never succeed until the user fixes the settings (bad key, wrong URL, unknown model).
    /// The pilot stops instead of retrying or silently falling back.
    /// </summary>
    public bool IsConfigError =>
        _configError ||
        StatusCode is 401 or 402 or 403 or 404 ||
        StatusCode is 400 or 422 && ConfigWords.Any(w => Message.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 400 wordings that mean "fix your settings" (wrong model name, bad key, no credit) across providers:
    /// DeepSeek "Model Not Exist" / "Insufficient Balance", GLM "模型不存在" / "余额不足", OpenRouter "not a valid model ID",
    /// Gemini "API key not valid", Anthropic "credit balance is too low".
    /// </summary>
    private static readonly string[] ConfigWords =
    {
        "model not found", "model_not_found", "does not exist", "not exist", "invalid model", "unknown model",
        "not a valid model", "no such model", "api key", "api_key", "apikey", "credit balance", "insufficient balance",
        "insufficient_quota", "模型不存在", "余额不足", "无效的api", "api key无效",
    };
}

public interface ILlmClient
{
    /// <summary>Human-readable "provider / model" label for logs and UI.</summary>
    string Describe { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
