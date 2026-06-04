import type { Offering } from "./types.js";
import { requireStringLength, requireOneOf } from "../validators.js";

const MAX_GOAL_LENGTH = 2_000;
const MAX_WALLET_LENGTH = 100;
const RISK_TOLERANCES = ["low", "medium", "high"] as const;

export const portfolioRun: Offering = {
  name: "portfolio_run",
  description:
    "Run a real multi-bot portfolio risk check on your wallet. ConciergeBot orchestrates RevokeBot, OracleBot, LiquidGuard, MEVProtect and (conditionally) SecurityBot internally via a sequential + conditional workflow, then returns one unified risk report with per-bot findings and a recommended next step.",
  slaMinutes: 10,
  requirementSchema: {
    type: "object",
    properties: {
      goal: {
        type: "string",
        description: "Plain-English description of the buyer's intent (e.g., 'Check my wallet before I deposit more USDC').",
        maxLength: MAX_GOAL_LENGTH
      },
      walletAddress: {
        type: "string",
        description: "The EVM wallet address to analyze (0x-prefixed, 42 characters)."
      },
      budgetUsdc: {
        type: "number",
        description: "Optional maximum downstream budget in USDC for cross-bot calls. ConciergeBot uses internal calls so this is informational only.",
        minimum: 0.01
      },
      riskTolerance: {
        type: "string",
        description: "Risk tolerance level. 'low' runs all checks including SecurityScan; 'medium'/'high' may skip optional checks.",
        enum: [...RISK_TOLERANCES]
      },
      chains: {
        type: "array",
        description: "Chains to analyze (e.g., ['base', 'ethereum']). At least one required.",
        items: {
          type: "string",
          description: "A chain name such as 'base' or 'ethereum'."
        },
        minItems: 1
      }
    },
    required: ["goal", "walletAddress", "chains"]
  },
  requirementExample: {
    goal: "Check my wallet before I deposit more USDC",
    walletAddress: "0x1234567890abcdef1234567890abcdef12345678",
    budgetUsdc: 0.50,
    riskTolerance: "low",
    chains: ["base", "ethereum"]
  },
  deliverableSchema: {
    type: "object",
    properties: {
      goal: {
        type: "string",
        description: "The buyer goal that was analyzed."
      },
      walletAddress: {
        type: "string",
        description: "The wallet address that was analyzed."
      },
      overallRiskLevel: {
        type: "string",
        description: "Aggregated risk level across all checks: 'low', 'medium', 'high', or 'unknown'."
      },
      findings: {
        type: "array",
        description: "Consolidated list of findings from all downstream bots.",
        items: {
          type: "string",
          description: "A single finding or observation from the portfolio analysis."
        }
      },
      subJobs: {
        type: "array",
        description: "Per-bot execution results.",
        items: {
          type: "object",
          description: "Result from a single downstream bot call.",
          properties: {
            agent: {
              type: "string",
              description: "The downstream agent name (e.g., 'TheRevokeBot')."
            },
            offering: {
              type: "string",
              description: "The offering that was called on the downstream agent."
            },
            status: {
              type: "string",
              description: "Execution status: 'ok', 'unavailable', or 'error'."
            },
            findings: {
              type: "array",
              description: "Findings specific to this sub-job.",
              items: {
                type: "string",
                description: "A finding from this specific bot."
              }
            },
            rawResult: {
              type: "object",
              description: "The raw response from the downstream bot (may be null if unavailable)."
            }
          },
          required: ["agent", "offering", "status", "findings"]
        }
      },
      totalCostUsdc: {
        type: "number",
        description: "Total USDC spent on downstream calls. Always 0 for internal orchestration."
      },
      nextStep: {
        type: "string",
        description: "Recommended next action based on the analysis results."
      },
      risks: {
        type: "array",
        description: "Workflow-level caveats or warnings (e.g., skipped checks, unavailable bots).",
        items: {
          type: "string",
          description: "A workflow-level risk or caveat."
        }
      },
      workflowPattern: {
        type: "string",
        description: "The orchestration pattern used for this execution (e.g., 'sequential_conditional')."
      }
    },
    required: [
      "goal",
      "walletAddress",
      "overallRiskLevel",
      "findings",
      "subJobs",
      "totalCostUsdc",
      "nextStep",
      "risks",
      "workflowPattern"
    ]
  },
  deliverableExample: {
    goal: "Check my wallet before I deposit more USDC",
    walletAddress: "0x1234567890abcdef1234567890abcdef12345678",
    overallRiskLevel: "low",
    findings: [
      "Found 0 risky approval(s)",
      "Price deviation within tolerance (0.12%)"
    ],
    subJobs: [
      {
        agent: "TheRevokeBot",
        offering: "wallet_scan",
        status: "ok",
        findings: ["Found 0 risky approval(s)"],
        rawResult: { riskyApprovals: 0 }
      },
      {
        agent: "TheOracleBot",
        offering: "oracle_check",
        status: "ok",
        findings: ["Price deviation within tolerance (0.12%)"],
        rawResult: { deviationPercent: 0.12 }
      },
      {
        agent: "TheSecurityBot",
        offering: "security_scan",
        status: "ok",
        findings: [],
        rawResult: { issuesFound: 0 }
      }
    ],
    totalCostUsdc: 0,
    nextStep: "No significant risks detected. Safe to proceed with your planned operations.",
    risks: [],
    workflowPattern: "sequential_conditional"
  },
  validate(req) {
    const goal = requireStringLength(req.goal, "goal", MAX_GOAL_LENGTH);
    if (!goal.valid) return goal;

    if (typeof req.walletAddress !== "string" || req.walletAddress.trim() === "") {
      return { valid: false, reason: "walletAddress is required" };
    }
    const wallet = req.walletAddress.trim();
    if (wallet.length < 10 || wallet.length > MAX_WALLET_LENGTH) {
      return { valid: false, reason: `walletAddress must be 10-${MAX_WALLET_LENGTH} characters` };
    }

    if (req.budgetUsdc !== undefined) {
      if (typeof req.budgetUsdc !== "number" || !Number.isFinite(req.budgetUsdc) || req.budgetUsdc < 0.01) {
        return { valid: false, reason: "budgetUsdc must be a number >= 0.01" };
      }
    }

    const risk = requireOneOf(req.riskTolerance, "riskTolerance", RISK_TOLERANCES);
    if (!risk.valid) return risk;

    if (!Array.isArray(req.chains) || req.chains.length === 0) {
      return { valid: false, reason: "chains must be a non-empty array" };
    }
    if (req.chains.some((c: unknown) => typeof c !== "string" || (c as string).trim() === "")) {
      return { valid: false, reason: "chains must contain non-empty strings" };
    }

    return { valid: true };
  },
  async execute(req, ctx) {
    const result = await ctx.client.portfolioRun({
      goal: String(req.goal).trim(),
      walletAddress: String(req.walletAddress).trim(),
      budgetUsdc: typeof req.budgetUsdc === "number" ? req.budgetUsdc : undefined,
      riskTolerance: typeof req.riskTolerance === "string" ? req.riskTolerance : "medium",
      chains: (req.chains as string[]).map(c => String(c).trim().toLowerCase())
    });
    return result;
  }
};
