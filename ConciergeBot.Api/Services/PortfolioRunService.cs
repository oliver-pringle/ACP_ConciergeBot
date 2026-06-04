using ConciergeBot.Api.Models;
using ConciergeBot.Api.Workflows;

namespace ConciergeBot.Api.Services;

public sealed class PortfolioRunService
{
    private readonly PortfolioRunWorkflow _workflow;

    public PortfolioRunService(PortfolioRunWorkflow workflow)
    {
        _workflow = workflow;
    }

    public Task<PortfolioRunDeliverable> RunAsync(PortfolioRunRequest req, CancellationToken ct)
    {
        return _workflow.RunAsync(req, ct);
    }
}
