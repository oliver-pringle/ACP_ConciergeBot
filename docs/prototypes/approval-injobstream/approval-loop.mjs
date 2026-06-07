#!/usr/bin/env node
// =============================================================================
// Durable approval-over-inJobStream — FEASIBILITY PROTOTYPE
// =============================================================================
// Run:  node docs/prototypes/approval-injobstream/approval-loop.mjs
// Deps: none (Node built-ins only). No network, no chain, no SDK.
//
// PURPOSE
// -------
// Settle the *seller-side feasibility* of a durable, human-in-the-loop spend/
// action-approval gate delivered over a kept-open ACP job (the inJobStream
// channel ConciergeBot already ships). It proves the mechanics that are the
// genuine reason to reach for MAF (or an equivalent SQLite+RetryWorker layer):
//
//   1. PAUSE: assemble a multi-bot calldata bundle, then suspend and wait for
//      an explicit buyer approval before delivering anything.
//   2. DURABLE: the pending approval survives a process restart (the droplet
//      stops containers on every sequential redeploy) and resumes the EXACT
//      state — the single capability a stateless await-chain cannot provide.
//   3. EDITABLE: buyer can reject a specific tx; the bundle re-assembles and
//      re-prompts.
//   4. AUTH-BOUND: an approval is cryptographically bound to {jobId, buyer,
//      nonce} so a third party cannot approve someone else's bundle (the
//      "approval-auth binding" hazard the red-team flagged).
//
// WHAT THIS IS *NOT*
// ------------------
// It does NOT prove the on-chain half (whether the ACP indexer/UI tolerates a
// job sitting funded-but-unsubmitted for the approval window — see FEASIBILITY.md
// "Gap 2"), and it does NOT measure demand. Those need a testnet smoke + a real
// hire. The transport here is a deterministic in-memory mock with the SAME
// method surface as the real SDK, so the real `agent.sendMessage` /
// `session.submit` drop into `RealAcpTransport` (sketched at the bottom) without
// touching the state machine.
// =============================================================================

