# PushMode: webhook | inJobStream — Design

**Date:** 2026-05-17
**Author:** Oliver Pringle (with Claude Opus 4.7 [1M ctx], max effort)
**Status:** Draft — design only, gated on Phase-1 SDK verification before any code lands
**Target directory:** `C:\code_crypto\ACP\ACP_ConciergeBot\`
**Companion docs:**
- prior spec: `2026-05-03-acp-conciergebot-boilerplate-design.md` (the webhook-only v1 this extends)
- portfolio idea round: `..\..\..\..\IdeasForACP2.0Bots9b.txt` Tier A-1 (the strategic rationale)

## Purpose

Extend `ACP_ConciergeBot` with a second delivery mode — **`inJobStream`** — that pushes per-tick payloads as `AgentMessage` entries on the kept-open ACP job, instead of HMAC-POSTing to a buyer-hosted webhook URL. The existing `webhook` mode is unchanged; `inJobStream` is additive and opt-in per subscription.

After the boilerplate change, the four candidate bots (`ChainlinkBot`, `MEVProtect`, `ArenaBot`, `LiquidGuard`) can each register a streaming subscription offering by setting `pushMode: "inJobStream"` on a single Offering definition. No new bot identity, no new wallet slot, no new SDK invented.

## Why this is needed

`webhook` mode requires the buyer to host a public HTTPS endpoint with stable DNS and HMAC-SHA256 verification. That excludes a large and growing cohort:
- MCP-client buyers running in Claude Desktop / Cursor / Cline (no public HTTPS surface)
- Browser-tab agents
- Butler-style retail orchestrators
- Any buyer behind NAT or carrier-grade CGN
- Buyers who'd otherwise need to stand up Cloudflare Tunnel / ngrok for a $0.05 hire

The ACP V2 SDK (`@virtuals-protocol/acp-node-v2 ^0.0.6`) already terminates a long-lived `SseTransport` (or `SocketTransport`) on the buyer's `AcpAgent`. Buyers naturally consume `AgentMessage` entries via `agent.on("entry", handler)`. If the seller pushes per-tick payloads as `AgentMessage(contentType: "structured")` on the kept-open job, the buyer receives them in real-time at zero extra infrastructure cost — using a transport they already hold open for chat.

Sub-second latency (vs ~250ms HMAC round-trip) also matters for two of the four candidate bots:
- `ChainlinkBot.price_stream` — trading bots, MEV searchers, LP rebalancers want sub-second prices
- `MEVProtect.mempool_stream` — pending-tx detection is inherently push-shaped

## Critical SDK risk — Phase 1 verification gates everything

The SDK has **no `STREAM` offering type** (verified: `AcpAgentOffering` at `node_modules/@virtuals-protocol/acp-node-v2/dist/events/types.d.ts:155-167` has only `name / description / deliverable / requirements / slaMinutes / priceType / priceValue / requiredFunds / isHidden / isPrivate / subscriptions[]`). The pattern proposed here is **architectural composition** on top of three SDK affordances:

1. The seller's `AcpAgent` already terminates SSE/WebSocket connections per active job session.
2. `AcpAgent.sendJobMessage(chainId, jobId, content, contentType?)` — `acpAgent.d.ts:68` — pushes a message to the job room without closing the job.
3. `JobSession.submit(payload)` ends the job. **As long as the seller does not call `submit`, the job stays open and the transport stays alive.**

This is UW Agent's `subscribeFlow` shape and is N=1 in production. Three pieces of behaviour are **unverified** and gate any code change:

- **Q1.** Does the V2 chain contract / indexer / app.virtuals.io UI handle a job that stays in `TRANSACTION` (jobStatus=2) for ≥1 hour without flagging or auto-closing it?
- **Q2.** The offering's `slaMinutes` derives the job's `expiredAt`. For a 60-min stream, slaMinutes must be ≥60. Is there an upper bound on `slaMinutes` that the marketplace registration UI accepts? (RevokeBot's `daily_scan_watch` proved 30 days works for the subscription path, but that path SUBMITS the receipt early and lives outside the job; this path keeps the job itself open.)
- **Q3.** Does buyer-side `AcpAgent.on("entry", handler)` reliably fire for each `sendJobMessage` push across an SSE reconnect? (The SDK's `SseTransport` has `seenEntries` for dedup, but mid-stream reconnect behaviour is undocumented.)

**Phase 1 = build the smoke test (`tick_stream_echo` offering) FIRST and answer Q1/Q2/Q3 with real chain data. Do not roll out to ChainlinkBot/MEVProtect/etc. until Phase 1 passes.** Phase 1 spec is in §10 below.

## Architecture (both modes)

```
                  WEBHOOK MODE (v1, unchanged)
                  ─────────────────────────────
