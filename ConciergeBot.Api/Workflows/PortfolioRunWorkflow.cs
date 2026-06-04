using ConciergeBot.Api.Models;

namespace ConciergeBot.Api.Workflows;

public sealed class PortfolioRunWorkflow
{
    private readonly WalletScanExecutor _walletScan;
    private readonly OracleCheckExecutor _oracleCheck;
    private readonly LiquidGuardExecutor _liquidGuard;
    private readonly MEVProtectExecutor _mevProtect;
    private readonly SecurityScanExecutor _securityScan;
    private readonly ILogger<PortfolioRunWorkflow> _log;

    public PortfolioRunWorkflow(
        WalletScanExecutor walletScan,
        OracleCheckExecutor oracleCheck,
        LiquidGuardExecutor liquidGuard,
        MEVProtectExecutor mevProtect,
        SecurityScanExecutor securityScan,
        ILogger<PortfolioRunWorkflow> log)
    {
        _walletScan = walletScan;
        _oracleCheck = oracleCheck;
        _liquidGuard = liquidGuard;
        _mevProtect = mevProtect;
        _securityScan = securityScan;
        _log = log;
    }

    public async Task<PortfolioRunDeliverable> RunAsync(PortfolioRunRequest req, CancellationToken ct)
    {
        var context = new WorkflowContext(
            req.WalletAddress,
            req.Chains,
            req.RiskTolerance,
            req.Goal
        );

        var subJobs = new List<SubJobResult>();
        var allFindings = new List<string>();
        var risks = new List<string>();

        _log.LogInformation(
            "[portfolio_run] Starting workflow for wallet={Wallet} chains={Chains} riskTolerance={RiskTol}",
            RedactAddress(req.WalletAddress),
            string.Join(",", req.Chains),
            req.RiskTolerance);

        // Step 1: WalletScan (always runs)
        var walletResult = await _walletScan.ExecuteAsync(context, ct);
        subJobs.Add(new SubJobResult(
            "TheRevokeBot",
            "wallet_scan",
            walletResult.RawResult is null ? "unavailable" : "ok",
            walletResult.Findings,
            walletResult.RawResult
        ));
        allFindings.AddRange(walletResult.Findings);

        // Step 2: OracleCheck (always runs)
        var oracleResult = await _oracleCheck.ExecuteAsync(context, ct);
        subJobs.Add(new SubJobResult(
            "TheOracleBot",
            "oracle_check",
            oracleResult.RawResult is null ? "unavailable" : "ok",
            oracleResult.Findings,
            oracleResult.RawResult
        ));
        allFindings.AddRange(oracleResult.Findings);

        // Step 3: LiquidGuard (always runs)
        var liquidGuardResult = await _liquidGuard.ExecuteAsync(context, ct);
        subJobs.Add(new SubJobResult(
            "TheLiquidGuardBot",
            "hf_check",
            liquidGuardResult.RawResult is null ? "unavailable" : "ok",
            liquidGuardResult.Findings,
            liquidGuardResult.RawResult
        ));
        allFindings.AddRange(liquidGuardResult.Findings);

        // Step 4: MEVProtect (always runs)
        var mevProtectResult = await _mevProtect.ExecuteAsync(context, ct);
        subJobs.Add(new SubJobResult(
            "TheMEVProtectBot",
            "mev_score",
            mevProtectResult.RawResult is null ? "unavailable" : "ok",
            mevProtectResult.Findings,
            mevProtectResult.RawResult
        ));
        allFindings.AddRange(mevProtectResult.Findings);

        // Step 5: SecurityScan (conditional)
        var shouldRunSecurity = ShouldRunSecurityScan(req.RiskTolerance, walletResult, oracleResult, liquidGuardResult, mevProtectResult);
        if (shouldRunSecurity)
        {
            var securityResult = await _securityScan.ExecuteAsync(context, ct);
            subJobs.Add(new SubJobResult(
                "TheSecurityBot",
                "security_scan",
                securityResult.RawResult is null ? "unavailable" : "ok",
                securityResult.Findings,
                securityResult.RawResult
            ));
            allFindings.AddRange(securityResult.Findings);
        }
        else
        {
            risks.Add("SecurityScan skipped (risk tolerance not low and no elevated risk detected)");
        }

        // Aggregate overall risk
        var overallRisk = DetermineOverallRisk(walletResult, oracleResult, liquidGuardResult, mevProtectResult);
        var nextStep = BuildNextStep(overallRisk, walletResult, oracleResult, liquidGuardResult, mevProtectResult);

        // Add any workflow-level risks
        var unavailableCount = subJobs.Count(j => j.Status == "unavailable");
        if (unavailableCount > 0)
        {
            risks.Add($"{unavailableCount} of {subJobs.Count} downstream bots were unavailable — results may be incomplete");
        }

        _log.LogInformation(
            "[portfolio_run] Completed workflow: overallRisk={Risk} subJobs={Count} unavailable={Unavailable}",
            overallRisk,
            subJobs.Count,
            unavailableCount);

        return new PortfolioRunDeliverable(
            req.Goal,
            req.WalletAddress,
            overallRisk,
            allFindings,
            subJobs,
            0m, // Internal calls, no marketplace USDC
            nextStep,
            risks,
            "sequential_conditional"
        );
    }

