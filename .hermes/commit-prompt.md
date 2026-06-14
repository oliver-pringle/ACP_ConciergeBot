Commit and push ALL changes in ConciergeBot repo:

1. `git add -A` — stage everything (modified + new files)
2. `git commit -m "feat: stack_execute ($0.25) + traction filter on route_stack

- New offering stack_execute fans out internal hires to RevokeBot,
  OracleBot, SecurityBot, LiquidGuard, MEVProtect; unsupported bots
  flagged for manual hire
- New StackExecutionService.cs (383 lines) with per-bot executors,
  15s timeouts, P4 ResponseHeadersRead, address redaction
- New StackExecutionModels.cs record types
- route_stack now surfaces traction: proven|unproven|unknown per
  recommendation; unproven offerings deprioritized and flagged with
  risk note
- Build gates: dotnet build 0/0, tsc clean, print-offerings 3
  offerings, route-stack-smoke ok:true"`

3. `git push origin main`

Report the commit SHA and confirm push succeeded.

Work in: C:\code_crypto\ACP\ACP_ConciergeBot\
