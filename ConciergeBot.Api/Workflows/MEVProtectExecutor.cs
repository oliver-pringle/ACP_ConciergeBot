using System.Text.Json;
using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Workflows;

public sealed class MEVProtectExecutor : IWorkflowExecutor<WorkflowContext, MEVProtectResult>
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<MEVProtectExecutor> _log;
    private readonly bool _enabled;

    public string Name => "MEVProtect";

    public MEVProtectExecutor(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<MEVProtectExecutor> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["PortfolioRun:MEVProtectApiKey"]
            ?? Environment.GetEnvironmentVariable("MEVPROTECT_INTERNAL_KEY")
            ?? "";
        _log = log;

        var baseUrl = config["PortfolioRun:MEVProtectBaseUrl"] ?? "";
        _enabled = !string.IsNullOrEmpty(baseUrl);

        if (_enabled && string.IsNullOrEmpty(_apiKey))
        {
            if (!env.IsDevelopment())
            {
                _log.LogWarning(
                    "[portfolio_run] MEVPROTECT_INTERNAL_KEY not set but MEVProtectBaseUrl is configured. " +
                    "MEVProtect executor will return unavailable for all calls.");
            }
        }
    }

    public async Task<MEVProtectResult> ExecuteAsync(WorkflowContext input, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(_apiKey))
        {
            return new MEVProtectResult("unknown", ["MEVProtect unavailable (not configured)"], null);
        }

        try
        {
            var http = _httpFactory.CreateClient("mevprotect");
            var url = $"v1/internal/mev_score?wallet={Uri.EscapeDataString(input.WalletAddress)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-API-Key", _apiKey);
            req.Headers.Add("X-Caller", "conciergebot");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[portfolio_run] MEVProtect mev_score returned {Status}", resp.StatusCode);
                return new MEVProtectResult("unknown", [$"MEVProtect returned {(int)resp.StatusCode}"], null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var (riskLevel, findings) = ExtractRiskAndFindings(body);

            return new MEVProtectResult(riskLevel, findings, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[portfolio_run] MEVProtect mev_score timed out");
            return new MEVProtectResult("unknown", ["MEVProtect unavailable (timeout)"], null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[portfolio_run] MEVProtect mev_score failed");
            return new MEVProtectResult("unknown", ["MEVProtect unavailable"], null);
        }
    }

    private static (string riskLevel, List<string> findings) ExtractRiskAndFindings(JsonElement body)
    {
        // MEVProtect's /v1/internal/mev_score is non-blocking: a wallet it hasn't
        // analysed yet returns pending=true with a neutral placeholder score while
        // it warms its 12h forensics cache in the background. Surface that honestly
        // as "unknown / warming" rather than reporting the placeholder as a real
        // medium-exposure verdict. A follow-up portfolio_run returns the real score.
        if (body.TryGetProperty("pending", out var pend) && pend.ValueKind == JsonValueKind.True)
        {
            return ("unknown",
                ["MEV exposure analysis is warming up — re-run shortly for the full result."]);
        }

        var findings = new List<string>();
        var riskLevel = "low";

        var mevScore = 100;
        var sandwichEvents = 0;
        var frontruns = 0;

        if (body.TryGetProperty("mevScore", out var ms) && ms.ValueKind == JsonValueKind.Number)
        {
            mevScore = ms.GetInt32();
        }

        if (body.TryGetProperty("sandwichEvents", out var se) && se.ValueKind == JsonValueKind.Number)
        {
            sandwichEvents = se.GetInt32();
        }

        // MEVProtect emits "frontruns" (not "frontrunEvents") — match the wire field.
        if (body.TryGetProperty("frontruns", out var fe) && fe.ValueKind == JsonValueKind.Number)
        {
            frontruns = fe.GetInt32();
        }

        // Band on OBSERVED event counts, not the raw score. MEVProtect's mevScore
        // is 100 (no sandwiches) down to 75 (>=25% sandwich rate), and 75 is ALSO
        // the neutral "no data / 0-tx" sentinel — so the score alone can't tell a
        // clean/unanalysed wallet (0 events) from a heavily-sandwiched one. Event
        // counts are unambiguous: 0 events = no observed exposure.
        if (sandwichEvents == 0 && frontruns == 0)
        {
            riskLevel = "low";
            findings.Add("No MEV sandwich or frontrun activity observed (30-day window).");
        }
        else if (sandwichEvents >= 3)
        {
            riskLevel = "high";
            findings.Add($"High MEV exposure: {sandwichEvents} sandwich, {frontruns} frontrun event(s) (score {mevScore}/100).");
        }
        else
        {
            riskLevel = "medium";
            findings.Add($"MEV exposure detected: {sandwichEvents} sandwich, {frontruns} frontrun event(s) (score {mevScore}/100).");
        }

        // Add additional context if present
        if (body.TryGetProperty("totalLossUsd", out var loss) && loss.ValueKind == JsonValueKind.Number)
        {
            var lossVal = loss.GetDouble();
            if (lossVal > 0)
                findings.Add($"Estimated MEV loss: ${lossVal:F2}");
        }

        return (riskLevel, findings);
    }
}
