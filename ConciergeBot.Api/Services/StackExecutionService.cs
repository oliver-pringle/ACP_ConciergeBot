using System.Text.Json;
using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Services;

public sealed class StackExecutionService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<StackExecutionService> _log;

    private static readonly HashSet<string> SupportedBots = new(StringComparer.OrdinalIgnoreCase)
    {
        "TheRevokeBot",
        "TheOracleBot",
        "TheSecurityBot",
        "TheLiquidGuard",
        "TheMEVProtectBot",
        "TheSafeRouteBot"
    };

    private static readonly Dictionary<string, string> BotToHttpClient = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TheRevokeBot"] = "revokebot",
        ["TheOracleBot"] = "oraclebot",
        ["TheSecurityBot"] = "securitybot",
        ["TheLiquidGuard"] = "liquidguard",
        ["TheMEVProtectBot"] = "mevprotect",
        ["TheSafeRouteBot"] = "saferoutebot"
    };

    public StackExecutionService(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<StackExecutionService> log)
    {
        _httpFactory = httpFactory;
        _config = config;
        _log = log;
    }

    public async Task<StackExecutionDeliverable> ExecuteAsync(
        StackExecutionRequest req,
        CancellationToken ct)
    {
        var executed = new List<ExecutedHire>();
        var failed = new List<FailedHire>();
        var risks = new List<string>();
        var totalCost = 0m;

        _log.LogInformation(
            "[stack_execute] Starting execution for {Count} hires, wallet={Wallet}",
            req.RecommendedStack.Length,
            RedactAddress(req.WalletAddress));

        foreach (var rec in req.RecommendedStack)
        {
            if (!SupportedBots.Contains(rec.Agent))
            {
                failed.Add(new FailedHire(
                    rec.Agent,
                    rec.Offering,
                    "skipped - no internal API available; hire manually via ACP marketplace"));
                risks.Add($"{rec.Agent} {rec.Offering} skipped (no internal endpoint)");
                continue;
            }

            var result = await ExecuteSingleHireAsync(rec, req.WalletAddress, req.Chains, ct);
            if (result.Status == "ok")
            {
                executed.Add(result);
                totalCost += result.CostUsdc;
            }
            else
            {
                executed.Add(result);
                failed.Add(new FailedHire(rec.Agent, rec.Offering, result.Status));
            }
        }

        var overallStatus = DetermineOverallStatus(executed, failed, req.RecommendedStack.Length);

        _log.LogInformation(
            "[stack_execute] Completed: status={Status} executed={Ok} failed={Failed} cost={Cost}",
            overallStatus,
            executed.Count(e => e.Status == "ok"),
            failed.Count,
            totalCost);

        return new StackExecutionDeliverable(
            req.Goal,
            overallStatus,
            executed,
            totalCost,
            failed,
            risks);
    }

    private async Task<ExecutedHire> ExecuteSingleHireAsync(
        StackRecommendation rec,
        string walletAddress,
        string[] chains,
        CancellationToken ct)
    {
        var chain = chains.Length > 0 ? chains[0] : "base";

        return rec.Agent.ToLowerInvariant() switch
        {
            "therevokebot" => await ExecuteRevokeBotAsync(rec, walletAddress, chain, ct),
            "theoraclebot" => await ExecuteOracleBotAsync(rec, walletAddress, chain, ct),
            "thesecuritybot" => await ExecuteSecurityBotAsync(rec, ct),
            "theliquidguard" => await ExecuteLiquidGuardAsync(rec, walletAddress, chain, ct),
            "themevprotectbot" => await ExecuteMEVProtectAsync(rec, walletAddress, ct),
            "thesaferoutebot" => await ExecuteSafeRouteAsync(rec, walletAddress, ct),
            _ => new ExecutedHire(rec.Agent, rec.Offering, "skipped", 0m, null)
        };
    }

    private async Task<ExecutedHire> ExecuteRevokeBotAsync(
        StackRecommendation rec,
        string walletAddress,
        string chain,
        CancellationToken ct)
    {
        var apiKey = GetApiKey("RevokeBotApiKey", "REVOKEBOT_INTERNAL_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable - no API key", 0m, null);

        try
        {
            var http = _httpFactory.CreateClient("revokebot");
            var url = $"v1/internal/quote?wallet={Uri.EscapeDataString(walletAddress)}&chain={Uri.EscapeDataString(chain)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add("X-Caller", "conciergebot-stack-execute");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[stack_execute] RevokeBot {Offering} returned {Status}", rec.Offering, resp.StatusCode);
                return new ExecutedHire(rec.Agent, rec.Offering, $"error - HTTP {(int)resp.StatusCode}", 0m, null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return new ExecutedHire(rec.Agent, rec.Offering, "ok", 0.20m, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[stack_execute] RevokeBot {Offering} timed out", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "timeout", 0m, null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[stack_execute] RevokeBot {Offering} failed", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable", 0m, null);
        }
    }

    private async Task<ExecutedHire> ExecuteOracleBotAsync(
        StackRecommendation rec,
        string walletAddress,
        string chain,
        CancellationToken ct)
    {
        var apiKey = GetApiKey("OracleBotApiKey", "ORACLEBOT_INTERNAL_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable - no API key", 0m, null);

        try
        {
            var http = _httpFactory.CreateClient("oraclebot");

            var chainId = chain.ToLowerInvariant() switch
            {
                "base" => 8453,
                "ethereum" => 1,
                _ => 8453
            };

            var requestBody = new
            {
                tokenSymbols = new[] { "ETH", "USDC", "WETH" },
                chainId
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/oracle_bulk");
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add("X-Caller", "conciergebot-stack-execute");
            request.Content = JsonContent.Create(requestBody);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[stack_execute] OracleBot {Offering} returned {Status}", rec.Offering, resp.StatusCode);
                return new ExecutedHire(rec.Agent, rec.Offering, $"error - HTTP {(int)resp.StatusCode}", 0m, null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return new ExecutedHire(rec.Agent, rec.Offering, "ok", 0.05m, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[stack_execute] OracleBot {Offering} timed out", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "timeout", 0m, null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[stack_execute] OracleBot {Offering} failed", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable", 0m, null);
        }
    }

    private async Task<ExecutedHire> ExecuteSecurityBotAsync(
        StackRecommendation rec,
        CancellationToken ct)
    {
        var apiKey = GetApiKey("SecurityBotApiKey", "SECURITYBOT_INTERNAL_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable - no API key", 0m, null);

        var targetUrl = rec.RequirementHint?.GetValueOrDefault("targetUrl")?.ToString();
        if (string.IsNullOrEmpty(targetUrl))
            return new ExecutedHire(rec.Agent, rec.Offering, "skipped - no targetUrl in requirementHint", 0m, null);

        try
        {
            var http = _httpFactory.CreateClient("securitybot");

            var requestBody = new { baseUrl = targetUrl };

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/internal/scan");
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add("X-Caller", "conciergebot-stack-execute");
            request.Content = JsonContent.Create(requestBody);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[stack_execute] SecurityBot {Offering} returned {Status}", rec.Offering, resp.StatusCode);
                return new ExecutedHire(rec.Agent, rec.Offering, $"error - HTTP {(int)resp.StatusCode}", 0m, null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return new ExecutedHire(rec.Agent, rec.Offering, "ok", 0.20m, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[stack_execute] SecurityBot {Offering} timed out", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "timeout", 0m, null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[stack_execute] SecurityBot {Offering} failed", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable", 0m, null);
        }
    }

    private async Task<ExecutedHire> ExecuteLiquidGuardAsync(
        StackRecommendation rec,
        string walletAddress,
        string chain,
        CancellationToken ct)
    {
        var apiKey = GetApiKey("LiquidGuardApiKey", "LIQUIDGUARD_INTERNAL_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable - no API key", 0m, null);

        try
        {
            var http = _httpFactory.CreateClient("liquidguard");

            var protocol = rec.RequirementHint?.GetValueOrDefault("protocol")?.ToString() ?? "aave-v3";
            var url = $"v1/internal/hf?wallet={Uri.EscapeDataString(walletAddress)}&chain={Uri.EscapeDataString(chain)}&protocol={Uri.EscapeDataString(protocol)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add("X-Caller", "conciergebot-stack-execute");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[stack_execute] LiquidGuard {Offering} returned {Status}", rec.Offering, resp.StatusCode);
                return new ExecutedHire(rec.Agent, rec.Offering, $"error - HTTP {(int)resp.StatusCode}", 0m, null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return new ExecutedHire(rec.Agent, rec.Offering, "ok", 0.05m, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[stack_execute] LiquidGuard {Offering} timed out", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "timeout", 0m, null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[stack_execute] LiquidGuard {Offering} failed", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable", 0m, null);
        }
    }

    private async Task<ExecutedHire> ExecuteMEVProtectAsync(
        StackRecommendation rec,
        string walletAddress,
        CancellationToken ct)
    {
        var apiKey = GetApiKey("MEVProtectApiKey", "MEVPROTECT_INTERNAL_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable - no API key", 0m, null);

        try
        {
            var http = _httpFactory.CreateClient("mevprotect");

            var url = $"v1/internal/mev-score?wallet={Uri.EscapeDataString(walletAddress)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add("X-Caller", "conciergebot-stack-execute");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[stack_execute] MEVProtect {Offering} returned {Status}", rec.Offering, resp.StatusCode);
                return new ExecutedHire(rec.Agent, rec.Offering, $"error - HTTP {(int)resp.StatusCode}", 0m, null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return new ExecutedHire(rec.Agent, rec.Offering, "ok", 0.10m, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[stack_execute] MEVProtect {Offering} timed out", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "timeout", 0m, null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[stack_execute] MEVProtect {Offering} failed", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable", 0m, null);
        }
    }

    // Round 19 P2 — the pre-swap safety leg. Unlike the wallet-keyed bots above,
    // SafeRoute is swap-keyed: sellToken/buyToken/amountIn come from the rec's
    // requirementHint and buyer = the stack's walletAddress (the route recipient).
    // It calls SafeRoute's gated /v1/safe_quote (no /v1/internal lane — the paid
    // endpoint IS the X-API-Key-gated surface). Verdict CAUTION/BLOCK is the gate.
    private async Task<ExecutedHire> ExecuteSafeRouteAsync(
        StackRecommendation rec,
        string walletAddress,
        CancellationToken ct)
    {
        var apiKey = GetApiKey("SafeRouteBotApiKey", "SAFEROUTEBOT_INTERNAL_KEY");
        if (string.IsNullOrEmpty(apiKey))
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable - no API key", 0m, null);

        var sellToken = rec.RequirementHint?.GetValueOrDefault("sellToken")?.ToString();
        var buyToken  = rec.RequirementHint?.GetValueOrDefault("buyToken")?.ToString();
        var amountIn  = rec.RequirementHint?.GetValueOrDefault("amountIn")?.ToString();
        if (string.IsNullOrEmpty(sellToken) || string.IsNullOrEmpty(buyToken) || string.IsNullOrEmpty(amountIn))
            return new ExecutedHire(rec.Agent, rec.Offering,
                "skipped - no sellToken/buyToken/amountIn in requirementHint", 0m, null);

        try
        {
            var http = _httpFactory.CreateClient("saferoutebot");
            var requestBody = new { sellToken, buyToken, amountIn, buyer = walletAddress };

            using var request = new HttpRequestMessage(HttpMethod.Post, "v1/safe_quote");
            request.Headers.Add("X-API-Key", apiKey);
            request.Headers.Add("X-Caller", "conciergebot-stack-execute");
            request.Content = JsonContent.Create(requestBody);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));

            using var resp = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[stack_execute] SafeRoute {Offering} returned {Status}", rec.Offering, resp.StatusCode);
                return new ExecutedHire(rec.Agent, rec.Offering, $"error - HTTP {(int)resp.StatusCode}", 0m, null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            return new ExecutedHire(rec.Agent, rec.Offering, "ok", 0.05m, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[stack_execute] SafeRoute {Offering} timed out", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "timeout", 0m, null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[stack_execute] SafeRoute {Offering} failed", rec.Offering);
            return new ExecutedHire(rec.Agent, rec.Offering, "unavailable", 0m, null);
        }
    }

    private string? GetApiKey(string configKey, string envVar)
    {
        return _config[$"PortfolioRun:{configKey}"]
               ?? Environment.GetEnvironmentVariable(envVar);
    }

    private static string DetermineOverallStatus(
        List<ExecutedHire> executed,
        List<FailedHire> failed,
        int total)
    {
        var okCount = executed.Count(e => e.Status == "ok");
        if (okCount == total && failed.Count == 0) return "complete";
        if (okCount == 0) return "failed";
        return "partial";
    }

    private static string RedactAddress(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length < 10)
            return "***";
        return $"{address[..6]}...{address[^4..]}";
    }
}
