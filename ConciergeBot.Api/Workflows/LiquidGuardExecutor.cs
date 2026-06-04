using System.Text.Json;
using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Workflows;

public sealed class LiquidGuardExecutor : IWorkflowExecutor<WorkflowContext, LiquidGuardResult>
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<LiquidGuardExecutor> _log;
    private readonly bool _enabled;

    public string Name => "LiquidGuard";

    public LiquidGuardExecutor(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<LiquidGuardExecutor> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["PortfolioRun:LiquidGuardApiKey"]
            ?? Environment.GetEnvironmentVariable("LIQUIDGUARD_INTERNAL_KEY")
            ?? "";
        _log = log;

        var baseUrl = config["PortfolioRun:LiquidGuardBaseUrl"] ?? "";
        _enabled = !string.IsNullOrEmpty(baseUrl);

        if (_enabled && string.IsNullOrEmpty(_apiKey))
        {
            if (!env.IsDevelopment())
            {
                _log.LogWarning(
                    "[portfolio_run] LIQUIDGUARD_INTERNAL_KEY not set but LiquidGuardBaseUrl is configured. " +
                    "LiquidGuard executor will return unavailable for all calls.");
            }
        }
    }

    public async Task<LiquidGuardResult> ExecuteAsync(WorkflowContext input, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(_apiKey))
        {
            return new LiquidGuardResult("unknown", ["LiquidGuard unavailable (not configured)"], null);
        }

        try
        {
            var http = _httpFactory.CreateClient("liquidguard");
            var chain = input.Chains.Length > 0 ? input.Chains[0] : "base";
            var url = $"v1/internal/hf?wallet={Uri.EscapeDataString(input.WalletAddress)}&chain={Uri.EscapeDataString(chain)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-API-Key", _apiKey);
            req.Headers.Add("X-Caller", "conciergebot");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[portfolio_run] LiquidGuard hf_check returned {Status}", resp.StatusCode);
                return new LiquidGuardResult("unknown", [$"LiquidGuard returned {(int)resp.StatusCode}"], null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var (riskLevel, findings) = ExtractRiskAndFindings(body);

            return new LiquidGuardResult(riskLevel, findings, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[portfolio_run] LiquidGuard hf_check timed out");
            return new LiquidGuardResult("unknown", ["LiquidGuard unavailable (timeout)"], null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[portfolio_run] LiquidGuard hf_check failed");
            return new LiquidGuardResult("unknown", ["LiquidGuard unavailable"], null);
        }
    }

    private static (string riskLevel, List<string> findings) ExtractRiskAndFindings(JsonElement body)
    {
        var findings = new List<string>();
        var riskLevel = "low";

        // Check if snapshots array is empty (no DeFi positions)
        if (body.TryGetProperty("snapshots", out var snapshots) && snapshots.ValueKind == JsonValueKind.Array)
        {
            if (snapshots.GetArrayLength() == 0)
            {
                findings.Add("No DeFi positions found");
                return (riskLevel, findings);
            }
        }

        // Extract health factor from aggregated object
        if (body.TryGetProperty("aggregated", out var agg) && agg.ValueKind == JsonValueKind.Object)
        {
            if (agg.TryGetProperty("healthFactor", out var hf) && hf.ValueKind == JsonValueKind.Number)
            {
                var healthFactor = hf.GetDouble();

                if (healthFactor < 1.1)
                {
                    riskLevel = "high";
                    findings.Add($"Critical health factor: {healthFactor:F2} — liquidation imminent");
                }
                else if (healthFactor < 1.5)
                {
                    riskLevel = "medium";
                    findings.Add($"Low health factor: {healthFactor:F2} — liquidation risk");
                }
                else
                {
                    findings.Add($"Health factor: {healthFactor:F2} — within safe range");
                }
            }

            // Extract additional aggregated metrics if present
            if (agg.TryGetProperty("totalCollateralUsd", out var collateral) && collateral.ValueKind == JsonValueKind.Number)
            {
                var col = collateral.GetDouble();
                if (col > 0)
                    findings.Add($"Total collateral: ${col:F2}");
            }

            if (agg.TryGetProperty("totalDebtUsd", out var debt) && debt.ValueKind == JsonValueKind.Number)
            {
                var d = debt.GetDouble();
                if (d > 0)
                    findings.Add($"Total debt: ${d:F2}");
            }
        }

        if (findings.Count == 0)
        {
            findings.Add("No DeFi positions found");
        }

        return (riskLevel, findings);
    }
}
