import type { Offering } from "./types.js";
import { requireStringLength, requireOneOf } from "../validators.js";

const MAX_GOAL_LENGTH = 2_000;
const MAX_WALLET_LENGTH = 100;
const RISK_TOLERANCES = ["low", "medium", "high"] as const;

export const stackExecute: Offering = {
  name: "stack_execute",
  description:
    "Execute a recommended bot/offering stack from route_stack. ConciergeBot fans out real internal hires to downstream portfolio bots (RevokeBot, OracleBot, LiquidGuard, MEVProtect, SecurityBot, SafeRouteBot) and returns per-hire results with cost breakdown. Bots without internal endpoints are flagged for manual hire.",
  slaMinutes: 10,
  requirementSchema: {
    type: "object",
    properties: {
      goal: {
        type: "string",
        description: "Plain-English buyer goal describing what the buyer wants to achieve.",
        maxLength: MAX_GOAL_LENGTH
      },
      recommendedStack: {
        type: "array",
        description: "Array of recommended hires from route_stack (or manually constructed). At least one required, max 20.",
        items: {
          type: "object",
          description: "A single recommended hire with agent, offering, and optional requirement hints.",
          properties: {
            agent: {
              type: "string",
              description: "The ACP seller agent name (e.g., 'TheRevokeBot', 'TheOracleBot')."
            },
            offering: {
              type: "string",
              description: "The offering name to execute on that agent (e.g., 'wallet_scan', 'oracle_check')."
            },
            requirementHint: {
              type: "object",
              description: "Optional draft requirement fields for the downstream hire.",
              additionalProperties: {
                description: "A requirement value for the downstream offering."
              }
            }
          },
          required: ["agent", "offering"]
        },
        minItems: 1,
        maxItems: 20
      },
      walletAddress: {
        type: "string",
        description: "The EVM wallet address to analyze (0x-prefixed, 42 characters). Used for risk preflight."
      },
      chains: {
        type: "array",
        description: "Chains to analyze (e.g., ['base', 'ethereum']). At least one required.",
        items: {
          type: "string",
          description: "A chain name such as 'base' or 'ethereum'."
        },
        minItems: 1
      },
      riskTolerance: {
        type: "string",
        description: "Optional risk tolerance level for execution decisions.",
        enum: [...RISK_TOLERANCES]
      }
    },
    required: ["goal", "recommendedStack", "walletAddress", "chains"]
  },
  requirementExample: {
    goal: "Execute the preflight checks recommended by route_stack",
    recommendedStack: [
      {
        agent: "TheRevokeBot",
        offering: "wallet_scan",
        requirementHint: { walletAddress: "0x1234...", chains: ["base"] }
      },
      {
        agent: "TheOracleBot",
        offering: "oracle_check",
        requirementHint: { asset: "ETH/USDC", chain: "base" }
      }
    ],
    walletAddress: "0x1234567890abcdef1234567890abcdef12345678",
    chains: ["base", "ethereum"],
    riskTolerance: "low"
  },
  deliverableSchema: {
    type: "object",
    properties: {
      goal: {
        type: "string",
        description: "The buyer goal that was executed."
      },
      overallStatus: {
        type: "string",
        description: "Execution status: 'complete' (all succeeded), 'partial' (some succeeded), or 'failed' (none succeeded)."
      },
      executedHires: {
        type: "array",
        description: "Per-hire execution results.",
        items: {
          type: "object",
          description: "Result from a single hire attempt.",
          properties: {
            agent: {
              type: "string",
              description: "The downstream agent name."
            },
            offering: {
              type: "string",
              description: "The offering that was executed."
            },
            status: {
              type: "string",
              description: "Execution status: 'ok', 'unavailable', 'timeout', 'error - HTTP XXX', or 'skipped'."
            },
            costUsdc: {
              type: "number",
              description: "USDC cost for this hire (0 if skipped/failed)."
            },
            result: {
              type: "object",
              description: "The raw response from the downstream bot (null if unavailable/failed)."
            }
          },
          required: ["agent", "offering", "status", "costUsdc"]
        }
      },
      totalCostUsdc: {
        type: "number",
        description: "Total USDC spent on successful downstream calls."
      },
      failedHires: {
        type: "array",
        description: "Hires that failed or were skipped.",
        items: {
          type: "object",
          description: "A failed or skipped hire with reason.",
          properties: {
            agent: {
              type: "string",
              description: "The agent that failed."
            },
            offering: {
              type: "string",
              description: "The offering that failed."
            },
            reason: {
              type: "string",
              description: "Reason for failure or skip."
            }
          },
          required: ["agent", "offering", "reason"]
        }
      },
      risks: {
        type: "array",
        description: "Execution-level caveats or warnings.",
        items: {
          type: "string",
          description: "An execution caveat or risk note."
        }
      }
    },
    required: ["goal", "overallStatus", "executedHires", "totalCostUsdc", "failedHires", "risks"]
  },
  deliverableExample: {
    goal: "Execute the preflight checks recommended by route_stack",
    overallStatus: "partial",
    executedHires: [
      {
        agent: "TheRevokeBot",
        offering: "wallet_scan",
        status: "ok",
        costUsdc: 0.20,
        result: { approvalCount: 3, highRiskCount: 0 }
      },
      {
        agent: "TheOracleBot",
        offering: "oracle_check",
        status: "ok",
        costUsdc: 0.05,
        result: { deviationPercent: 0.08 }
      }
    ],
    totalCostUsdc: 0.25,
    failedHires: [
      {
        agent: "TheEASIssuer",
        offering: "attest_result",
        reason: "skipped - no internal API available; hire manually via ACP marketplace"
      }
    ],
    risks: ["TheEASIssuer attest_result skipped (no internal endpoint)"]
  },
  validate(req) {
    const goal = requireStringLength(req.goal, "goal", MAX_GOAL_LENGTH);
    if (!goal.valid) return goal;

    if (!Array.isArray(req.recommendedStack) || req.recommendedStack.length === 0) {
      return { valid: false, reason: "recommendedStack must be a non-empty array" };
    }
    if (req.recommendedStack.length > 20) {
      return { valid: false, reason: "recommendedStack exceeds 20 item limit" };
    }
    for (let i = 0; i < req.recommendedStack.length; i++) {
      const item = req.recommendedStack[i] as Record<string, unknown>;
      if (typeof item?.agent !== "string" || item.agent.trim() === "") {
        return { valid: false, reason: `recommendedStack[${i}].agent is required` };
      }
      if (typeof item?.offering !== "string" || item.offering.trim() === "") {
        return { valid: false, reason: `recommendedStack[${i}].offering is required` };
      }
    }

    if (typeof req.walletAddress !== "string" || req.walletAddress.trim() === "") {
      return { valid: false, reason: "walletAddress is required" };
    }
    const wallet = req.walletAddress.trim();
    if (wallet.length < 10 || wallet.length > MAX_WALLET_LENGTH) {
      return { valid: false, reason: `walletAddress must be 10-${MAX_WALLET_LENGTH} characters` };
    }

    if (!Array.isArray(req.chains) || req.chains.length === 0) {
      return { valid: false, reason: "chains must be a non-empty array" };
    }
    if (req.chains.some((c: unknown) => typeof c !== "string" || (c as string).trim() === "")) {
      return { valid: false, reason: "chains must contain non-empty strings" };
    }

    if (req.riskTolerance !== undefined) {
      const risk = requireOneOf(req.riskTolerance, "riskTolerance", RISK_TOLERANCES);
      if (!risk.valid) return risk;
    }

    return { valid: true };
  },
  async execute(req, ctx) {
    const result = await ctx.client.stackExecute({
      goal: String(req.goal).trim(),
      recommendedStack: (req.recommendedStack as Array<Record<string, unknown>>).map(item => ({
        agent: String(item.agent).trim(),
        offering: String(item.offering).trim(),
        reason: typeof item.reason === "string" ? item.reason : undefined,
        requirementHint: typeof item.requirementHint === "object" && item.requirementHint !== null
          ? item.requirementHint as Record<string, unknown>
          : undefined
      })),
      walletAddress: String(req.walletAddress).trim(),
      chains: (req.chains as string[]).map(c => String(c).trim().toLowerCase()),
      riskTolerance: typeof req.riskTolerance === "string" ? req.riskTolerance : undefined
    });
    return result;
  }
};
