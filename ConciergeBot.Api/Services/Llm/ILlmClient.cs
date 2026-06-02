namespace ConciergeBot.Api.Services.Llm;

/// Minimal LLM abstraction. ConciergeBot uses an LLM ONLY to improve the
/// wording of an already-determined deterministic route (see RouteNarrator) —
/// never to choose offerings, prices, or steps. Implementations MUST fail
/// closed: a non-OK <see cref="LlmCompletion"/> leaves the caller's
/// deterministic output unchanged.
///
/// This is the single isolation point for any future Microsoft Agent Framework
/// (MAF) spike: a MafLlmClient could implement this interface in a separate
/// optional project without touching the seller API. See
/// docs/conciergebot/compute-smoke.md.
public interface ILlmClient
{
    /// True only when the client can actually make a call (provider enabled and,
    /// for network providers, an API key present).
    bool IsEnabled { get; }

    /// Stable provider label surfaced in the smoke response:
    /// "disabled" | "openai-compatible" | "virtuals-compute".
    string ProviderLabel { get; }

    /// Configured model id (e.g. "moonshotai/kimi-k2-0905").
    string Model { get; }

    /// Run one completion. Returns a structured result; never throws for
    /// transport/parse failures — those become <see cref="LlmCompletion.Ok"/>=false
    /// with a stable opaque error code.
    Task<LlmCompletion> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}