TickSchedulerWorker  ──►  WebhookDeliveryService  ──►  HMAC-POST to buyer's HTTPS URL
(every 10s, fires due
 subscription rows)

                  IN-JOB STREAM MODE (v2, new)
                  ─────────────────────────────
TickSchedulerWorker  ──►  InJobStreamDeliveryService  ──►  POST /v1/internal/push-tick
(same row schema, new                                       on sidecar
 PushMode column)                                          │
                                                            ▼
                                                  sidecar.AcpAgent
                                                  .sendJobMessage(
                                                    chainId, jobId,
                                                    JSON.stringify(payload),
                                                    "structured"
                                                  )
                                                            │
                                                            ▼  via SseTransport / SocketTransport
                                                  buyer's AcpAgent.on("entry", handler)
                                                  fires with kind="message",
                                                  contentType="structured"
```

Both modes share:
- `Subscription` table (schema change: add `PushMode` column, see §4).
- `TickSchedulerWorker` polling cadence and `TicksDelivered` accounting.
- `SubscriptionRun` audit trail.
- `RetryWorker` (no-op for `inJobStream` rows — see §6).

`inJobStream` mode adds:
- **Sidecar HTTP endpoint** `POST /v1/internal/push-tick` (X-API-Key-gated) — the C# tier's only call into the sidecar.
- **`PushMode = "inJobStream"`** rows skip `WebhookDeliveryService` and call `InJobStreamDeliveryService` instead.
- **No `submit()` after subscription receipt** — the seller deliberately keeps the job open. Final submit fires when `TicksDelivered == TicksPurchased`.

## Schema changes

### `subscriptions` table — add 3 columns

```sql
ALTER TABLE subscriptions ADD COLUMN PushMode         TEXT NOT NULL DEFAULT 'webhook';
ALTER TABLE subscriptions ADD COLUMN StreamChainId    INTEGER NULL;
ALTER TABLE subscriptions ADD COLUMN StreamJobId      TEXT    NULL;
```

- `PushMode IN ('webhook', 'inJobStream')`. Defaulted to `webhook` for backward compat — every existing row stays on the v1 path with no migration.
- `StreamChainId` + `StreamJobId` are only populated when `PushMode = 'inJobStream'`. They're the persistent handle into the SDK's job for re-attachment after sidecar restarts.
- `WebhookUrl` + `WebhookSecret` stay NULLABLE in practice for `inJobStream` rows (current schema requires NOT NULL — change to NULL allowed, or accept the sentinel `"stream://"` URL and a zero secret).

### `Subscription` model

```csharp
public record Subscription(
    string Id,
    string JobId,
    string BuyerAgent,
    string OfferingName,
    string RequirementJson,
    string? WebhookUrl,                  // CHANGED: nullable
    string? WebhookSecret,               // CHANGED: nullable
    int IntervalSeconds,
    int TicksPurchased,
    int TicksDelivered,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    DateTime? LastRunAt,
    DateTime NextRunAt,
    string Status,
    int ConsecutiveFailures,
    string PushMode,                     // NEW: "webhook" | "inJobStream"
    int? StreamChainId,                  // NEW: only set when PushMode=inJobStream
    string? StreamJobId                  // NEW: only set when PushMode=inJobStream
);
```

### `Offering` type (TS sidecar) — add `pushMode`

```typescript
export interface SubscriptionConfig {
  pricePerTickUsdc: number;
  minIntervalSeconds: number;
  maxTicks: number;
  maxDurationDays: number;
  tiers: SubscriptionTier[];

