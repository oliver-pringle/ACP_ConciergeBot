// Scheduled sidecar self-recycle — the deafness guard (2026-07-25).
//
// WHY: the ACP SDK's socket transport can go silently DEAF while the process
// looks healthy: its "disconnect" handler only self-reconnects on
// "io server disconnect", and the reconnect auth callback "proceeds with the
// current token" when a token refresh fails — so an expired-token reconnect
// loop hears no jobs forever, with nothing in the logs. Because the seller's
// response gates the buyer's funding step, a deaf sidecar loses arrivals
// invisibly (easissuer-acp was deaf for days before 2026-07-25's sweep; see
// user-memory project_acp_disk_full_incident_and_funnel_audit_2026_07_24).
// A process restart fully re-authenticates and reconnects in ~15s under the
// compose `restart: unless-stopped` policy — so the cheapest robust defence
// is a planned periodic exit.
//
// WHAT: after SIDECAR_RECYCLE_HOURS (default 24) plus 0-60 min of random
// jitter (so a fleet never recycles in lockstep), exit(0) at the next minute
// tick with no job activity in the last 10 minutes (an in-flight delivery is
// never interrupted). SIDECAR_RECYCLE_HOURS=0 disables (e.g. local dev).
//
// WIRING: const liveness = startLivenessRecycle(); once at boot, and
// liveness.noteActivity(); as the first line of the agent "entry" handler.

export interface LivenessHandle {
  noteActivity(): void;
}

const QUIET_WINDOW_MS = 10 * 60_000;
const CHECK_INTERVAL_MS = 60_000;

export function startLivenessRecycle(opts?: { hours?: number }): LivenessHandle {
  const raw = opts?.hours ?? Number(process.env.SIDECAR_RECYCLE_HOURS ?? "24");
  if (!Number.isFinite(raw) || raw <= 0) {
    console.log("[liveness] self-recycle DISABLED (SIDECAR_RECYCLE_HOURS<=0)");
    return { noteActivity() { /* no-op */ } };
  }

  let lastActivityAt = 0;
  const jitterMs = Math.floor(Math.random() * 3_600_000);
  const dueAt = Date.now() + raw * 3_600_000 + jitterMs;
  console.log(
    `[liveness] self-recycle scheduled in ~${((raw * 3_600_000 + jitterMs) / 3_600_000).toFixed(1)}h ` +
    `(deafness guard; SIDECAR_RECYCLE_HOURS=${raw}, jitter ${(jitterMs / 60_000).toFixed(0)}m)`
  );

  const timer = setInterval(() => {
    if (Date.now() < dueAt) return;
    if (Date.now() - lastActivityAt < QUIET_WINDOW_MS) return; // active job — try again next tick
    console.log("[liveness] planned self-recycle (no job activity in 10m); docker restart policy relaunches this sidecar");
    clearInterval(timer);
    process.exit(0);
  }, CHECK_INTERVAL_MS);
  // Never keep an otherwise-exiting process alive just for the recycle timer.
  timer.unref?.();

  return {
    noteActivity() { lastActivityAt = Date.now(); },
  };
}
