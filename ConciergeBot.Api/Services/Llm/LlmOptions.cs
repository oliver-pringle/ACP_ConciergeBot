using Microsoft.Extensions.Configuration;

namespace ConciergeBot.Api.Services.Llm;

/// Bound from the "Llm" configuration section + the VIRTUALS_API_KEY env var.
/// All fields have safe defaults; an unconfigured bot resolves to
/// <see cref="LlmProvider.Disabled"/> with no key.
public sealed class LlmOptions
{
    public const string DefaultEndpoint = "https://compute.virtuals.io/v1";
    public const string DefaultModel = "moonshotai/kimi-k2-0905";

    public LlmProvider Provider { get; init; } = LlmProvider.Disabled;
    public string Endpoint { get; init; } = DefaultEndpoint;
    public string Model { get; init; } = DefaultModel;

    /// Resolved from Llm:ApiKey (config) first, else the VIRTUALS_API_KEY env
    /// var. Never logged. Null when no key is configured.
    public string? ApiKey { get; init; }

    public int TimeoutSeconds { get; init; } = 30;
    public int MaxOutputTokens { get; init; } = 512;

    public string ProviderLabel => Provider switch
    {
        LlmProvider.OpenAiCompatible => "openai-compatible",
        LlmProvider.VirtualsCompute => "virtuals-compute",
        _ => "disabled",
    };

    /// Build options from configuration. <paramref name="env"/> is an optional
    /// environment-variable source for testability; when null, the real process
    /// environment is read.
    public static LlmOptions FromConfiguration(
        IConfiguration config,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        string? Env(string key) =>
            env is not null
                ? (env.TryGetValue(key, out var v) ? Clean(v) : null)
                : Clean(Environment.GetEnvironmentVariable(key));

        return new LlmOptions
        {
            Provider = ParseProvider(config["Llm:Provider"]),
            Endpoint = Clean(config["Llm:Endpoint"]) ?? DefaultEndpoint,
            Model = Clean(config["Llm:Model"]) ?? DefaultModel,
            ApiKey = Clean(config["Llm:ApiKey"]) ?? Env("VIRTUALS_API_KEY"),
            TimeoutSeconds = config.GetValue("Llm:TimeoutSeconds", 30),
            MaxOutputTokens = config.GetValue("Llm:MaxOutputTokens", 512),
        };
    }

    /// Parse the provider string. Unknown values fail closed to Disabled rather
    /// than throwing — a typo in config must never silently enable a network
    /// path or crash the boot.
    public static LlmProvider ParseProvider(string? raw) =>
        raw?.Trim().ToLowerInvariant() switch
        {
            "openai-compatible" => LlmProvider.OpenAiCompatible,
            "virtuals-compute" => LlmProvider.VirtualsCompute,
            _ => LlmProvider.Disabled,
        };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
