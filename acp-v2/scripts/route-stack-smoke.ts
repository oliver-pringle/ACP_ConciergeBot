import assert from "node:assert/strict";
import { getOffering, listOfferings } from "../src/offerings/registry.js";
import { priceFor } from "../src/pricing.js";
import { route } from "../src/router.js";

const offering = getOffering("route_stack");
assert.ok(offering, "route_stack should be registered");
assert.equal(offering.name, "route_stack");
assert.ok(listOfferings().includes("route_stack"), "route_stack should be listed");
assert.equal(priceFor("route_stack", {}).amountUsdc, 0.05);

const validRequirement = {
  goal: "Monitor this Base wallet for risky approvals and oracle risk before I deposit more funds",
  budgetUsdc: 1.0,
  riskTolerance: "low",
  chains: ["base", "ethereum"]
};

const validation = offering.validate(validRequirement);
assert.equal(validation.valid, true, validation.reason);

const missingGoal = offering.validate({ budgetUsdc: 1.0 });
assert.equal(missingGoal.valid, false);
assert.match(missingGoal.reason ?? "", /goal/i);

const badBudget = offering.validate({ ...validRequirement, budgetUsdc: -1 });
assert.equal(badBudget.valid, false);
assert.match(badBudget.reason ?? "", /budgetUsdc/i);

const result = await route("route_stack", validRequirement, {
  client: {
    async createSubscription() { throw new Error("route_stack should not create subscriptions"); },
    async portfolioRun() { throw new Error("route_stack should not call portfolioRun API"); },
    async stackExecute() { throw new Error("route_stack should not call stackExecute API"); }
  }
});

if (result.ok !== true) throw new Error("route_stack route failed");
assert.equal(result.ok, true);

const payload = result.result as any;
assert.equal(payload.goal, validRequirement.goal);
assert.equal(payload.riskTolerance, "low");
assert.ok(Array.isArray(payload.recommendedStack));
assert.ok(payload.recommendedStack.length >= 2, "should recommend at least two portfolio hires for approval/oracle risk");
assert.ok(payload.recommendedStack.some((x: any) => x.agent === "TheRevokeBot" && x.offering === "wallet_scan"));
assert.ok(payload.recommendedStack.some((x: any) => x.agent === "TheOracleBot" && x.offering === "oracle_check"));
assert.ok(payload.totalEstimatedCostUsdc > 0);
assert.ok(payload.totalEstimatedCostUsdc <= validRequirement.budgetUsdc);
assert.ok(Array.isArray(payload.hireOrder));
assert.ok(payload.nextStep.includes("Hire"));

// Round 19 P2: a swap-safety goal must recommend SafeRoute's safe_quote.
const swapResult = await route("route_stack", {
  goal: "Is it safe to swap into this token on Base? check for honeypot / sell tax before I buy",
  chains: ["base"]
}, {
  client: {
    async createSubscription() { throw new Error("route_stack should not create subscriptions"); },
    async portfolioRun() { throw new Error("route_stack should not call portfolioRun API"); },
    async stackExecute() { throw new Error("route_stack should not call stackExecute API"); }
  }
});
assert.equal(swapResult.ok, true);
const swapPayload = swapResult.result as any;
assert.ok(
  swapPayload.recommendedStack.some((x: any) => x.agent === "TheSafeRouteBot" && x.offering === "safe_quote"),
  "swap-safety goal should recommend TheSafeRouteBot safe_quote"
);

console.log(JSON.stringify({ ok: true, offering: "route_stack", recommendations: payload.recommendedStack.length, totalEstimatedCostUsdc: payload.totalEstimatedCostUsdc, swapRecommendsSafeRoute: true }, null, 2));
