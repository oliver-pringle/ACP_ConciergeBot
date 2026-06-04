# Runbook — wire portfolio_run's LiquidGuard + MEVProtect legs

**Date:** 2026-06-04
**Goal:** Make `portfolio_run` call all **5** downstream bots. Today only 3 are wired
(RevokeBot / OracleBot / SecurityBot); the LiquidGuard + MEVProtect executors return
`"unavailable (not configured)"` because their caller keys are unset on the droplet.
**Risk:** Low. The endpoints already exist and are battle-tested (MEVProtect's
`/v1/internal/mev_score` is the same lane Metabot's `RiskPeerClients` consumes). No code
change — config only. No marketplace re-registration needed.

## Verified facts (so this can't silently 404 / 401)

| Leg | ConciergeBot reads (env) | Calls | Target gate |
|---|---|---|---|
| LiquidGuard | `PortfolioRun__LiquidGuardApiKey` (fallback `LIQUIDGUARD_INTERNAL_KEY`) | `GET liquidguard-api:5000/v1/internal/hf?wallet=&chain=` | global `X-API-Key` == LiquidGuard's **`INTERNAL_API_KEY`** (`/v1/internal/*` NOT whitelisted) |
| MEVProtect | `PortfolioRun__MEVProtectApiKey` (fallback `MEVPROTECT_INTERNAL_KEY`) | `GET mevprotect-api:5000/v1/internal/mev_score?wallet=` | global `X-API-Key` == MEVProtect's **`INTERNAL_API_KEY`** |

So each caller key MUST equal the **target bot's own `INTERNAL_API_KEY`** (per the portfolio
cross-bot convention: a caller holds the *target* bot's key under a disambiguated var). The
executor header is `X-API-Key`; both endpoints validate it. The executors are already
`_enabled` (their BaseUrls are set in `appsettings.json`), so the ONLY missing piece is the key.

The committed `docker-compose.yml` now declares both env passthroughs (`${VAR:-}`, empty
default = harmless no-op until `.env` provides the value). The droplet can't cleanly `git pull`
(untracked feature files collide), so apply the compose lines **manually** on the droplet too.

## Steps (on the droplet, `138.68.174.116`)

> Never echo `.env` contents unredacted. Use `sed 's/=.*/=<redacted>/'` to sanity-check shape.

### 1. Grab each target bot's INTERNAL_API_KEY
```bash
# LiquidGuard's key (the value ConciergeBot must send):
grep '^INTERNAL_API_KEY=' /root/ACP_LiquidGuard/.env | sed 's/=.*/=<redacted>/'   # confirm it exists
# MEVProtect's key:
grep '^INTERNAL_API_KEY=' /root/ACP_MEVProtect/.env  | sed 's/=.*/=<redacted>/'
```
(If a bot keeps its key only in compose `environment:` / host shell rather than `.env`, read it
from there. It must be the SAME value that `liquidguard-api` / `mevprotect-api` boot with.)

### 2. Add the two caller keys to ConciergeBot's env  (`/root/ACP_ConciergeBot/acp-v2/.env`)
```bash
cd /root/ACP_ConciergeBot
# .env-newline gotcha: `>>` silent-concatenates onto the previous line if the file lacks a
# trailing newline. Always check + fix first:
tail -c 1 acp-v2/.env | od -An -c          # if last char is NOT \n:
printf '\n' >> acp-v2/.env
# then append (paste the REAL key values from step 1):
printf 'PortfolioRun__LiquidGuardApiKey=%s\n' '<liquidguard INTERNAL_API_KEY>' >> acp-v2/.env
printf 'PortfolioRun__MEVProtectApiKey=%s\n'  '<mevprotect INTERNAL_API_KEY>'  >> acp-v2/.env
sed 's/=.*/=<redacted>/' acp-v2/.env | tail -8     # verify shape, redacted
```

### 3. Add the two passthrough lines to the droplet's `docker-compose.yml`
The committed compose has them (after `PortfolioRun__OracleBotApiKey`); replicate manually on
the droplet (don't `git pull` — it'll abort on the untracked feature files). In the
`conciergebot-api` service `environment:` block, after the OracleBot line, add:
```yaml
      - PortfolioRun__LiquidGuardApiKey=${PortfolioRun__LiquidGuardApiKey:-}
      - PortfolioRun__MEVProtectApiKey=${PortfolioRun__MEVProtectApiKey:-}
```

### 4. Recreate the api container so it re-reads env  (NOT `restart` — `up -d`)
```bash
docker compose --env-file acp-v2/.env up -d conciergebot-api
docker compose logs conciergebot-api --since 1m | grep -i 'portfolio_run\|LIQUIDGUARD\|MEVPROTECT'
# success = NO "…not set but …BaseUrl is configured" warnings for LiquidGuard/MEVProtect
```
> `restart` re-uses the old env; `up -d` recreates with the new `.env`. (Portfolio cross-bot
> key convention — see `feedback_acp_cross_bot_key_sync.md`.)

### 5. Smoke — confirm both legs go from "unavailable" → "ok"
Hire `portfolio_run` once (ACP_Tester, or curl the internal endpoint with a test wallet that
holds a real Aave/Compound position on Base), and check the `subJobs` array:
```
TheLiquidGuardBot  hf_check    -> status "ok"    (was "unavailable")
TheMEVProtectBot   mev_score   -> status "ok"    (was "unavailable")
```
Internal-endpoint smoke (on the droplet bridge, needs ConciergeBot's own key):
```bash
docker exec conciergebot-acp sh -lc 'curl -fsS -X POST http://conciergebot-api:5000/v1/internal/portfolio-run \
  -H "X-API-Key: $CONCIERGEBOT_API_KEY" -H "content-type: application/json" \
  -d "{\"goal\":\"smoke\",\"walletAddress\":\"0x<real-aave-wallet>\",\"chains\":[\"base\"]}"' \
  | python3 -c 'import sys,json; [print(j["agent"],j["offering"],j["status"]) for j in json.load(sys.stdin)["subJobs"]]'
```
Expect `TheLiquidGuardBot hf_check ok` and `TheMEVProtectBot mev_score ok`.

## Rollback
Remove the two `PortfolioRun__*ApiKey` lines from `acp-v2/.env` (or blank them) and
`docker compose up -d conciergebot-api`. The legs fall back to `"unavailable"` — non-fatal;
`portfolio_run` still returns the other 3 legs. No data migration, nothing destructive.

## Notes
- A wrong/short key → the target returns **401** → the executor logs `LiquidGuard returned 401`
  and the leg shows `status: "ok"`? No — a non-2xx makes the leg `"unknown"` with a
  `"...returned 401"` finding. If you see `401` in a leg's findings, the key is wrong (must be
  the *target's* INTERNAL_API_KEY, ≥32 chars). `404` would mean a path/BaseUrl mismatch (not
  expected — endpoints verified present).
- `LiquidGuard /v1/internal/hf` only supports `chain ∈ {base, ethereum}`; `mev_score` is
  Ethereum-mainnet forensics regardless of the requested chain (it ignores `chain`).
- Repo state: committed `docker-compose.yml` now leads prod by these 2 `${VAR:-}` lines
  (intentional — empty-default no-ops). After step 3 the droplet matches again.