  /// NEW: which delivery mode this offering supports. Defaults to "webhook"
  /// for backward compatibility — existing offerings stay on v1 path.
  /// Set to "inJobStream" for SDK-native push (buyer needs no webhook).
  /// Set to "both" if you want the buyer to pick at hire time via a
  /// requirement field `pushMode: "webhook" | "inJobStream"`.
  pushMode?: "webhook" | "inJobStream" | "both";
}
```

When `pushMode` is `"inJobStream"`, the offering's `requirementSchema` SHOULD drop `webhookUrl` as a required field. When `"both"`, the schema MUST include a `pushMode` enum field so the buyer chooses.

## Lifecycle — inJobStream mode

### Hire-time (same as v1, with `submit()` deferred)

```
1. Buyer sends requirement (no webhookUrl needed for inJobStream-only offerings).
2. Sidecar.handleRequirement validates, computes price, calls setBudget.
3. Buyer funds → sidecar.handleJobFunded fires.
4. Sidecar branches on offering.subscription.pushMode:
   - webhook:     existing path (POST /subscriptions, submit receipt, done)
   - inJobStream: same POST /subscriptions BUT supply pushMode + chainId + jobId
                  in the request body; receipt deliverable omits webhookSecret
                  and includes streamProtocolHint instead; THEN call
                  session.sendMessage(JSON.stringify(receipt), "structured")
                  WITHOUT calling session.submit(payload). The job stays open.
```

The receipt sent over the open job (not `submit`'d):

```json
{
  "subscriptionId": "8f3d5a2c9e1b4d7a...",
  "ticksPurchased": 24,
  "intervalSeconds": 3600,
  "expiresAt": "2026-05-18T14:23:11Z",
  "deliveryMode": "inJobStream",
  "streamProtocolHint": "AgentMessage(contentType='structured') per tick; final submit closes the job"
}
```

### Per-tick (every `intervalSeconds`)

```
1. TickSchedulerWorker.TickOnceAsync picks up the due sub (PushMode='inJobStream').
2. TickExecutorService.ComputePayloadAsync produces the tick JSON.
3. Branch on PushMode:
   - webhook:     WebhookDeliveryService.DeliverAsync (existing)
   - inJobStream: InJobStreamDeliveryService.PushAsync — POSTs the payload
                  to the sidecar's POST /v1/internal/push-tick endpoint with
                  {subscriptionId, chainId, jobId, tickNumber, payloadJson}
4. Sidecar's push-tick handler calls
     agent.sendJobMessage(chainId, jobId, payloadJson, "structured")
   No reply is awaited — fire-and-forget over the open transport.
5. Sidecar returns 200 (queued) or 502 (transport down — see §6).
6. C# tier marks SubscriptionRun delivered + updates TicksDelivered + NextRunAt.
7. If TicksDelivered == TicksPurchased → InJobStreamDeliveryService.FinaliseAsync
   calls a second sidecar endpoint POST /v1/internal/submit-final which calls
   session.submit(finalReceipt) and closes the job.
```

### Final submit (job close)

The seller submits a final structured deliverable that summarises the stream:

```json
{
  "subscriptionId": "8f3d5a2c...",
  "ticksDelivered": 24,
  "deliveredAt": "2026-05-18T14:23:11Z",
  "streamSummary": {
    "firstTickAt": "2026-05-17T15:23:11Z",
    "lastTickAt":  "2026-05-18T14:23:11Z",
    "dropouts": 0
  }
}
```

This satisfies the offering's declared `deliverableSchema` for buyers / indexers that expect a job to end with one canonical deliverable.

## Components — new + changed

### New (C# side)

- **`Services/InJobStreamDeliveryService.cs`** — analogue of `WebhookDeliveryService`. Methods:
  - `Task<DeliveryResult> PushAsync(Subscription sub, int tickNumber, string payloadJson, CancellationToken ct)`
  - `Task<DeliveryResult> FinaliseAsync(Subscription sub, string finalPayloadJson, CancellationToken ct)`

  Implementation: `HttpClient.PostAsync` to `http://localhost:6000/v1/internal/push-tick` (or whatever the sidecar's internal port is — current scheme uses 6001 for EASIssuer-style sign endpoints; pick consistent port).

