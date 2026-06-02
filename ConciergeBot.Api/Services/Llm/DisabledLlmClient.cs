namespace ConciergeBot.Api.Services.Llm;

/// No-op LLM client used when Llm:Provider=disabled (the default). Makes no
/// network calls and requires no secret. Every completion returns a safe
/// "llm_disabled" failure so callers transparently fall back to deterministic
/// output.
public sealed class DisabledLlmClient : ILlmClient
{
    private readonly LlmOptions _options;
    public DisabledLlmClient(LlmOptions options) => _options = options;

    public bool IsEnabled => false;
    public string ProviderLabel => "disabled";
    public string Model => _options.Model;

    public Task<LlmCompletion> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
        => Task.FromResult(LlmCompletion.Failure("llm_disabled"));
}
