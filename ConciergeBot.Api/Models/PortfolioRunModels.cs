using System.Text.Json.Serialization;

namespace ConciergeBot.Api.Models;

public record PortfolioRunRequest(
    string Goal,
    string WalletAddress,
    decimal BudgetUsdc,
    string RiskTolerance,
    string[] Chains
);

public record PortfolioRunDeliverable(
    string Goal,
    string WalletAddress,
    string OverallRiskLevel,
    List<string> Findings,
    List<SubJobResult> SubJobs,
    decimal TotalCostUsdc,
    string NextStep,
    List<string> Risks,
    string WorkflowPattern
);

public record SubJobResult(
    string Agent,
    string Offering,
    string Status,
    List<string> Findings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? RawResult
);

public record WalletScanResult(
    string RiskLevel,
    List<string> Findings,
    object? RawResult
);

public record OracleCheckResult(
    bool DeviationFound,
    List<string> Findings,
    object? RawResult
);

public record SecurityScanResult(
    int IssuesFound,
    List<string> Findings,
    object? RawResult
);

public record LiquidGuardResult(
    string RiskLevel,
    List<string> Findings,
    object? RawResult
);

public record MEVProtectResult(
    string RiskLevel,
    List<string> Findings,
    object? RawResult
);
