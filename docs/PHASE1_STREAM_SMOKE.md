# Phase-1 inJobStream Smoke Runbook

End-to-end verification gate for the `webhook | inJobStream` PushMode landing in `ACP_ConciergeBot` (and downstream `price_stream` on ACP_ChainlinkBot). Until this runbook passes, **do not register any `inJobStream` offering on app.virtuals.io for production agents**. Spec context: `docs/superpowers/specs/2026-05-17-pushmode-injobstream-design.md` §10.

The script `acp-v2/scripts/phase1-stream-smoke.ts` is the canonical buyer. It hires `tick_stream_echo`, holds the SDK transport open for the full stream window, captures every entry, and prints a PASS/WARN/FAIL/N/A line per checklist item.

---

## Why we're running it on Base Sepolia

The three unverified SDK questions need real chain interaction:
- **Q1** — does V2 tolerate an ACP job in `TRANSACTION` state for 5+ minutes without auto-closing it
- **Q2** — does the marketplace UI accept `slaMinutes` ≥ 10 (the test fixture) at registration time
- **Q3** — does buyer-side `AcpAgent.on("entry")` reliably fire after an SSE reconnect

Sepolia gives us free testnet ETH, doesn't compete for the 5-agent cap on production wallets, and exercises the same SDK / indexer / app.virtuals.io path as mainnet for the questions above.

---

## Prerequisites

### 1. Provision a Sepolia agent for ConciergeBot

Manual UI flow on https://app.virtuals.io/acp/agents/:

1. Create a new V2 agent — call it something like `BSB-Phase1-Test`.
2. **Chain: Base Sepolia (84532)**. Do NOT use mainnet.
3. From **Signers** tab, copy `walletId` + `signerPrivateKey`.
4. Note the **agent wallet address** — this is `SMOKE_SELLER_ADDRESS` later.
5. Skip Offerings tab for now — `npm run print-offerings` blocks below.

### 2. Fund the seller wallet with Sepolia ETH

The seller wallet pays gas to set the budget. Use https://www.alchemy.com/faucets/base-sepolia or any Base Sepolia faucet — 0.05 ETH is plenty.

### 3. Provision (or reuse) a buyer wallet on Sepolia

Three options:
- **Easiest** — reuse the ACP_Tester buyer wallet (`memory/reference_acp_tester_wallet.md`). Its `walletId` + `signerPrivateKey` already work on Sepolia.
- Spin a new throwaway Sepolia agent under a fresh MetaMask wallet purely as buyer.
- Use any existing Sepolia ACP wallet you have credentials for.

### 4. Fund the buyer wallet with Sepolia USDC + ETH

The buyer pays the hire price (5 ticks × $0.01 = $0.05 USDC) plus gas. Sepolia USDC: `0x036CbD53842c5426634e7929541eC2318f3dCF7e`. Faucets: https://faucet.circle.com/ (Base Sepolia, 10 USDC drip).

### 5. Run the BSB sidecar + API locally (or on a server reachable by the seller's wallet)

```powershell
# Terminal 1 — C# API
cd ConciergeBot.Api
$env:ASPNETCORE_ENVIRONMENT="Development"
$env:CONCIERGEBOT_API_KEY="dev-stream-smoke-key"
$env:ALLOW_INSECURE_WEBHOOKS="true"  # only needed if you also smoke tick_echo
dotnet run

# Terminal 2 — Sidecar
cd acp-v2
# Fill .env from the agent's Signers tab — chain must be baseSepolia
cp .env.example .env
# Edit .env with:
#   ACP_WALLET_ADDRESS=<seller agent wallet>
#   ACP_WALLET_ID=<from Signers tab>
#   ACP_SIGNER_PRIVATE_KEY=0x<from Signers tab>
#   ACP_CHAIN=baseSepolia
#   CONCIERGEBOT_API_URL=http://localhost:5000
#   CONCIERGEBOT_API_KEY=dev-stream-smoke-key
#   BASE_RPC_URL=<optional, defaults to Base Sepolia public RPC>
npm install
npm run dev
```