    private static bool ShouldRunSecurityScan(
        string riskTolerance,
        WalletScanResult walletResult,
        OracleCheckResult oracleResult,
        LiquidGuardResult liquidGuardResult,
        MEVProtectResult mevProtectResult)
    {
        if (string.Equals(riskTolerance, "low", StringComparison.OrdinalIgnoreCase))
            return true;

        if (walletResult.RiskLevel is "medium" or "high")
            return true;

        if (oracleResult.DeviationFound)
            return true;

        if (liquidGuardResult.RiskLevel is "medium" or "high")
            return true;

        if (mevProtectResult.RiskLevel is "medium" or "high")
            return true;

        return false;
    }

    private static string DetermineOverallRisk(
        WalletScanResult walletResult,
        OracleCheckResult oracleResult,
        LiquidGuardResult liquidGuardResult,
        MEVProtectResult mevProtectResult)
    {
        // High if any executor reports high risk
        if (walletResult.RiskLevel == "high" ||
            oracleResult.DeviationFound ||
            liquidGuardResult.RiskLevel == "high" ||
            mevProtectResult.RiskLevel == "high")
            return "high";

        // Medium if any executor reports medium risk
        if (walletResult.RiskLevel == "medium" ||
            liquidGuardResult.RiskLevel == "medium" ||
            mevProtectResult.RiskLevel == "medium")
            return "medium";

        // Unknown if any critical executor is unavailable
        if (walletResult.RiskLevel == "unknown" ||
            liquidGuardResult.RiskLevel == "unknown" ||
            mevProtectResult.RiskLevel == "unknown")
            return "unknown";

        return "low";
    }

    private static string BuildNextStep(
        string overallRisk,
        WalletScanResult walletResult,
        OracleCheckResult oracleResult,
        LiquidGuardResult liquidGuardResult,
        MEVProtectResult mevProtectResult)
    {
        if (overallRisk == "high")
        {
            if (liquidGuardResult.RiskLevel == "high")
                return "URGENT: Health factor critically low — add collateral or repay debt immediately to avoid liquidation.";
            if (walletResult.RiskLevel == "high")
                return "Review and revoke risky token approvals before depositing more funds.";
            if (oracleResult.DeviationFound)
                return "Wait for oracle prices to stabilize before proceeding with DeFi operations.";
            if (mevProtectResult.RiskLevel == "high")
                return "Consider using private transaction submission (Flashbots, MEV Blocker) for large swaps.";
            return "Address the identified high-risk issues before proceeding.";
        }

        if (overallRisk == "medium")
        {
            if (liquidGuardResult.RiskLevel == "medium")
                return "Monitor your health factor — consider adding collateral as a buffer.";
            if (mevProtectResult.RiskLevel == "medium")
                return "Consider MEV protection for significant DeFi operations.";
            return "Consider reviewing the medium-risk findings before large deposits.";
        }

        if (overallRisk == "unknown")
            return "Some checks were unavailable — retry later or verify manually.";

        return "No significant risks detected. Safe to proceed with your planned operations.";
    }

    private static string RedactAddress(string address)
    {
        if (string.IsNullOrEmpty(address) || address.Length < 10)
            return "***";
        return $"{address[..6]}...{address[^4..]}";
    }
}