### New (sidecar side)

- **`acp-v2/src/streamPush.ts`** — Express handler for `/v1/internal/push-tick` and `/v1/internal/submit-final`. Holds a reference to the boot-time `AcpAgent` and the active sessions Map. Looks up `session = agent.getSession(chainId, jobId)`; throws if absent (job dropped, race with `job.expired`). Calls `session.sendMessage(payloadJson, "structured")` for push; `session.submit(finalPayloadJson)` for finalise.

- **Wire-up in `seller.ts`** — register the streamPush HTTP server on a new internal port (NOT the marketplace-facing one) gated by `CONCIERGEBOT_API_KEY` so only the C# tier can call it.

### Changed

- **`acp-v2/src/seller.ts handleJobFunded`** — branch on `offering.subscription?.pushMode`:
  ```ts
  if (offering.subscription.pushMode === "inJobStream") {
    // Send receipt as a structured message (NOT submit)
    await session.sendMessage(JSON.stringify(receipt), "structured");
    // Do NOT call session.submit(); job stays in TRANSACTION
  } else {
    // Existing webhook path: submit receipt, end job
    const payload = await toDeliverable(session.jobId, receipt);
    await session.submit(payload);
  }
  ```

- **`Services/SubscriptionService.cs CreateAsync`** — accept new optional `pushMode + chainId + jobId` fields on `CreateSubscriptionRequest`. When `pushMode = 'inJobStream'`:
  - skip the `webhookUrl` + `WebhookUrlValidator` block
  - skip secret generation
  - persist `PushMode = "inJobStream"`, `StreamChainId`, `StreamJobId`
  - keep `windowSeconds <= MaxFutureWindow` bound (90 days) — same as webhook
  - **HARD CAP for inJobStream: `windowSeconds <= MaxStreamWindow` (default 4h)** — until Phase 1 proves longer windows are safe (see §6).

- **`Workers/TickSchedulerWorker.cs ProcessSubscriptionAsync`** — branch on `sub.PushMode`:
  ```csharp
  var result = sub.PushMode switch {
      "inJobStream" => await streamDeliverer.PushAsync(sub, nextTickNumber, payload, ct),
      _             => await webhookDeliverer.DeliverAsync(sub, nextTickNumber, payload, ct),
  };
  // ... existing accounting unchanged
  if (completed) {
    if (sub.PushMode == "inJobStream") {
      await streamDeliverer.FinaliseAsync(sub, BuildFinalReceipt(sub), ct);
    }
  }
  ```

## Cross-cutting concerns

### Retry semantics

