using System.Text.Json;
using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Workflows;

public sealed class WalletScanExecutor : IWorkflowExecutor<WorkflowContext, WalletScanResult>
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<WalletScanExecutor> _log;
    private readonly bool _enabled;

    public string Name => "WalletScan";

    public WalletScanExecutor(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<WalletScanExecutor> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["PortfolioRun:RevokeBotApiKey"]
            ?? Environment.GetEnvironmentVariable("REVOKEBOT_INTERNAL_KEY")
            ?? "";
        _log = log;

        var baseUrl = config["PortfolioRun:RevokeBotBaseUrl"] ?? "";
        _enabled = !string.IsNullOrEmpty(baseUrl);

        if (_enabled && string.IsNullOrEmpty(_apiKey))
        {
            if (!env.IsDevelopment())
            {
                _log.LogWarning(
                    "[portfolio_run] REVOKEBOT_INTERNAL_KEY not set but RevokeBotBaseUrl is configured. " +
                    "WalletScan executor will return unavailable for all calls.");
            }
        }
    }

    public async Task<WalletScanResult> ExecuteAsync(WorkflowContext input, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(_apiKey))
        {
            return new WalletScanResult("unknown", ["RevokeBot unavailable (not configured)"], null);
        }

        try
        {
            var http = _httpFactory.CreateClient("revokebot");
            var chain = input.Chains.Length > 0 ? input.Chains[0] : "base";
            var url = $"v1/internal/quote?wallet={Uri.EscapeDataString(input.WalletAddress)}&chain={Uri.EscapeDataString(chain)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-API-Key", _apiKey);
            req.Headers.Add("X-Caller", "conciergebot");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[portfolio_run] RevokeBot wallet_scan returned {Status}", resp.StatusCode);
                return new WalletScanResult("unknown", [$"RevokeBot returned {(int)resp.StatusCode}"], null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var riskLevel = ExtractRiskLevel(body);
            var findings = ExtractFindings(body);

            return new WalletScanResult(riskLevel, findings, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[portfolio_run] RevokeBot wallet_scan timed out");
            return new WalletScanResult("unknown", ["RevokeBot unavailable (timeout)"], null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[portfolio_run] RevokeBot wallet_scan failed");
            return new WalletScanResult("unknown", ["RevokeBot unavailable"], null);
        }
    }

    private static string ExtractRiskLevel(JsonElement body)
    {
        if (body.TryGetProperty("highRiskCount", out var hrc) && hrc.ValueKind == JsonValueKind.Number)
        {
            var count = hrc.GetInt32();
            return count == 0 ? "low" : count <= 2 ? "medium" : "high";
        }
        if (body.TryGetProperty("riskLevel", out var rl) && rl.ValueKind == JsonValueKind.String)
            return rl.GetString() ?? "unknown";
        return "low";
    }

    private static List<string> ExtractFindings(JsonElement body)
    {
        var findings = new List<string>();

        if (body.TryGetProperty("approvalCount", out var ac) && ac.ValueKind == JsonValueKind.Number)
        {
            var count = ac.GetInt32();
            findings.Add($"Found {count} token approval(s)");
        }

        if (body.TryGetProperty("highRiskSpenders", out var hrs) && hrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var spender in hrs.EnumerateArray())
            {
                if (spender.ValueKind == JsonValueKind.String)
                    findings.Add($"High-risk spender: {spender.GetString()}");
            }
        }

        if (body.TryGetProperty("totalAtRiskUsd", out var tar) && tar.ValueKind == JsonValueKind.String)
        {
            var usd = tar.GetString();
            if (!string.IsNullOrEmpty(usd) && usd != "0" && usd != "0.00")
                findings.Add($"Total at risk: ${usd}");
        }

        return findings;
    }
}