You should see in the sidecar logs:
```
[seller] chain=baseSepolia wallet=0x...
[seller] offerings registered (in code): 3
[streamPush] listening on :6001
[seller] running — waiting for jobs
```

If `[streamPush]` doesn't appear, the PushMode wiring didn't land — re-pull from git.

### 6. Register `tick_stream_echo` on the marketplace

```powershell
cd acp-v2
npm run print-offerings
```

Find the `tick_stream_echo` block and paste it into **app.virtuals.io → BSB-Phase1-Test agent → Offerings → New offering**. Use the tier:
- name: `phase1_smoke`
- price: `0.05` USDC
- duration: `7` days

**Q2 measurement:** the registration form will accept or reject `slaMinutes=10`. If it rejects, record the message — that's an immediate Q2 fail and we need to redesign the offering metadata before continuing.

---

## Running the smoke

```powershell
cd acp-v2

# Required
$env:SMOKE_BUYER_WALLET_ADDRESS="0x<buyer wallet>"
$env:SMOKE_BUYER_WALLET_ID="<buyer's Privy walletId>"
$env:SMOKE_BUYER_SIGNER_PRIVATE_KEY="0x<buyer's signer key>"
$env:SMOKE_SELLER_ADDRESS="0x<BSB-Phase1-Test agent wallet>"
$env:SMOKE_CHAIN="baseSepolia"

# Optional (defaults shown)
$env:SMOKE_INTERVAL_SECONDS="60"
$env:SMOKE_TICKS="5"
$env:SMOKE_MESSAGE="phase1"
$env:SMOKE_RECONNECT_AFTER_TICK="0"      # set to 2 to test Q3
$env:SMOKE_TIMEOUT_MS="600000"           # 10 min — generous beyond the 5 min window

npx tsx scripts/phase1-stream-smoke.ts
```

Total runtime is the stream window + a small buffer — for the defaults that's ~5 min 30 sec.

---

## Expected output (happy path)

```
================================================================
Phase-1 inJobStream Smoke Harness
================================================================
Buyer wallet:    0x...
Seller:          0x...
Chain:           baseSepolia (chainId=84532)
Cadence:         60s × 5 ticks
Reconnect after: tick (never)
Hire-window:     600s

[setup] buyer agent ready
[discover] seller="BSB-Phase1-Test" offering=tick_stream_echo (slaMinutes=10)

[hire] created job 12345

[+   742ms] SYSTEM  job.created
[+  3120ms] SYSTEM  budget.set
[+  3120ms]         funding job (amount=50000)
[+  7400ms] SYSTEM  job.funded
[+  9210ms] RECEIPT structured: {"subscriptionId":"...","ticksPurchased":5,...}
[+ 70112ms] TICK 1  structured: {"subscriptionId":"...","tick":1,"message":"phase1",...}
[+130250ms] TICK 2  structured: {"subscriptionId":"...","tick":2,...}
[+190301ms] TICK 3  structured: ...
[+250410ms] TICK 4  structured: ...
[+310520ms] TICK 5  structured: ...
[+311800ms] SYSTEM  job.completed

[settled] done at +311900ms

================================================================
Phase-1 Checklist Report
================================================================
[PASS] 1   Subscription receipt arrived as AgentMessage(structured), not via submit
        → First structured message captured
[PASS] 2   All 5 ticks delivered as AgentMessage(structured)
        → 5 ticks observed (expected 5)
[PASS] 3   Final submit fired job.completed cleanly
        → job.completed at +311800ms
[PASS] Q1  Q1: Job remained in TRANSACTION for the whole stream (no premature close)
        → 5 ticks delivered before close — V2 indexer tolerated the long-open job
[N/A ] Q2  Q2: slaMinutes upper bound (manual check)
        → Confirm at registration time that app.virtuals.io accepted slaMinutes=10 without error
[N/A ] Q3  Q3: SSE reconnect (not tested)
        → Re-run with SMOKE_RECONNECT_AFTER_TICK=2 to test SSE catchup behaviour
[PASS] 4   Cadence within ±20% of 60s
        → avg interval 60.1s (drift 0.2%)
[PASS] 5   Every tick payload is valid JSON
        → all payloads parsed
[PASS] 6   Each tick payload has {subscriptionId, tick, message}
        → shape OK on every tick
[N/A ] 7   Webhook-mode regression (covered by tick_echo smoke separately)
        → Run the existing tick_echo HMAC webhook smoke after this passes
[N/A ] 8   Concurrent stress (10 simultaneous hires)
        → Manual: run this script with 10 backgrounded instances in parallel once items 1-6 PASS

Summary: 7 PASS, 0 WARN, 0 FAIL, 4 N/A

RESULT: ✅ Phase-1 gate PASSED. Safe to register price_stream on app.virtuals.io.
```