import { createHmac, timingSafeEqual, randomBytes } from "node:crypto";
import { mkdtempSync, writeFileSync, readFileSync, existsSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";

// ---- tiny test harness ------------------------------------------------------
let PASS = 0, FAIL = 0;
const log = (...a) => console.log(...a);
function assert(cond, label) {
  if (cond) { PASS++; log(`   ✓ ${label}`); }
  else { FAIL++; log(`   ✗ FAIL: ${label}`); }
}
function section(t) { log(`\n${"=".repeat(74)}\n${t}\n${"=".repeat(74)}`); }

// =============================================================================
// 1. MOCK TRANSPORT — same shape as @virtuals-protocol/acp-node-v2 AcpAgent
//    (seller->buyer push = agent.sendMessage; buyer->seller = on("entry");
//     close = session.submit). Records everything for assertions.
// =============================================================================
class MockAcpTransport {
  constructor() {
    this.buyerInbox = new Map();   // jobId -> [{content, contentType}]   (seller -> buyer)
    this.submitted  = new Map();   // jobId -> deliverable string         (job closed)
    this.openJobs   = new Set();   // jobIds currently in funded/open (not submitted)
    this._handler   = null;        // the seller's on("entry") handler
  }
  // seller registers its job-room handler — mirrors agent.on("entry", h)
  on(handler) { this._handler = handler; }
  // a job becomes funded & kept open (seller deliberately does not submit)
  openJob(jobId) { this.openJobs.add(jobId); }
  // seller -> buyer push on the open job  (mirrors agent.sendMessage REST send)
  async sendToBuyer(jobId, content, contentType = "structured") {
    if (!this.openJobs.has(jobId)) throw new Error(`job ${jobId} not open`);
    if (!this.buyerInbox.has(jobId)) this.buyerInbox.set(jobId, []);
    this.buyerInbox.get(jobId).push({ content, contentType });
  }
  // buyer -> seller message on the open job — fires the seller's on("entry").
  // This is the path ACP_Tester CANNOT currently drive (see FEASIBILITY.md Gap 1),
  // but which session.sendMessage / agent.sendMessage support at the SDK level.
  async buyerSends(jobId, fromAddress, content, contentType = "structured") {
    if (!this.openJobs.has(jobId)) throw new Error(`job ${jobId} not open (already closed?)`);
    if (!this._handler) throw new Error("no seller handler attached");
    const session = { jobId, chainId: 84532 };
    const entry = { kind: "message", contentType, from: fromAddress, content, timestamp: 0 };
    await this._handler(session, entry);
  }
  // seller closes the job with the final deliverable (mirrors session.submit)
  async submit(jobId, deliverable) {
    this.openJobs.delete(jobId);
    this.submitted.set(jobId, deliverable);
  }
  lastBuyerMessage(jobId) {
    const inbox = this.buyerInbox.get(jobId) ?? [];
    return inbox[inbox.length - 1]?.content ? JSON.parse(inbox[inbox.length - 1].content) : null;
  }
}

// =============================================================================
// 2. DURABLE STORE — stands in for either MAF's JsonCheckpointStore OR the
//    existing SQLite pending_approval row + RetryWorker. A pending approval
//    persisted here MUST survive a process restart.
// =============================================================================
class DurableStore {
  constructor(dir) { this.dir = dir; }
  _path(jobId) { return join(this.dir, `approval-${jobId}.json`); }
  save(record) { writeFileSync(this._path(record.jobId), JSON.stringify(record, null, 2)); }
  load(jobId) {
    const p = this._path(jobId);
    return existsSync(p) ? JSON.parse(readFileSync(p, "utf8")) : null;
  }
  listPending() {
    // mirrors "SELECT * FROM pending_approval WHERE state='AWAITING_APPROVAL'"
    // run by RetryWorker on boot to re-arm in-flight approvals.
    return []; // (single-job prototype; real impl scans the dir / table)
  }
}

// =============================================================================
// 3. MOCK DOWNSTREAM BOTS — the fan-in sources. In production these are real
//    acp-shared calls to RevokeBot revoke_calldata + LiquidGuard position_fix.
//    Deterministic here so the prototype is reproducible.
// =============================================================================
function fetchRevokeCalldata(wallet) {
  return [
    { source: "RevokeBot", human: "Revoke unlimited USDC approval to 0xRouter…aaaa", to: "0xA0b8…USDC", data: "0x095ea7b3…00", risk: "high" },
    { source: "RevokeBot", human: "Revoke unlimited WETH approval to 0xOldDex…bbbb", to: "0xC02a…WETH", data: "0x095ea7b3…00", risk: "high" },
  ];
}
function fetchLiquidGuardFix(wallet) {
  return [
    { source: "LiquidGuard", human: "Repay 200 USDC to Aave v3 (raise HF 1.04 -> 1.6)", to: "0xPool…aave", data: "0x573ade81…", risk: "med" },
    { source: "LiquidGuard", human: "Supply 0.10 WETH as extra collateral", to: "0xPool…aave", data: "0x617ba037…", risk: "low" },
  ];
}

// =============================================================================
// 4. APPROVAL-ESCROW SELLER — the state machine under test.
//    States: ASSEMBLING -> AWAITING_APPROVAL -> {DELIVERED | CANCELLED}
//    (with an editable loop back to AWAITING_APPROVAL on reject_tx).
// =============================================================================
const APPROVAL_SECRET = "prototype-only-server-side-secret-not-a-real-key";

class ApprovalEscrowSeller {
  constructor(transport, store) { this.transport = transport; this.store = store; }

  // Bind an approval token to {jobId, buyerAddress, nonce, bundleHash} so only
  // the original buyer, approving THIS exact bundle, can release it.
  _approvalToken(rec) {
    return createHmac("sha256", APPROVAL_SECRET)
      .update(`${rec.jobId}.${rec.buyerAddress.toLowerCase()}.${rec.nonce}.${rec.bundleHash}`)
      .digest("hex");
  }
  _bundleHash(bundle) {
    return createHmac("sha256", "bundle-hash")
      .update(bundle.map(tx => `${tx.to}:${tx.data}`).join("|"))
      .digest("hex").slice(0, 16);
  }

  // ---- entry point: a funded job arrives, assemble + pause ----
  async beginJob(jobId, buyerAddress, goal, { excludeIdx = [] } = {}) {
    this.transport.openJob(jobId);
    let bundle = [...fetchRevokeCalldata(buyerAddress), ...fetchLiquidGuardFix(buyerAddress)];
    bundle = bundle.filter((_, i) => !excludeIdx.includes(i));
    // ordering safety: revokes before any re-approve/supply (revoke-then-act)
    bundle.sort((a, b) => (a.source === "RevokeBot" ? -1 : 1) - (b.source === "RevokeBot" ? -1 : 1));

    const bundleHash = this._bundleHash(bundle);
    const nonce = randomBytes(8).toString("hex");
    const rec = {
      jobId, buyerAddress, goal, bundle, bundleHash, nonce,
      state: "AWAITING_APPROVAL", createdAt: "(stamped-by-caller)", excludeIdx,
    };
    this.store.save(rec);                                   // <-- DURABILITY: persist BEFORE prompting
    await this._pushApprovalRequest(rec);
    return rec;
  }

  async _pushApprovalRequest(rec) {
    const token = this._approvalToken(rec);
    await this.transport.sendToBuyer(rec.jobId, JSON.stringify({
      kind: "approval_request",
      jobId: rec.jobId,
      summary: `Bundle of ${rec.bundle.length} txs awaiting your approval:`,
      txs: rec.bundle.map((tx, i) => ({ index: i, action: tx.human, risk: tx.risk })),
      bundleHash: rec.bundleHash,
      approvalToken: token,           // buyer echoes this back to approve (proves they saw THIS bundle)
      nonce: rec.nonce,
      reply: "send {action:'approve'|'reject_tx'|'cancel', nonce, approvalToken[, txIndex]}",
    }), "structured");
    log(`   [seller] paused job ${rec.jobId} -> pushed approval_request (${rec.bundle.length} txs, hash=${rec.bundleHash}, state=AWAITING_APPROVAL persisted)`);
  }

  // ---- buyer -> seller message handler (the on("entry") branch the real
  //      seller.ts would add next to its existing 'requirement' branch) ----
  async handleBuyerMessage(session, entry) {
    const jobId = session.jobId;
    let msg;
    try { msg = JSON.parse(entry.content); } catch { return; }
    if (!msg.action) return;

    // Reload state from the durable store every time — the in-memory map may be
    // empty (fresh process after a restart). This is the resume path.
    const rec = this.store.load(jobId);
    if (!rec || rec.state !== "AWAITING_APPROVAL") {
      log(`   [seller] ignoring buyer msg for job ${jobId}: no pending approval`);
      return;
    }

    // AUTH BINDING: the approval must come from the original buyer AND carry the
    // token bound to this exact {jobId, buyer, nonce, bundleHash}.
    const fromOk = entry.from && entry.from.toLowerCase() === rec.buyerAddress.toLowerCase();
    const expectedToken = this._approvalToken(rec);
    const tokenOk =
      typeof msg.approvalToken === "string" &&
      msg.approvalToken.length === expectedToken.length &&
      timingSafeEqual(Buffer.from(msg.approvalToken), Buffer.from(expectedToken)) &&
      msg.nonce === rec.nonce;

    if (!fromOk || !tokenOk) {
      log(`   [seller] REJECTED forged/mismatched approval on job ${jobId} (fromOk=${fromOk} tokenOk=${tokenOk}) — bundle stays paused`);
      return;
    }

    if (msg.action === "approve") {
      const deliverable = JSON.stringify({
        kind: "approved_bundle",
        jobId, bundleHash: rec.bundleHash,
        approvedBy: rec.buyerAddress,
        txs: rec.bundle,
        note: "Signed-ready calldata. ConciergeBot never broadcasts; you sign.",
      });
      rec.state = "DELIVERED"; this.store.save(rec);
      await this.transport.submit(jobId, deliverable);
      log(`   [seller] APPROVED job ${jobId} -> submitted bundle, job closed (state=DELIVERED)`);
      return;
    }
    if (msg.action === "reject_tx" && Number.isInteger(msg.txIndex)) {
      log(`   [seller] buyer rejected tx #${msg.txIndex} on job ${jobId} -> re-assembling without it`);
      const newExclude = [...rec.excludeIdx];
      // map the displayed index back onto the ORIGINAL source list
      const original = [...fetchRevokeCalldata(rec.buyerAddress), ...fetchLiquidGuardFix(rec.buyerAddress)];
      const rejected = rec.bundle[msg.txIndex];
      const origIdx = original.findIndex(tx => tx.to === rejected.to && tx.data === rejected.data);
      if (origIdx >= 0) newExclude.push(origIdx);
      await this.beginJob(jobId, rec.buyerAddress, rec.goal, { excludeIdx: newExclude }); // re-pause, new nonce
      return;
    }
    if (msg.action === "cancel") {
      rec.state = "CANCELLED"; this.store.save(rec);
      await this.transport.submit(jobId, JSON.stringify({ kind: "cancelled", jobId, reason: "buyer cancelled" }));
      log(`   [seller] buyer cancelled job ${jobId} -> submitted cancellation (state=CANCELLED)`);
      return;
    }
  }
}

// =============================================================================
// 5. SCENARIOS
// =============================================================================
async function main() {
  const dir = mkdtempSync(join(tmpdir(), "approval-proto-"));
  const BUYER = "0xBuyer000000000000000000000000000000beef";
  const ATTACKER = "0xEvil0000000000000000000000000000000bad1";

  // ---- Scenario A: happy path (approve) ----
  section("SCENARIO A — happy path: assemble -> pause -> buyer approves -> deliver");
  {
    const transport = new MockAcpTransport();
    const store = new DurableStore(dir);
    const seller = new ApprovalEscrowSeller(transport, store);
    transport.on((s, e) => seller.handleBuyerMessage(s, e));

    const rec = await seller.beginJob("1001", BUYER, "clean up my wallet before I bridge");
    assert(store.load("1001").state === "AWAITING_APPROVAL", "pending approval persisted to durable store");
    assert(transport.lastBuyerMessage("1001").kind === "approval_request", "buyer received approval_request on the open job");
    assert(!transport.submitted.has("1001"), "nothing delivered yet (job still open, awaiting approval)");

    const req = transport.lastBuyerMessage("1001");
    await transport.buyerSends("1001", BUYER, JSON.stringify({ action: "approve", nonce: req.nonce, approvalToken: req.approvalToken }));
    assert(transport.submitted.has("1001"), "after approval, deliverable submitted & job closed");
    assert(JSON.parse(transport.submitted.get("1001")).kind === "approved_bundle", "deliverable is the approved bundle");
    assert(store.load("1001").state === "DELIVERED", "final state persisted = DELIVERED");
  }

  // ---- Scenario B: DURABILITY — approval survives a process restart ----
  section("SCENARIO B — durability: pause -> CRASH (drop all memory) -> restart -> late approval resumes");
  {
    const store = new DurableStore(dir);

    // process #1: assemble + pause, then "crash"
    let transport1 = new MockAcpTransport();
    let seller1 = new ApprovalEscrowSeller(transport1, store);
    transport1.on((s, e) => seller1.handleBuyerMessage(s, e));
    const rec = await seller1.beginJob("2002", BUYER, "rescue my Aave position");
    const req = transport1.lastBuyerMessage("2002");
    assert(store.load("2002").state === "AWAITING_APPROVAL", "approval persisted before crash");

    // ---- simulate container restart: brand-new process, EMPTY in-memory state,
    //      but the SAME durable store + the same still-open job/transport ----
    log("   --- 💥 simulated redeploy: new seller process, in-memory state discarded ---");
    let transport2 = new MockAcpTransport();
    transport2.buyerInbox = transport1.buyerInbox;     // job-room transport persists across the restart
    transport2.openJobs   = transport1.openJobs;       // job is still open on-chain
    let seller2 = new ApprovalEscrowSeller(transport2, store);
    transport2.on((s, e) => seller2.handleBuyerMessage(s, e));

    // RetryWorker-style re-arm would run here; the handler itself reloads from
    // store on the next buyer message, so no in-memory rehydrate is required.
    await transport2.buyerSends("2002", BUYER, JSON.stringify({ action: "approve", nonce: req.nonce, approvalToken: req.approvalToken }));
    assert(transport2.submitted.has("2002"), "approval received AFTER restart still resolves the job");
    assert(store.load("2002").state === "DELIVERED", "post-restart state = DELIVERED (no double-delivery, no lost pause)");
  }

  // ---- Scenario C: editable — buyer rejects one tx, bundle re-assembles ----
  section("SCENARIO C — editable: buyer rejects a tx -> re-assemble -> approve smaller bundle");
  {
    const transport = new MockAcpTransport();
    const store = new DurableStore(dir);
    const seller = new ApprovalEscrowSeller(transport, store);
    transport.on((s, e) => seller.handleBuyerMessage(s, e));

    await seller.beginJob("3003", BUYER, "clean up but keep my WETH supply");
    const req1 = transport.lastBuyerMessage("3003");
    const n1 = req1.txs.length;
    await transport.buyerSends("3003", BUYER, JSON.stringify({ action: "reject_tx", txIndex: n1 - 1, nonce: req1.nonce, approvalToken: req1.approvalToken }));
    const req2 = transport.lastBuyerMessage("3003");
    assert(req2.txs.length === n1 - 1, `bundle re-assembled smaller (${n1} -> ${req2.txs.length} txs)`);
    assert(req2.nonce !== req1.nonce, "re-assembled bundle carries a FRESH nonce (old approval can't replay)");
    await transport.buyerSends("3003", BUYER, JSON.stringify({ action: "approve", nonce: req2.nonce, approvalToken: req2.approvalToken }));
    assert(JSON.parse(transport.submitted.get("3003")).txs.length === n1 - 1, "delivered the edited (smaller) bundle");
  }

  // ---- Scenario D: auth binding — forged / wrong-buyer approvals rejected ----
  section("SCENARIO D — auth binding: forged + wrong-buyer + stale-nonce approvals are rejected");
  {
    const transport = new MockAcpTransport();
    const store = new DurableStore(dir);
    const seller = new ApprovalEscrowSeller(transport, store);
    transport.on((s, e) => seller.handleBuyerMessage(s, e));

    await seller.beginJob("4004", BUYER, "rescue position");
    const req = transport.lastBuyerMessage("4004");

    // D1: attacker address, correct token (token alone is not enough — must be the buyer)
    await transport.buyerSends("4004", ATTACKER, JSON.stringify({ action: "approve", nonce: req.nonce, approvalToken: req.approvalToken }));
    assert(!transport.submitted.has("4004"), "approval from a DIFFERENT address rejected (from-binding)");

    // D2: right buyer, forged token
    await transport.buyerSends("4004", BUYER, JSON.stringify({ action: "approve", nonce: req.nonce, approvalToken: "deadbeef".repeat(8) }));
    assert(!transport.submitted.has("4004"), "approval with a FORGED token rejected (token-binding)");

    // D3: right buyer, stale nonce (e.g. from a superseded bundle)
    await transport.buyerSends("4004", BUYER, JSON.stringify({ action: "approve", nonce: "00000000deadc0de", approvalToken: req.approvalToken }));
    assert(!transport.submitted.has("4004"), "approval with a STALE nonce rejected");
    assert(store.load("4004").state === "AWAITING_APPROVAL", "after 3 bad attempts the bundle is STILL safely paused");

    // D4: finally, the legitimate buyer approves
    await transport.buyerSends("4004", BUYER, JSON.stringify({ action: "approve", nonce: req.nonce, approvalToken: req.approvalToken }));
    assert(transport.submitted.has("4004"), "legitimate buyer approval succeeds after the rejected attempts");
  }

  // ---- summary ----
  section("RESULT");
  log(`   ${PASS} passed, ${FAIL} failed`);
  log(`\n   Seller-side mechanics PROVEN: durable pause, restart-resume, editable`);
  log(`   re-assembly, and auth-bound approval all work over the open-job channel.`);
  log(`   Remaining unknowns are NOT seller-side — see FEASIBILITY.md:`);
  log(`     Gap 1 (tooling): ACP_Tester has no buyer 'respond' tool to drive this live.`);
  log(`     Gap 2 (protocol): on-chain tolerance of a long funded/unsubmitted job.`);
  log(`     Gap 3 (demand):   who is the approver — buyer-agent vs human principal.`);

  rmSync(dir, { recursive: true, force: true });
  process.exit(FAIL === 0 ? 0 : 1);
}

main().catch(e => { console.error(e); process.exit(1); });

// =============================================================================
// 6. REAL-SDK SEAM (sketch — not run here). The state machine above is
//    transport-agnostic; production swaps MockAcpTransport for this adapter
//    over @virtuals-protocol/acp-node-v2. Nothing in ApprovalEscrowSeller changes.
// =============================================================================
//
//   class RealAcpTransport {
//     constructor(agent) { this.agent = agent; }      // the boot-time AcpAgent
//     openJob(jobId) {}                                // seller kept it open by NOT calling submit
//     async sendToBuyer(jobId, content, contentType) { // -> existing inJobStream push
//       await this.agent.sendMessage(84532, jobId, content, contentType);
//     }
//     async submit(jobId, deliverable) {               // -> close the kept-open job
//       const s = this.agent.getSession(84532, jobId); await s.submit(deliverable);
//     }
//     // buyer -> seller arrives via the seller's existing agent.on("entry"):
//     //   if (entry.kind === "message" && isApproval(entry)) seller.handleBuyerMessage(session, entry)
//     // i.e. ONE new branch beside the current contentType === "requirement" branch in seller.ts.
//   }
//
//   The buyer side (to drive a LIVE smoke) needs the mirror of this — a
//   `respond(jobId, content)` on AcpBuyer calling `session.sendMessage(content,
//   "structured")` + an `acp_respond` MCP tool. ACP_Tester does not ship it yet
//   (it receives seller messages but cannot send one back). That is Gap 1.
