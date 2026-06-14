# Build: stack_execute offering on ConciergeBot

## Context

ConciergeBot currently has 2 offerings:
- `route_stack` ($0.05) — recommends a stack of bot/offering hires for a buyer goal, returns `recommendedStack[]` with per-item `{agent, offering, reason, estimatedCostUsdc, requirementHint}`
- `portfolio_run` ($0.35) — runs a multi-bot portfolio risk check via internal HTTP calls (RevokeBot, OracleBot, SecurityBot)

The gap: `route_stack` tells the buyer WHAT to hire, but the buyer has to manually hire each one. We need `stack_execute` — a paid offering that takes the route_stack output and actually executes the recommended hires on the buyer's behalf.

## What to build

### 1. New offering: `stack_execute` ($0.25, one-shot)

**Input schema (requirement):**
```json
{
  "type": "object",
  "properties": {
    "goal": { "type": "string", "description": "Plain-English buyer goal", "maxLength": 2000 },
    "recommendedStack": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "agent": { "type": "string" },
          "offering": { "type": "string" },
          "requirementHint": { "type": "object", "additionalProperties": true }
        },
        "required": ["agent", "offering"]
      }
    },
    "walletAddress": { "type": "string", "description": "EVM wallet to analyze (for risk preflight)" },
    "chains": { "type": "array", "items": { "type": "string" }, "minItems": 1 },
    "riskTolerance": { "type": "string", "enum": ["low", "medium", "high"] }
  },
  "required": ["goal", "recommendedStack", "walletAddress", "chains"]
}
```

**Deliverable:** Per-hire execution results with status, findings, cost breakdown, and any failures.
```
{
  "goal": "...",
  "overallStatus": "partial" | "complete" | "failed",
  "executedHires": [
    {
      "agent": "TheRevokeBot",
      "offering": "wallet_scan",
      "status": "ok" | "failed" | "skipped",
      "costUsdc": 0.20,
      "result": { ... }  // raw hire result
    }
  ],
  "totalCostUsdc": 0.50,
  "failedHires": [],
  "risks": []
}
```

### 2. Implementation approach

**C# side (`ConciergeBot.Api`):**
- Add a new service `StackExecutionService.cs` that:
  - Takes the recommended stack and fans out real ACP hires
  - Uses the existing MAF executor pattern (see `PortfolioRunService.cs` for the sequential+conditional workflow)
  - Each hire: call the downstream bot's ACP offering via the acp-shared internal network
  - Use existing internal endpoints (RevokeBot GET /v1/internal/quote, OracleBot POST /v1/internal/oracle_bulk, SecurityBot POST /v1/internal/scan)
  - For bots WITHOUT internal endpoints (DeFiEval, AgentEval, MEVProtect, etc.), the executor notes them as "skipped — no internal API" and suggests the buyer hire manually
- Add a new endpoint `POST /v1/internal/stack-execute` wired in Program.cs
- Register the service in DI

**TypeScript side (`acp-v2/`):**
- New file: `src/offerings/stack_execute.ts` — follows the existing `portfolio_run.ts` pattern
- Register in `registry.ts`
- Add pricing in `pricing.ts` ($0.25 USDC)
- The offering's handler calls the C# API at `POST /v1/internal/stack-execute`

### 3. Traction filter on route_stack

Modify `RouteStackService.cs` to add a traction filter:
- Before recommending an offering, check if it has any observable hire history
- Use Metabot's existing portfolio data (acp_browse_agent or the rollup endpoint)
- Flag offerings with zero known hires as "unproven" and deprioritize them
- Add a `tractionNotes` field to the route_stack deliverables noting which recommendations have unknown hire history

### 4. Build gates

After implementation, these MUST pass:
- `dotnet build ConciergeBot.Api/ConciergeBot.Api.csproj` — 0 errors, 0 warnings
- `cd acp-v2 && npm run build` — clean tsc
- `cd acp-v2 && npm run print-offerings` — shows 3 offerings: route_stack, portfolio_run, stack_execute
- All Property Descriptions check passes (P32)

### 5. File checklist

Files to create/modify:
- `ConciergeBot.Api/Services/StackExecutionService.cs` — NEW
- `ConciergeBot.Api/ConciergeBot.Api.csproj` — no changes needed
- `ConciergeBot.Api/Program.cs` — add DI + endpoint route
- `acp-v2/src/offerings/stack_execute.ts` — NEW
- `acp-v2/src/offerings/registry.ts` — add stack_execute
- `acp-v2/src/pricing.ts` — add $0.25 price

Files to modify for traction filter:
- `ConciergeBot.Api/Services/RouteStackService.cs` — add traction check

### 6. Existing internal endpoints (use these)

RevokeBot: `GET http://revokebot-api:5000/v1/internal/quote?wallet={addr}&chain={chain}` with `X-API-Key` header
OracleBot: `POST http://oraclebot-api:5000/v1/internal/oracle_bulk` with body `{"tokenSymbols":["ETH","USDC","WETH"],"chainId":8453}` and `X-API-Key` header
SecurityBot: `POST http://securitybot-api:5000/v1/internal/scan` with body `{"baseUrl":"..."}` and `X-API-Key` header

Auth: ALL bots use `X-API-Key` header (NOT `X-Internal-Api-Key`)
Keys are in `PortfolioRun__RevokeBotApiKey`, `PortfolioRun__OracleBotApiKey`, `PortfolioRun__SecurityBotApiKey` (ASP.NET config pattern)

### 7. What to return

After making all changes:
1. Report every file created or modified
2. Run `dotnet build` and report result
3. Run `npm run build` and report result
4. Run `npm run print-offerings` and show the full output
5. Do NOT commit, do NOT push, do NOT deploy

Work in: C:\code_crypto\ACP\ACP_ConciergeBot\