- **Webhook mode** — `RetryWorker` retries failed deliveries with exponential backoff (`RetryBackoff.DelayFor(n)`). Unchanged.
- **inJobStream mode** — three failure classes:
  - **Push failed because transport is down** (sidecar can't reach `agent.getSession`, returns 502). Mark the run `retrying` with attempts=1 + 30s backoff. `RetryWorker` will retry. If transport stays down for >5 minutes (= 10 retry attempts at 30s each), mark sub `Status='suspended'` and emit an attestation if EASIssuer is wired.
  - **Job has expired on-chain** (sidecar returns 410 Gone). Mark sub `Status='completed_early'`. No more ticks fire. Best-effort: `agent.sendJobMessage` may still deliver a final receipt via `postMessage` (REST fallback) if the chain state hasn't fully settled.
  - **Buyer disconnected mid-stream** (transport open seller-side, but buyer's `AcpAgent` is down). The seller has no way to know — pushes look successful. This is **acceptable** for streaming; buyers reconnecting via SSE will use `lastEventTimestamp` + `seenEntries` for catchup (SDK-provided). For lost ticks, the buyer can call a separate `gap_fill` REST resource on the bot (out of scope for v1 spec).

### Session re-attachment after sidecar restart

If the sidecar restarts mid-stream:
1. On boot, `AcpAgent.start()` calls `hydrateSessions` (acpAgent.d.ts:64 — private but invoked on `start()`) which restores in-flight sessions from the indexer.
2. For each active row in `subscriptions WHERE PushMode='inJobStream' AND Status='active'`, the sidecar verifies `agent.getSession(chainId, jobId)` returns a session. If absent, mark sub `Status='broken'` and log.
3. New ticks land normally — `streamPush` looks up the session per request.

This is why we persist `StreamChainId + StreamJobId` on the row, not just in process memory.

### Resource limits

- **Concurrent open jobs per bot** — soft-cap at 100 active streams per process. Beyond that, refuse new stream subscriptions with `429 too_many_active_streams`. (Each open transport holds ~50KB of SDK state + an SSE long-poll; 100 = ~5MB + 100 connections, manageable on the 8GB droplet alongside the existing 23 containers.)
- **Max stream window** — `MaxStreamWindow` defaults to 4 hours during Phase 2; raise to 24 hours after Phase 3 stability data. Webhook mode keeps the existing 90-day cap.
- **Push payload size** — same 1MB cap as webhook mode. `sendJobMessage` content is JSON-stringified; ACP v2 chat messages have no documented hard cap but 1MB is a reasonable conservative limit.

### Auth between C# tier and sidecar's new endpoints

- `POST /v1/internal/push-tick` and `POST /v1/internal/submit-final` MUST require `X-API-Key: $CONCIERGEBOT_API_KEY` matching the same shared secret the sidecar uses elsewhere. (Inherited from the existing internal-call pattern.)
- These endpoints MUST NOT be exposed through Caddy — they live on the sidecar's INTERNAL port only, behind the docker bridge.

## Migration / backward compatibility

- **No breaking changes.** Every existing subscription row stays on `PushMode='webhook'` via the column default. `TickSchedulerWorker`'s new branch falls through to `WebhookDeliveryService` for unknown / null `PushMode` values.
- **Offering registration UI** — no change. The marketplace UI doesn't know about `pushMode`; that's a sidecar-internal field. Buyers learn the offering is `inJobStream` by reading the `requirementSchema` (which omits `webhookUrl` and includes `deliveryMode: "inJobStream"` as a constant in the description).
- **Existing offerings unchanged.** `tick_echo` stays webhook-mode. The new `tick_stream_echo` offering (§10) is the Phase-1 test fixture.

## Per-offering opt-in (rollout sketch)

After Phase-1 verification passes:

```typescript
// ChainlinkBot/acp-v2/src/offerings/price_stream.ts
export const priceStream: Offering = {
  name: "price_stream",
  description: "Sub-second price stream for one Chainlink price feed. Buyer subscribes to a feed symbol; bot pushes a structured AgentMessage on each new price tick over the open ACP job. No webhook required.",
  slaMinutes: 60,           // ← single 60-min stream window
  requirementSchema: {
    type: "object",
    properties: {
      symbol:           { type: "string",  description: "Price feed symbol, e.g. ETH/USD" },
      thresholdBps:     { type: "integer", minimum: 0, maximum: 1000, default: 0, description: "Push only on price changes >= N basis points. 0 = push every tick." },
      maxDurationMinutes: { type: "integer", minimum: 5, maximum: 60, description: "How long the stream runs. Buyer pays per minute." }
    },
    required: ["symbol", "maxDurationMinutes"]
  },
  // No webhookUrl field — pushMode is inJobStream-only
  deliverableSchema: { /* final receipt schema */ },
  validate(req) { /* ... */ },
  subscription: {
    pricePerTickUsdc:  0.0001,    // very small; per-tick = one price update
    minIntervalSeconds: 1,        // sub-second push (tick = real-time update)
    maxTicks: 60 * 60,            // 1 push per second for 60 min worst case
    maxDurationDays: 0,           // intentional 0 — gated by maxDurationMinutes
    tiers: [
      { name: "stream_60min", priceUsd: 0.30, durationDays: 7 } // tier exists for marketplace UI
    ],
    pushMode: "inJobStream"
  }
};
```

`MEVProtect.mempool_stream`, `ArenaBot.arena_stream`, `LiquidGuard.hf_stream` follow the same shape with bot-specific payload computation in their `TickExecutorService.ComputePayloadAsync`.

## Open questions (resolve before implementation)

1. **Sub-second cadence vs `TickSchedulerWorker.PollInterval` (10s)** — the existing scheduler polls every 10s. For `price_stream` at 1-second cadence, we need either (a) reduce poll interval to 1s for stream-mode rows only, (b) introduce a separate `StreamPushWorker` with a tighter loop, (c) push from a different trigger (e.g. the bot's existing price-feed subscriber fires the push directly, bypassing the scheduler entirely for streaming offerings).
   - **Lean:** (c). Streaming offerings shouldn't go through the generic per-N-seconds scheduler; the source event IS the tick. ChainlinkBot already runs feed listeners — the listener fires `PushTickAsync` directly when a new price arrives. Generic scheduler handles only periodic-cadence streams (e.g. `arena_stream` every 60s).

2. **What is the right `slaMinutes` for a stream that's expected to stay open >60 min?** The SDK uses `slaMinutes` to derive `expiredAt`. If the marketplace caps `slaMinutes` (e.g. <120), long streams need a different strategy: short jobs with handoff, or accept ~1h max.
   - **Phase 1 must measure this.**

3. **Does V2 indexer count an in-job `sendJobMessage` as activity for reputation / agent_job_count?** If yes, streaming inflates rep artificially (one job = 3600 messages). If no, streaming is invisible to existing rep surface and we need a separate counter.
   - **Phase 1 must check `agent_job_count` deltas pre/post smoke.**

4. **Pricing model for streaming offerings — per-minute vs per-tick?** Per-minute is buyer-friendly (predictable cost); per-tick aligns with delivery cost. Recommend per-minute with `pricePerTickUsdc` defined as `priceUsd / minutes / ticksPerMinute_expected`. Validators clamp.

5. **Does session.sendMessage need REST fallback if transport drops?** `AcpAgent.sendMessage` (vs `sendJobMessage`) is the REST fallback per `acpAgent.d.ts:73`. Recommend the sidecar `streamPush` handler tries `sendJobMessage` first (transport push), falls back to `sendMessage` (REST POST) on transport failure. Both end up as `AgentMessage` entries on the job.

## Acceptance criteria — Phase 1 smoke (`tick_stream_echo`)

Phase 1 ships ONE new offering: `tick_stream_echo`, max 5 minutes / 5 ticks, in `inJobStream` mode only. The acceptance gate before any production rollout:

1. **Build clean** — `cd ConciergeBot.Api && dotnet build` zero warnings; `cd acp-v2 && npm run build` clean tsc.
2. **Unit tests** — new `InJobStreamDeliveryService` tests + new `SubscriptionService.CreateAsync` branch tests, all green.
3. **End-to-end smoke from ACP_Tester** (real testnet hire):
   - Hire `tick_stream_echo` with `intervalSeconds=60, ticks=5`.
   - Confirm subscription receipt arrives as a structured AgentMessage on the open job (NOT via submit).
   - Confirm 5 per-tick `AgentMessage(contentType="structured")` entries fire on the buyer side, at the expected cadence (60s ± 5s tolerance).
   - Confirm the final `submit` fires after tick 5, closing the job cleanly.
4. **Q1 answer logged** — the job spent 5 minutes in `TRANSACTION` state without indexer flagging it. Confirm via `getJob` query and acp_today digest snapshot.
5. **Q2 answer logged** — confirm marketplace UI accepted `slaMinutes=10` for the offering registration. Try `slaMinutes=120` for the longest-stream test (Phase 2 prep).
6. **Q3 answer logged** — kill the buyer's `AcpAgent` mid-stream (after tick 2), restart, confirm SSE reconnect dedup behaviour + whether ticks 3-5 are visible on `getHistory`.
7. **No regressions** — `tick_echo` (the existing webhook-mode offering) still works end-to-end via the existing webhook smoke test.
8. **Concurrent stress** — run 10 simultaneous `tick_stream_echo` hires against the same bot. Confirm no resource leak, no transport drop, all 50 ticks (10×5) land within window.

Promotion gates (in order):
- Phase 1 passes → ship `ChainlinkBot.price_stream` (Phase 2, 1 week).
- Phase 2 stable for 2 weeks → ship `MEVProtect.mempool_stream` (Phase 3, 1 week).
- Phase 3 stable + no ops surprises → ship `LiquidGuard.hf_stream` + `ArenaBot.arena_stream` (Phase 4, 1 week).

If Phase 1 fails on Q1/Q2/Q3, document the failure mode, retain the `PushMode = "webhook"` default for all bots, and revisit when `@virtuals-protocol/acp-node-v2` ships a proper streaming primitive in a future SDK release.

## Out of scope (v1 of this design)

- Buyer-pull catchup of missed ticks (the `gap_fill` resource hinted at in Cross-cutting / Retry). Defer to v1.1 if streaming sees real adoption.
- WebSocket-only mode (vs SSE) — let the SDK pick; both work via `AcpChatTransport`.
- Streaming the `webhook` receipt as a structured message AND submitting (dual delivery) — adds complexity for no buyer value.
- Per-tick EAS attestation — would add ~$0.001 cost per tick and is unnecessary for sub-second streams. Final receipt CAN be attested if the offering caller wants it; that's a one-shot at job close, same cost as any other attest.
- Cross-bot delegated push (one bot pushes ticks on behalf of another bot's open job). Avoid; keep one bot = source of truth per stream.
- Refund-on-stream-failure logic. v1 = no refunds, same as current `webhook` mode; rely on `ConsecutiveFailures` suspending the sub if it's truly broken.

## Files this spec will touch (when implementation lands)

```
ACP_ConciergeBot\
├── ConciergeBot.Api\
│   ├── Models\Subscription.cs                       CHANGED (3 new fields)
│   ├── Data\SubscriptionRepository.cs               CHANGED (new columns in INSERT/SELECT)
│   ├── Data\Db.cs                                   CHANGED (ALTER TABLE migrations)
│   ├── Services\InJobStreamDeliveryService.cs       NEW
│   ├── Services\SubscriptionService.cs              CHANGED (pushMode branch in CreateAsync)
│   ├── Workers\TickSchedulerWorker.cs               CHANGED (branch on PushMode)
│   ├── Workers\RetryWorker.cs                       CHANGED (skip inJobStream rows OR retry sidecar push)
│   ├── Models\Dtos.cs                               CHANGED (CreateSubscriptionRequest gets pushMode/chainId/jobId)
│   └── Program.cs                                   CHANGED (register InJobStreamDeliveryService + HttpClient)
├── acp-v2\
│   └── src\
│       ├── offerings\types.ts                       CHANGED (SubscriptionConfig.pushMode added)
│       ├── offerings\tick_stream_echo.ts            NEW (Phase 1 test offering)
│       ├── offerings\registry.ts                    CHANGED (register tick_stream_echo)
│       ├── seller.ts                                CHANGED (handleJobFunded branches on pushMode)
│       └── streamPush.ts                            NEW (internal HTTP server for push-tick / submit-final)
├── ConciergeBot.Tests\
│   ├── InJobStreamDeliveryServiceTests.cs           NEW
│   └── SubscriptionServiceCreateAsyncTests.cs       CHANGED (add pushMode branch coverage)
└── docs\
    ├── superpowers\specs\2026-05-17-pushmode-injobstream-design.md   THIS DOC
    └── superpowers\plans\2026-05-17-pushmode-phase1.md               NEW once design approved
```

Estimated implementation effort: **5-7 working days for Phase 1** (smoke gate). Ship offerings Phase 2-4 are 1-3 days each per bot after the boilerplate change validates.
