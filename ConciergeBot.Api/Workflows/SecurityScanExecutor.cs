using System.Text.Json;
using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Workflows;

public sealed class SecurityScanExecutor : IWorkflowExecutor<WorkflowContext, SecurityScanResult>
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _apiKey;
    private readonly ILogger<SecurityScanExecutor> _log;
    private readonly bool _enabled;
    private readonly string _defaultTargetUrl;

    public string Name => "SecurityScan";

    public SecurityScanExecutor(
        IHttpClientFactory httpFactory,
        IConfiguration config,
        IHostEnvironment env,
        ILogger<SecurityScanExecutor> log)
    {
        _httpFactory = httpFactory;
        _apiKey = config["PortfolioRun:SecurityBotApiKey"]
            ?? Environment.GetEnvironmentVariable("SECURITYBOT_INTERNAL_KEY")
            ?? "";
        _log = log;
        _defaultTargetUrl = config["PortfolioRun:DefaultSecurityTarget"]
            ?? "https://api.acp-metabot.dev/conciergebot/health";

        var baseUrl = config["PortfolioRun:SecurityBotBaseUrl"] ?? "";
        _enabled = !string.IsNullOrEmpty(baseUrl);

        if (_enabled && string.IsNullOrEmpty(_apiKey))
        {
            if (!env.IsDevelopment())
            {
                _log.LogWarning(
                    "[portfolio_run] SECURITYBOT_INTERNAL_KEY not set but SecurityBotBaseUrl is configured. " +
                    "SecurityScan executor will return unavailable for all calls.");
            }
        }
    }

    public async Task<SecurityScanResult> ExecuteAsync(WorkflowContext input, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(_apiKey))
        {
            return new SecurityScanResult(0, ["SecurityBot unavailable (not configured)"], null);
        }

        try
        {
            var http = _httpFactory.CreateClient("securitybot");

            using var req = new HttpRequestMessage(HttpMethod.Post, "v1/internal/scan")
            {
                Content = JsonContent.Create(new
                {
                    baseUrl = _defaultTargetUrl
                })
            };
            req.Headers.Add("X-API-Key", _apiKey);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("[portfolio_run] SecurityBot security_scan returned {Status}", resp.StatusCode);
                return new SecurityScanResult(0, [$"SecurityBot returned {(int)resp.StatusCode}"], null);
            }

            var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var issuesFound = ExtractIssuesFound(body);
            var findings = ExtractFindings(body);

            return new SecurityScanResult(issuesFound, findings, body);
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("[portfolio_run] SecurityBot security_scan timed out");
            return new SecurityScanResult(0, ["SecurityBot unavailable (timeout)"], null);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "[portfolio_run] SecurityBot security_scan failed");
            return new SecurityScanResult(0, ["SecurityBot unavailable"], null);
        }
    }

    private static int ExtractIssuesFound(JsonElement body)
    {
        if (body.TryGetProperty("issuesFound", out var i) && i.ValueKind == JsonValueKind.Number)
            return i.GetInt32();
        if (body.TryGetProperty("vulnerabilities", out var v) && v.ValueKind == JsonValueKind.Array)
            return v.GetArrayLength();
        if (body.TryGetProperty("findings", out var f) && f.ValueKind == JsonValueKind.Array)
            return f.GetArrayLength();
        return 0;
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
                else if (item.TryGetProperty("description", out var desc))
                    findings.Add(desc.GetString() ?? "");
            }
        }
        if (body.TryGetProperty("vulnerabilities", out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray())
            {
                if (item.TryGetProperty("pattern", out var p))
                    findings.Add($"Security issue: {p.GetString()}");
            }
        }
        return findings;
    }
}
