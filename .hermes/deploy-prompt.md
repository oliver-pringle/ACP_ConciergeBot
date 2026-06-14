# Deploy ConciergeBot to droplet (138.68.174.116)

## Prerequisites check

Before deploying, check for stale processes:
```bash
ssh root@138.68.174.116 "ps aux | grep -E 'docker compose|docker-buildx|buildx bake' | grep -v grep"
```
If any are running, report them and wait. Do NOT start a build while another is active.

## Step 1 — Push the commit first (if not already pushed)

```bash
git -C /c/code_crypto/ACP/ACP_ConciergeBot push origin main
```

## Step 2 — Transfer source files to droplet

The droplet path is `/root/ACP_ConciergeBot/`.

**New files (MUST be explicitly scp'd — rsync won't transfer untracked files):**
```bash
scp ConciergeBot.Api/Models/StackExecutionModels.cs root@138.68.174.116:/root/ACP_ConciergeBot/ConciergeBot.Api/Models/StackExecutionModels.cs
scp ConciergeBot.Api/Services/StackExecutionService.cs root@138.68.174.116:/root/ACP_ConciergeBot/ConciergeBot.Api/Services/StackExecutionService.cs
scp acp-v2/src/offerings/stack_execute.ts root@138.68.174.116:/root/ACP_ConciergeBot/acp-v2/src/offerings/stack_execute.ts
```

**Pull latest for modified files:**
```bash
ssh root@138.68.174.116 "cd /root/ACP_ConciergeBot && git pull --ff-only origin main"
```

## Step 3 — Rebuild containers SEQUENTIALLY

Container names: `conciergebot-api` (C#), `conciergebot-acp` (Node sidecar)

**First: stop and rebuild API:**
```bash
ssh root@138.68.174.116 "cd /root/ACP_ConciergeBot && docker compose stop conciergebot-api && docker compose up -d --build conciergebot-api"
```
Wait for this to finish before proceeding.

**Second: stop and rebuild sidecar:**
```bash
ssh root@138.68.174.116 "cd /root/ACP_ConciergeBot && docker compose stop conciergebot-acp && docker compose up -d --build conciergebot-acp"
```

## Step 4 — Verify

Wait 10 seconds for containers to stabilize, then:

```bash
ssh root@138.68.174.116 "docker compose ps --format '{{.Name}} {{.Status}}'"
```

Verify both containers show "Up" (not restarting).

**Health check:**
```bash
curl -sk https://api.acp-metabot.dev/conciergebot/health
```

Should return 200.

**Smoke test route_stack from inside the sidecar:**
```bash
ssh root@138.68.174.116 "docker exec conciergebot-acp node -e \"
fetch('http://conciergebot-api:5000/v1/internal/portfolio-run', {
  method: 'POST',
  headers: {'Content-Type':'application/json'},
  body: JSON.stringify({goal:'test',walletAddress:'0x0000000000000000000000000000000000000001',chains:['base']})
}).then(r=>r.text().then(t=>console.log(r.status,t.slice(0,500)))).catch(e=>console.error(e.message))
\" 2>&1"
```

Should return 200 with a JSON body containing `subJobs`.

**Check sidecar boot log for delegation:**
```bash
ssh root@138.68.174.116 "docker logs conciergebot-acp --tail 30 2>&1 | grep -i 'delegation\|restart\|waiting\|error\|OK\|ModularAccount'"
```

Look for "delegation OK" or similar — no restart loops, no fatal errors.

## Step 5 — Report

Report:
- Container status (both Up?)
- Health endpoint (200?)
- Sidecar boot log summary (delegation OK? any errors?)
- `print-offerings` from the droplet to confirm 3 offerings visible

To run print-offerings remotely:
```bash
ssh root@138.68.174.116 "docker exec conciergebot-acp node -e \"
import('./acp-v2/dist/scripts/print-offerings-for-registration.js').catch(e=>console.error(e.message))
\" 2>&1" || ssh root@138.68.174.116 "cd /root/ACP_ConciergeBot/acp-v2 && docker compose run --rm conciergebot-acp npm run print-offerings 2>&1"
```

## IMPORTANT RULES
- NEVER print .env files, private keys, or API keys
- Build sequentially — do NOT run both docker compose up -d --build in parallel
- If any step fails, report the error and stop — do NOT keep trying different things
- Do NOT touch any other bot's containers

Work in: C:\code_crypto\ACP\ACP_ConciergeBot\
