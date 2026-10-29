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
    /// <summary>Stable system prompt (rules, output format, standing instructions). Cacheable.</summary>
    public string System { get; set; } = "";
    public List<LlmMessage> Messages { get; set; } = new();
    /// <summary>JSON schema the answer must follow, when the provider supports structured output. Null = free text.</summary>
    public string? JsonSchema { get; set; }
    /// <summary>Overrides the profile's max tokens for this request.</summary>
    public int? MaxTokens { get; set; }
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
}

/// <summary>Raised for failures the caller may show to the user. <see cref="Retryable"/> tells the retry loop what to do.</summary>
public sealed class LlmException : Exception
{
    public int? StatusCode { get; }
    public bool Retryable { get; }
    public TimeSpan? RetryAfter { get; }

    public LlmException(string message, int? statusCode = null, bool retryable = false, TimeSpan? retryAfter = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Retryable = retryable;
        RetryAfter = retryAfter;
    }
}

public interface ILlmClient
{
    /// <summary>Human-readable "provider / model" label for logs and UI.</summary>
    string Describe { get; }

    Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken);
}
