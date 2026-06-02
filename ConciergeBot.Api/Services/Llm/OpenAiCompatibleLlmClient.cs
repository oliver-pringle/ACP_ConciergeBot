using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ConciergeBot.Api.Services.Llm;

/// Calls any OpenAI Chat-Completions-compatible endpoint. Used for both
/// Llm:Provider=openai-compatible and Llm:Provider=virtuals-compute (Virtuals
/// Compute is wire-compatible). Authenticates with a Bearer token resolved in
/// <see cref="LlmOptions"/>.
///
/// Security:
///   * The API key is sent only as the Authorization header and is NEVER logged
///     or echoed in any result.
///   * Upstream error bodies and exception messages are NEVER relayed — failures
///     surface as stable opaque codes (upstream_status_N / timeout / request_failed).
///   * Uses HttpCompletionOption.ResponseHeadersRead so a large/streamed body
///     isn't buffered wholesale before we start parsing.
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly HttpClient _http;
    private readonly LlmOptions _options;

    public OpenAiCompatibleLlmClient(HttpClient http, LlmOptions options)
    {
        _http = http;
        _options = options;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string ProviderLabel => _options.ProviderLabel;
    public string Model => _options.Model;

    public async Task<LlmCompletion> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (!IsEnabled)
            return LlmCompletion.Failure("missing_api_key");

        var url = _options.Endpoint.TrimEnd('/') + "/chat/completions";
        var requestBody = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
            max_tokens = _options.MaxOutputTokens,
            temperature = 0.2,
            stream = false,
        };

        var sw = Stopwatch.StartNew();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                sw.Stop();
                // Never relay the upstream body — surface only a stable status code.
                return LlmCompletion.Failure($"upstream_status_{(int)resp.StatusCode}", sw.ElapsedMilliseconds);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            sw.Stop();

            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrEmpty(text))
                    return LlmCompletion.Success(text!, sw.ElapsedMilliseconds);
            }

            return LlmCompletion.Failure("empty_response", sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            sw.Stop();
            return LlmCompletion.Failure("cancelled", sw.ElapsedMilliseconds);
        }
        catch (TaskCanceledException)
        {
            // HttpClient.Timeout elapsed.
            sw.Stop();
            return LlmCompletion.Failure("timeout", sw.ElapsedMilliseconds);
        }
        catch (Exception)
        {
            // Never surface ex.Message — it can embed the endpoint URL / resolution
            // detail. Stable opaque code only.
            sw.Stop();
            return LlmCompletion.Failure("request_failed", sw.ElapsedMilliseconds);
        }
    }
}
