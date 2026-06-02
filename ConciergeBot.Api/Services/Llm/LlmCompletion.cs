namespace ConciergeBot.Api.Services.Llm;

/// Result of an LLM completion attempt. <see cref="Ok"/> is the single source
/// of truth — callers MUST fall back to their deterministic output when it is
/// false. <see cref="Error"/> is a stable opaque code (never an upstream body
/// or exception message), safe to surface in the API-key-protected smoke
/// response. <see cref="Text"/> is null on failure.
public sealed record LlmCompletion(bool Ok, string? Text, long LatencyMs, string? Error)
{
    public static LlmCompletion Success(string text, long latencyMs) => new(true, text, latencyMs, null);
    public static LlmCompletion Failure(string error, long latencyMs = 0) => new(false, null, latencyMs, error);
}
