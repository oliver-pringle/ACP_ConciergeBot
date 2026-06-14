# Add traction filter to route_stack

## Context

`route_stack.ts` (C:\code_crypto\ACP\ACP_ConciergeBot\acp-v2\src\offerings\route_stack.ts) is a pure TypeScript offering — it recommends a stack of bot/offering hires based on keyword matching against the buyer's goal. There are 6 hardcoded CANDIDATES in the CANDIDATES array.

The problem: in a V2 marketplace where 1,795 agents exist and ~5 lifetime hires have happened, route_stack should flag offerings that have zero observable hire history so buyers know which recommendations are "unproven."

## What to change

### 1. Add `knownHires` field to Candidate type and CANDIDATES data

Add a `knownHires: number` field to the `Candidate` type and every entry in the CANDIDATES array. Set realistic values based on what we know:

| Agent | Offering | knownHires |
|-------|----------|------------|
| TheRevokeBot | wallet_scan | 2 |
| TheOracleBot | oracle_check | 1 |
| TheSecurityBot | security_scan | 1 |
| TheLiquidGuard | hf_check | 0 |
| TheMEVProtectBot | mev_score | 0 |
| TheEASIssuer | attest_result | 1 |

### 2. Add `traction` field to the deliverable

Each item in `recommendedStack` should gain a `traction` field:
```
"traction": "proven" | "unproven" | "unknown"
```
- `knownHires > 0` → "proven"
- `knownHires === 0` → "unproven"
- candidate not in CANDIDATES (shouldn't happen) → "unknown"

Update the deliverableSchema to include this field.

### 3. Add traction risks

In the `buildRisks` function, if any recommended item has `knownHires === 0`, add a risk note:
"One or more recommended offerings have no observable hire history on the Virtuals marketplace — their reliability is unproven."

### 4. Deprioritize unproven in applyBudget

In `applyBudget`, when selecting candidates and two have similar keyword match, prefer those with `knownHires > 0`. Specifically: when the matched set includes both proven and unproven candidates, sort the matched array so proven candidates come first (preserving their relative order, then unproven after).

### 5. Build gates

After changes:
- `npm run build` — clean tsc
- `npm run print-offerings` — route_stack shows the new `traction` field in deliverableSchema
- `tsx scripts/route-stack-smoke.ts` should still pass (the smoke test should still work — it checks the recommendation structure)

### 6. NO other changes

- Do NOT modify any other files
- Do NOT commit
- Do NOT modify C# code
- Only change `route_stack.ts`

Work in: C:\code_crypto\ACP\ACP_ConciergeBot\