Exit code `0` = green; `1` = at least one FAIL; `2` = setup error.

---

## After PASS — close out the checklist

Items the script can't verify automatically:

1. **Q3 (SSE reconnect)** — re-run with `SMOKE_RECONNECT_AFTER_TICK=2`. The script will stop and re-create its agent between ticks 2 and 3, then watch for the remaining ticks to arrive after reconnect. Compare `tickCount` reported.
2. **Item 7 (webhook regression)** — run any existing `tick_echo` HMAC smoke (e.g. via `ACP_Tester` with a webhook.site URL). Confirm tick_echo is unaffected.
3. **Item 8 (concurrent stress)** — launch 10 background runs of this script against the same BSB agent. Watch sidecar RAM + `[streamPush]` logs; expect no leaks, no transport drops, all 50 ticks delivered within window.
4. **Q2** — write down whether app.virtuals.io accepted `slaMinutes=10` at registration. If it capped you lower, note the upper bound — this is the slaMinutes ceiling for all future inJobStream offerings.

---

## What to do on FAIL

| Failure | Likely cause | Action |
|---|---|---|
| `[discover] tick_stream_echo not found` | Offering not registered on app.virtuals.io | Re-paste the `print-offerings` block; refresh agent profile |
| `[setup] provider create failed` | Wrong walletId / signerPrivateKey / EIP-7702 delegation drift | Check Signers tab; rerun `scripts/provision-7702.ts` on buyer if available |
| `[hire] createJobFromOffering failed: ... budget insufficient` | Buyer wallet out of USDC/ETH | Top up via faucet |
| **Items 1-2 FAIL: no structured messages received** | streamPush server not running, or seller.ts didn't take the inJobStream branch | Check sidecar logs for `[streamPush] listening on :6001` AND `kept job OPEN for stream sub` |
| **Q1 FAIL: job.completed fired before final tick** | V2 indexer auto-closed the long-open job | Hard stop — file an SDK issue, leave inJobStream as code-only across the portfolio |
| Cadence drift > 20% | `TickSchedulerWorker.PollInterval=10s` vs 60s cadence; or worker fell behind | Check API container CPU + SQLite WAL contention; usually fine for >30s cadences |
| Reconnect test (Q3) shows lost ticks after the reconnect | SDK's `seenEntries` dedup doesn't deliver buffered messages on resume | Note the loss rate; if >0%, schemes that need durability need to fall back to webhook |

---

## If Phase-1 passes

1. Repeat the smoke against `price_stream` on TheChainlinkBot **on Sepolia** (don't go straight to mainnet). Register price_stream on a fresh Sepolia-only ChainlinkBot agent and rerun the script with `SMOKE_BUYER_*` pointed at it.
2. Once Sepolia price_stream is green, register price_stream on the **live mainnet** TheChainlinkBot agent (wallet `0x6f28…e738`).
3. Mark the spec's Phase-1 gate as PASSED in
   `ACP_ConciergeBot/docs/superpowers/specs/2026-05-17-pushmode-injobstream-design.md`
4. Proceed to Phase 3 (MEVProtect mempool_stream) per the spec rollout plan.

If Phase-1 fails on Q1/Q2/Q3, leave both BSB's `tick_stream_echo` and ChainlinkBot's `price_stream` UNREGISTERED on app.virtuals.io. The code stays in the repos — when a future `acp-node-v2` SDK release ships a proper streaming primitive, re-run this runbook against the new SDK.
