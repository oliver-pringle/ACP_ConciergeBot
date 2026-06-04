# ConciergeBot — ACP 2.0 Portfolio Concierge + Orchestrator

A concierge / orchestrator agent for the **Virtuals Protocol ACP marketplace**. It recommends which ACP bots to hire for a goal, and runs a real multi-bot wallet-risk check by fanning out to sibling portfolio bots over the private `acp-shared` network — returning one unified report.

15th portfolio bot. Clone-lineage of `ACP_BasicSubscriptionBot` (it keeps the full tick / subscription / webhook stack) plus a hand-rolled `Workflows/` orchestration layer.

## Offerings (2)

| Offering | Type | Price | Description |
|---|---|---|---|
| `route_stack` | ONE-SHOT | $0.05 USDC | Deterministic keyword-match recommender. Given a plain-English goal (+ optional budget / risk tolerance / chains), returns an ordered ACP stack — agent, offering, reason, cost estimate, hire order, and a ready-to-adapt requirement hint per hire. Pure recommendation; makes **no** downstream calls. |
| `portfolio_run` | ONE-SHOT | $0.35 USDC | Real multi-bot wallet-risk run. Orchestrates RevokeBot, OracleBot, LiquidGuard, MEVProtect and (conditionally) SecurityBot internally via a sequential + conditional workflow, then returns one unified `overallRiskLevel`, consolidated findings, per-bot `subJobs`, and a recommended next step. Downstream calls are internal, so `totalCostUsdc` is `0`. |

`route_stack` knows six portfolio candidates: TheRevokeBot `wallet_scan`, TheOracleBot `oracle_check`, TheSecurityBot `security_scan`, TheLiquidGuard `hf_check`, TheMEVProtectBot `mev_score`, TheEASIssuer `attest_result`.

> **Not Microsoft Agent Framework.** The orchestration is a hand-rolled `IWorkflowExecutor` (sequential `await`s + `if/else`), not MAF. An earlier `portfolio_run` description overclaimed "Microsoft Agent Framework" and was corrected + re-registered 2026-06-04.

## Architecture

C# `ConciergeBot.Api` (orchestration + persistence) + Node `acp-v2` sidecar (ACP protocol layer).

- **`Workflows/`** — `IWorkflowExecutor<TIn,TOut>` + `PortfolioRunWorkflow`: sequential downstream calls with conditional gating (`ShouldRunSecurityScan`, `DetermineOverallRisk`). Each leg degrades to `status: "unavailable"` if that target's cross-bot key is unset, rather than failing the whole run.
- **Cross-bot calls** go over the external `acp-shared` Docker bridge; the caller holds the *target* bot's `INTERNAL_API_KEY` under a disambiguated env var and hits the target's `/v1/internal/*` lane.
- The inherited `ILlmClient` (`/v1/internal/llm-smoke`) is decorative narration only, default Disabled — it never chooses workflow steps.

## Cross-bot wiring (`portfolio_run`)

Each leg is live only when its target key is configured:

| Leg | Status on prod |
|---|---|
| RevokeBot `wallet_scan` | wired |
| OracleBot `oracle_check` | wired |
| SecurityBot `security_scan` (only when `riskTolerance: low`) | wired |
| LiquidGuard `hf_check` | key unconfigured → degrades to `unavailable` |
| MEVProtect `mev_score` | key unconfigured → degrades to `unavailable` |

To enable the LiquidGuard + MEVProtect legs, drop each target's `INTERNAL_API_KEY` into `acp-v2/.env`, add the compose passthroughs (`PortfolioRun__LiquidGuardApiKey` / `PortfolioRun__MEVProtectApiKey`), then `docker compose up -d conciergebot-api`. Runbook: `docs/runbooks/2026-06-04-portfolio-run-liquidguard-mev-keys.md`.

## Local development

```bash
# Terminal 1 — C# API
cd ConciergeBot.Api
dotnet run

# Terminal 2 — ACP sidecar
cd acp-v2
cp .env.example .env       # fill in agent credentials
npm install
npm run dev
```

## Status & cleanup

- **Deployed + registered.** `conciergebot-api` / `conciergebot-acp` have run on the droplet since 2026-06-03; the committed compose tracks prod after the 2026-06-04 backport. Both `route_stack` and `portfolio_run` are registered on app.virtuals.io (MetaMask wallet 3).
- The boilerplate `echo`, `tick_echo`, and `tick_stream_echo` stub offerings and the sample `echoStatus` Resource are **still in `src/offerings/` + `src/resources.ts`** (inherited from BasicSubscriptionBot). They are not part of the product — remove when convenient. ConciergeBot exposes no real Resources yet.

## Design

Concept: `IdeasForACP2.0Bots*.txt` in the parent workspace. MAF feasibility analysis + the inJobStream durable-approval prototype (17/17): `docs/prototypes/approval-injobstream/`.
