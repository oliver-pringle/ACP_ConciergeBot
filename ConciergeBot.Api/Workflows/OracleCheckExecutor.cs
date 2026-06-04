using System.Text.Json;
using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Workflows;

public sealed class OracleCheckExecutor : IWorkflowExecutor<WorkflowContext, OracleCheckResult>
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<OracleCheckExecutor> _log;
    private readonly bool _enabled;

    public string Name => "OracleCheck";

    public OracleCheckExecutor(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<OracleCheckExecutor> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["PortfolioRun:OracleBotApiKey"]
            ?? Environment.GetEnvironmentVariable("ORACLEBOT_INTERNAL_KEY")
            ?? "";
        _log = log;

        var baseUrl = config["PortfolioRun:OracleBotBaseUrl"] ?? "";
        _enabled = !string.IsNullOrEmpty(baseUrl);

        if (_enabled && string.IsNullOrEmpty(_apiKey))
        {
            if (!env.IsDevelopment())
            {
                _log.LogWarning(
                    "[portfolio_run] ORACLEBOT_INTERNAL_KEY not set but OracleBotBaseUrl is configured. " +
                    "OracleCheck executor will return unavailable for all calls.");
            }
        }
    }

    public async Task<OracleCheckResult> ExecuteAsync(WorkflowContext input, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(_apiKey))
        {
            return new OracleCheckResult(false, ["OracleBot unavailable (not configured)"], null);
        }

        try
        {
            var http = _httpFactory.CreateClient("oraclebot");
            var chainId = MapChainToId(input.Chains.Length > 0 ? input.Chains[0] : "base");
            var tokenSymbols = ExtractTokenSymbols(input.Goal);

            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/internal/oracle_bulk")
            {
                Content = JsonContent.Create(new
                {
                    tokenSymbols,
                    chainId
                })
            };
            req.Headers.Add("X-API-Key", _apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[portfolio_run] OracleBot oracle_check returned {Status}", resp.StatusCode);
                return new OracleCheckResult(false, [$"OracleBot returned {(int)resp.StatusCode}"], null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var deviationFound = ExtractDeviationFound(body);
            var findings = ExtractFindings(body);

            return new OracleCheckResult(deviationFound, findings, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[portfolio_run] OracleBot oracle_check timed out");
            return new OracleCheckResult(false, ["OracleBot unavailable (timeout)"], null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[portfolio_run] OracleBot oracle_check failed");
            return new OracleCheckResult(false, ["OracleBot unavailable"], null);
        }
    }

    private static int MapChainToId(string chain)
    {
        return chain.ToLowerInvariant() switch
        {
            "ethereum" or "eth" or "mainnet" => 1,
            "base" => 8453,
            _ => 8453
        };
    }

    private static string[] ExtractTokenSymbols(string goal)
    {
        var lower = goal.ToLowerInvariant();
        var symbols = new List<string>();

        if (lower.Contains("eth") || lower.Contains("ether")) symbols.Add("ETH");
        if (lower.Contains("btc") || lower.Contains("wbtc") || lower.Contains("bitcoin")) symbols.Add("WBTC");
        if (lower.Contains("link") || lower.Contains("chainlink")) symbols.Add("LINK");
        if (lower.Contains("aave")) symbols.Add("AAVE");
        if (lower.Contains("usdc")) symbols.Add("USDC");

        return symbols.Count >= 3 ? symbols.ToArray() : ["ETH", "USDC", "WETH"];
    }

    private static bool ExtractDeviationFound(JsonElement body)
    {
        if (body.TryGetProperty("deviationFound", out var df) && df.ValueKind == JsonValueKind.True)
            return true;
        if (body.TryGetProperty("deviation", out var d) && d.ValueKind == JsonValueKind.True)
            return true;
        if (body.TryGetProperty("deviationPercent", out var dp) && dp.ValueKind == JsonValueKind.Number)
            return dp.GetDouble() > 1.0;
        return false;
    }

    private static List<string> ExtractFindings(JsonElement body)
    {
        var findings = new List<string>();
        if (body.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in f.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    findings.Add(item.GetString() ?? "");
                else if (item.TryGetProperty("message", out var msg))
                    findings.Add(msg.GetString() ?? "");
            }
        }
        if (body.TryGetProperty("deviationPercent", out var dp) && dp.ValueKind == JsonValueKind.Number)
        {
            var pct = dp.GetDouble();
            if (pct > 1.0)
                findings.Add($"Price deviation of {pct:F2}% detected");
            else
                findings.Add($"Price deviation within tolerance ({pct:F2}%)");
        }
        return findings;
    }
}
