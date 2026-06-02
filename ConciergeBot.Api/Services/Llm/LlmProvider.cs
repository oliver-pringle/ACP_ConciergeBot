namespace ConciergeBot.Api.Services.Llm;

/// LLM provider selection for ConciergeBot's optional narration / future
/// agentic paths. Default is <see cref="Disabled"/> — the deterministic
/// router is always the source of truth and the bot is fully functional
/// with no LLM configured.
///
///   * Disabled         — no network, no key required. /v1/internal/llm-smoke
///                         returns a safe disabled result.
///   * OpenAiCompatible  — any OpenAI /chat/completions-compatible endpoint
///                         (set Llm:Endpoint + a key).
///   * VirtualsCompute   — Virtuals Compute (https://compute.virtuals.io/v1),
///                         authenticated with VIRTUALS_API_KEY. Wire-compatible
///                         with the OpenAI Chat Completions shape, so it shares
///                         OpenAiCompatibleLlmClient.
public enum LlmProvider
{
    Disabled = 0,
    OpenAiCompatible = 1,
    VirtualsCompute = 2,
}
