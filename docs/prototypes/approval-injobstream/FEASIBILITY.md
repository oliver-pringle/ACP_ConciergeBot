# Durable approval-over-inJobStream — feasibility findings

**Date:** 2026-06-04
**Question:** Can ConciergeBot run a durable, human-in-the-loop **approval gate** over a
kept-open ACP job (the `inJobStream` channel) — pause after assembling a multi-bot
calldata bundle, wait for the buyer's go/no-go, survive a redeploy, then deliver?
This is the one place MAF (or an equivalent SQLite + RetryWorker layer) is genuinely
load-bearing for this bot, and the path that would make a future "uses MAF" claim true.

**Verdict:** Seller-side mechanics are **proven and feasible**; the SDK supports the full
round-trip; the blockers are **not** in the bot's own code. Two gaps must be closed before
a production tier, and one product decision (who approves) reframes the whole design.

---

## What's proven (runnable)

`node docs/prototypes/approval-injobstream/approval-loop.mjs` → **17/17 pass**.
Dependency-free; mock transport with the same method surface as the real SDK.

| Scenario | Proves |
|---|---|
| A — happy path | assemble bundle → persist pending → push `approval_request` on open job → buyer approves → submit. Nothing is delivered before approval. |
| B — **durability** | pause persisted → **simulated redeploy (in-memory state discarded)** → a *late* approval after restart still resolves the job, exactly once. This is the MAF-load-bearing property a stateless await-chain cannot provide. |
| C — editable | buyer rejects one tx → bundle re-assembles smaller → **fresh nonce** (the superseded approval can't replay) → approve. |
| D — auth binding | approvals from a **different address**, a **forged token**, or a **stale nonce** are all rejected; the bundle stays safely paused; the legitimate buyer still succeeds. Closes the "anyone can approve a stranger's bundle" hazard. |

The state machine (`ApprovalEscrowSeller`) is transport-agnostic. The real SDK drops into
`RealAcpTransport` (sketched at the foot of the prototype) without touching it.

---

## SDK-level feasibility (verified against `@virtuals-protocol/acp-node-v2@^0.0.6` `.d.ts`)

The round-trip is feasible because the transport is already bidirectional on an open job:

- **Seller → buyer push:** `agent.sendMessage(chainId, jobId, content, contentType)` —
  one-shot REST send, "does not require start()/stop()" (`acpAgent.d.ts:73`). ConciergeBot
  **already ships this** as the `inJobStream` push path (`streamPush.ts` → `/v1/internal/push-tick`).
- **Keep the job open:** the seller deliberately does **not** call `session.submit()`; the job
  stays in funded/TRANSACTION state and the transport stays alive. ConciergeBot **already does
  this** for `tick_stream_echo` (`seller.ts:133-149`).
- **Buyer → seller message:** `session.sendMessage(content, contentType?)` (`jobSession.d.ts:23`)
  / `agent.sendMessage(...)`. The seller receives it through its existing
  `agent.on("entry")` handler — **the same path the `requirement` message already uses**
  (`seller.ts:61`, `EntryHandler` in `acpAgent.d.ts:7`). An approval is just a *second* buyer
  message, post-funding. Adding it server-side is **one new branch** beside the current
  `contentType === "requirement"` branch.

So nothing new is needed at the protocol/transport level for the seller half.

---

## Gaps (what stops a live smoke today)

### Gap 1 — tooling: ACP_Tester can RECEIVE but not SEND on an open job
`ACP_Tester/src/buyer.ts` captures seller messages (the `entry.kind === "message"` branch,
`buyer.ts:160-171`) so a buyer *sees* the `approval_request` — but `AcpBuyer` exposes only
`hire` / `browseAgent` / `fund`; there is **no `respond`/`sendMessage`** and no matching MCP
tool (`acp_health`, `acp_browse_agent`, `acp_hire`, `acp_attest_review` only). `hire()` simply
waits for `job.completed`/timeout, so against an approval job it would **block until the
SLA times out** with no way to approve.
**Fix (small):** add `AcpBuyer.respondOnJob(jobId, content)` → `session.sendMessage(content, "structured")`,
expose it as an `acp_respond` MCP tool (and/or a one-off `scripts/smoke-approval.ts`). Then the
existing `mcp__acp-tester__*` flow can drive a real approval.

### Gap 2 — protocol (unverified on-chain): tolerance of a long funded/unsubmitted job
The approval window requires the job to sit **funded-but-unsubmitted** while waiting. The
`inJobStream` design already flags this as an open question and caps streams at **4h**
(`docs/superpowers/specs/2026-05-17-pushmode-injobstream-design.md`, Q1/Q2):
- Does the V2 indexer / app.virtuals.io tolerate a job in TRANSACTION state for the approval
  window without flagging/auto-closing it?
- `slaMinutes` drives `expiredAt`; the approval window **must** fit inside the SLA, and there
  may be an upper bound the registration UI accepts.
**Only a testnet smoke answers this.** Until then, treat the approval window as bounded by the
offering's `slaMinutes` (and keep it short — minutes, not hours, for v1).

### Gap 3 — product (the reframe): who is the approver?
The red-team's sharpest point: on ACP the buyer is an **autonomous agent**, not a human at a
console. A human-in-the-loop pause only has a consumer if a *person* is reachable. Two shapes:
- **(i) Agent-approves-on-job** (what the prototype models): viable via `inJobStream`, but only
  useful if the buying agent is itself wired to surface the request to its principal and reply.
- **(ii) Principal-approves-out-of-band:** the bot emails/links/webhooks the buyer's **human**
  with an approve URL; the job either resolves on the click or the bot delivers a short-lived
  approval token. This sidesteps Gap 1 and the agent-has-no-opinion problem entirely.
**This is the real demand question** — and it's a *design* decision, not a code blocker. Lean
(ii) for retail, support (i) for agent buyers that opt in.

---

## Recommended path to a real "demand-feasibility" answer

1. **Close Gap 1** (~0.5 day): `AcpBuyer.respondOnJob` + `acp_respond` MCP tool + a
   `smoke-approval.ts` that hires an approval offering, reads the `approval_request`, and replies.
2. **Ship a throwaway `approve_echo` offering** on ConciergeBot (the `tick_stream_echo` analogue):
   keep job open → push an `approval_request` → on buyer reply, submit. **Smoke it on Base Sepolia**
   to answer Gap 2 (does the job survive the pause? what `slaMinutes` ceiling does the UI accept?
   does the seller's `on("entry")` fire for the post-funding buyer message?).
3. **Only if 1–2 pass:** decide Gap 3 (approver identity) and build the real `bundle_approve`
   tier (RevokeBot + LiquidGuard fan-in + WitnessBot proof-of-approval), lifting the durable
   state machine from this prototype and Metabot's P61 inner-fund cap for any spend ceiling.

**Net:** the durable-approval pattern is real and the seller-side is done-in-prototype. Do **not**
build the production tier until the Base Sepolia smoke (step 2) confirms the long-open-job
tolerance and you've chosen the approver model. The feasibility risk now lives entirely in
Gap 2 (on-chain) and Gap 3 (product), not in the bot's orchestration.
