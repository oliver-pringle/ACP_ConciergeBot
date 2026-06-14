using System.Text.Json.Serialization;

namespace ConciergeBot.Api.Models;

public record StackExecutionRequest(
    string Goal,
    StackRecommendation[] RecommendedStack,
    string WalletAddress,
    string[] Chains,
    string? RiskTolerance
);

public record StackRecommendation(
    string Agent,
    string Offering,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    Dictionary<string, object?>? RequirementHint
);

public record StackExecutionDeliverable(
    string Goal,
    string OverallStatus,
    List<ExecutedHire> ExecutedHires,
    decimal TotalCostUsdc,
    List<FailedHire> FailedHires,
    List<string> Risks
);

public record ExecutedHire(
    string Agent,
    string Offering,
    string Status,
    decimal CostUsdc,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    object? Result
);

public record FailedHire(
    string Agent,
    string Offering,
    string Reason
);
