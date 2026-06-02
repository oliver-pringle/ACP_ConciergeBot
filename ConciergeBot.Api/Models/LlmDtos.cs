namespace ConciergeBot.Api.Models;

/// Request body for POST /v1/internal/llm-smoke. API-key protected (the route
/// is not under /health or /v1/resources/*, so the X-API-Key middleware gates it).
public record LlmSmokeRequest(string? Prompt);

/// Response for the LLM smoke probe. Carries NO secret — only the provider
/// label, model id, an ok flag, latency, a short text preview, and a stable
/// opaque error code on failure.
public record LlmSmokeResponse(
    string Provider,
    string Model,
    bool Ok,
    long LatencyMs,
    string? TextPreview,
    string? Error
);
