namespace ConciergeBot.Api.Workflows;

public interface IWorkflowExecutor<TInput, TOutput>
{
    string Name { get; }
    Task<TOutput> ExecuteAsync(TInput input, CancellationToken ct = default);
}

public record WorkflowContext(
    string WalletAddress,
    string[] Chains,
    string RiskTolerance,
    string Goal
);
