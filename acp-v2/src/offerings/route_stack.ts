import type { Offering } from "./types.js";
import type { ValidationResult } from "../validators.js";
import { requireOneOf, requireStringLength } from "../validators.js";

const MAX_GOAL_LENGTH = 2_000;
const RISK_TOLERANCES = ["low", "medium", "high"] as const;

type RiskTolerance = typeof RISK_TOLERANCES[number];

type Traction = "proven" | "unproven" | "unknown";

type StackRecommendation = {
  agent: string;
  offering: string;
  reason: string;
  estimatedCostUsdc: number;
  requirementHint: Record<string, unknown>;
  traction: Traction;
};

type Candidate = Omit<StackRecommendation, "traction"> & {
  keywords: string[];
  defaultPick?: boolean;
  knownHires: number;
};

const CANDIDATES: Candidate[] = [
  {
    agent: "TheRevokeBot",
    offering: "wallet_scan",
    reason: "Find risky token approvals and spender exposure before the buyer moves more funds.",
    estimatedCostUsdc: 0.20,
    keywords: ["approval", "approvals", "revoke", "spender", "allowance", "permit2", "wallet risk", "wallet"],
    defaultPick: true,
    knownHires: 2,
    requirementHint: {
      walletAddress: "0x... buyer wallet to scan ...",
      chains: ["base", "ethereum"]
    }
  },
  {
    agent: "TheOracleBot",
    offering: "oracle_check",
    reason: "Compare price sources and flag oracle or peg/depeg risk before acting.",
    estimatedCostUsdc: 0.05,
    keywords: ["oracle", "price", "peg", "depeg", "feed", "twap", "pyth", "chainlink"],
    defaultPick: true,
    knownHires: 1,
    requirementHint: {
      asset: "TOKEN/USDC or pool address",
      chain: "base"
    }
  },
  {
    agent: "TheSecurityBot",
    offering: "security_scan",
    reason: "Run a passive security check for externally observable ACP/API issues.",
    estimatedCostUsdc: 0.20,
    keywords: ["security", "audit", "scan", "vulnerability", "webhook", "api", "risk"],
    knownHires: 1,
    requirementHint: {
      targetUrl: "https://... public bot or API endpoint ..."
    }
  },
  {
    agent: "TheLiquidGuard",
    offering: "hf_check",
    reason: "Check lending health factor and liquidation distance for DeFi positions.",
    estimatedCostUsdc: 0.05,
    keywords: ["aave", "compound", "morpho", "health factor", "liquidation", "borrow", "collateral", "defi position"],
    knownHires: 0,
    requirementHint: {
      walletAddress: "0x... position owner ...",
      protocol: "aave-v3",
      chain: "base"
    }
  },
  {
    agent: "TheMEVProtectBot",
    offering: "mev_score",
    reason: "Estimate MEV/sandwich exposure before submitting an Ethereum transaction.",
    estimatedCostUsdc: 0.10,
    keywords: ["mev", "sandwich", "private mempool", "transaction", "swap", "flashbots"],
    knownHires: 0,
    requirementHint: {
      transaction: "0x... signed or planned transaction ...",
      chain: "ethereum"
    }
  },
  {
    agent: "TheEASIssuer",
    offering: "attest_result",
    reason: "Publish a low-cost attestation for a completed check or route decision.",
    estimatedCostUsdc: 0.05,
    keywords: ["attest", "attestation", "proof", "eas", "reputation", "verify"],
    knownHires: 1,
    requirementHint: {
      subject: "0x... address or result id ...",
      result: "summary to attest"
    }
  }
];

export const routeStack: Offering = {
  name: "route_stack",
  description:
    "Recommend the best ACP bot/offering stack for a buyer goal, with cost estimates, hire order, risk notes, and ready-to-adapt requirement hints.",
  slaMinutes: 5,
  requirementSchema: {
    type: "object",
    properties: {
      goal: {
        type: "string",
        description: "Plain-English buyer goal describing what the buyer wants to achieve across ACP agents.",
        maxLength: MAX_GOAL_LENGTH
      },
      budgetUsdc: {
        type: "number",
        description: "Optional maximum downstream budget in USDC for the recommended stack. The route excludes optional recommendations that exceed this budget.",
        minimum: 0.01
      },
      riskTolerance: {
        type: "string",
        description: "Optional risk tolerance for the route. Low favours safer preflight/check-first stacks; high allows leaner stacks.",
        enum: [...RISK_TOLERANCES]
      },
      chains: {
        type: "array",
        description: "Optional chains the buyer cares about. Used to shape requirement hints and warnings.",
        items: {
          type: "string",
          description: "A chain name such as base, ethereum, or solana."
        }
      }
    },
    required: ["goal"]
  },
  requirementExample: {
    goal: "Monitor this Base wallet for risky approvals and oracle risk before I deposit more funds",
    budgetUsdc: 1.0,
    riskTolerance: "low",
    chains: ["base", "ethereum"]
  },
  deliverableSchema: {
    type: "object",
    properties: {
      goal: { type: "string", description: "The buyer goal that was routed." },
      riskTolerance: { type: "string", description: "The effective risk tolerance used for routing." },
      chains: {
        type: "array",
        description: "Chains considered by the route.",
        items: { type: "string", description: "A chain considered by the route." }
      },
      recommendedStack: {
        type: "array",
        description: "Ordered list of recommended ACP agent hires.",
        items: {
          type: "object",
          description: "A single recommended ACP agent/offering hire.",
          properties: {
            agent: { type: "string", description: "Recommended ACP seller agent name." },
            offering: { type: "string", description: "Recommended offering name to hire on that agent." },
            reason: { type: "string", description: "Why this hire belongs in the route." },
            estimatedCostUsdc: { type: "number", description: "Estimated cost in USDC for this downstream hire." },
            requirementHint: {
              type: "object",
              description: "Draft requirement fields the buyer can adapt for this downstream hire.",
              additionalProperties: { description: "A draft requirement value for the downstream offering." }
            },
            traction: {
              type: "string",
              description: "Whether this offering has observable hire history on the Virtuals marketplace.",
              enum: ["proven", "unproven", "unknown"]
            }
          },
          required: ["agent", "offering", "reason", "estimatedCostUsdc", "requirementHint", "traction"]
        }
      },
      totalEstimatedCostUsdc: { type: "number", description: "Estimated total downstream spend for the recommended stack, excluding this ConciergeBot route fee." },
      hireOrder: {
        type: "array",
        description: "Offering names in the recommended order to hire them.",
        items: { type: "string", description: "Offering name to hire in order." }
      },
      risks: {
        type: "array",
        description: "Route caveats, budget warnings, or unsupported-scope notes.",
        items: { type: "string", description: "A route caveat or risk note." }
      },
      nextStep: { type: "string", description: "The most useful next action for the buyer." }
    },
    required: ["goal", "riskTolerance", "chains", "recommendedStack", "totalEstimatedCostUsdc", "hireOrder", "risks", "nextStep"]
  },
  deliverableExample: {
    goal: "Monitor this Base wallet for risky approvals and oracle risk before I deposit more funds",
    riskTolerance: "low",
    chains: ["base", "ethereum"],
    recommendedStack: [
      {
        agent: "TheRevokeBot",
        offering: "wallet_scan",
        reason: "Find risky token approvals and spender exposure before the buyer moves more funds.",
        estimatedCostUsdc: 0.20,
        requirementHint: { walletAddress: "0x... buyer wallet to scan ...", chains: ["base", "ethereum"] },
        traction: "proven"
      },
      {
        agent: "TheOracleBot",
        offering: "oracle_check",
        reason: "Compare price sources and flag oracle or peg/depeg risk before acting.",
        estimatedCostUsdc: 0.05,
        requirementHint: { asset: "TOKEN/USDC or pool address", chain: "base" },
        traction: "proven"
      }
    ],
    totalEstimatedCostUsdc: 0.25,
    hireOrder: ["wallet_scan", "oracle_check"],
    risks: [],
    nextStep: "Hire TheRevokeBot wallet_scan first, then use its result to decide whether the oracle check is still needed."
  },
  validate(req) {
    const goal = requireStringLength(req.goal, "goal", MAX_GOAL_LENGTH);
    if (!goal.valid) return goal;

    if (req.budgetUsdc !== undefined) {
      if (typeof req.budgetUsdc !== "number" || !Number.isFinite(req.budgetUsdc) || req.budgetUsdc < 0.01) {
        return { valid: false, reason: "budgetUsdc must be a number >= 0.01" };
      }
    }

    const risk = requireOneOf(req.riskTolerance, "riskTolerance", RISK_TOLERANCES);
    if (!risk.valid) return risk;

    if (req.chains !== undefined) {
      if (!Array.isArray(req.chains)) return { valid: false, reason: "chains must be an array" };
      if (req.chains.some(c => typeof c !== "string" || c.trim() === "")) {
        return { valid: false, reason: "chains must contain non-empty strings" };
      }
    }

    return { valid: true };
  },
  async execute(req) {
    return routeStackForRequirement(req);
  }
};

function routeStackForRequirement(req: Record<string, unknown>) {
  const goal = String(req.goal).trim();
  const riskTolerance = parseRiskTolerance(req.riskTolerance);
  const chains = parseChains(req.chains);
  const budgetUsdc = typeof req.budgetUsdc === "number" && Number.isFinite(req.budgetUsdc)
    ? req.budgetUsdc
    : undefined;

  const goalText = goal.toLowerCase();
  const matched = CANDIDATES.filter(candidate =>
    candidate.keywords.some(keyword => goalText.includes(keyword))
  );

  const base = matched.length > 0
    ? matched
    : CANDIDATES.filter(candidate => candidate.defaultPick);

  const stack = applyBudget(base, budgetUsdc, riskTolerance);
  const total = roundUsdc(stack.reduce((sum, item) => sum + item.estimatedCostUsdc, 0));
  const hasUnproven = stack.some(c => c.knownHires === 0);
  const risks = buildRisks({ budgetUsdc, total, chains, matchedCount: matched.length, riskTolerance, hasUnproven });

  return {
    goal,
    riskTolerance,
    chains,
    recommendedStack: stack.map(stripCandidateFields),
    totalEstimatedCostUsdc: total,
    hireOrder: stack.map(item => item.offering),
    risks,
    nextStep: buildNextStep(stack)
  };
}

function parseRiskTolerance(value: unknown): RiskTolerance {
  return RISK_TOLERANCES.includes(value as RiskTolerance) ? value as RiskTolerance : "medium";
}

function parseChains(value: unknown): string[] {
  if (!Array.isArray(value)) return ["base"];
  const chains = value.map(v => String(v).trim().toLowerCase()).filter(Boolean);
  return chains.length > 0 ? Array.from(new Set(chains)) : ["base"];
}

function applyBudget(candidates: Candidate[], budgetUsdc: number | undefined, riskTolerance: RiskTolerance): Candidate[] {
  const proven = candidates.filter(c => c.knownHires > 0);
  const unproven = candidates.filter(c => c.knownHires === 0);
  const sorted = [...proven, ...unproven];

  const ordered = riskTolerance === "low"
    ? sorted
    : riskTolerance === "high"
      ? sorted.slice(0, Math.max(1, Math.min(2, sorted.length)))
      : sorted.slice(0, Math.max(1, Math.min(3, sorted.length)));

  if (budgetUsdc === undefined) return ordered;

  const picked: Candidate[] = [];
  let total = 0;
  for (const candidate of ordered) {
    if (roundUsdc(total + candidate.estimatedCostUsdc) <= budgetUsdc) {
      picked.push(candidate);
      total = roundUsdc(total + candidate.estimatedCostUsdc);
    }
  }

  return picked.length > 0 ? picked : [ordered[0]];
}

function buildRisks(input: {
  budgetUsdc: number | undefined;
  total: number;
  chains: string[];
  matchedCount: number;
  riskTolerance: RiskTolerance;
  hasUnproven: boolean;
}): string[] {
  const risks: string[] = [];
  if (input.matchedCount === 0) {
    risks.push("No exact portfolio keyword match found; returned the default wallet-risk preflight stack.");
  }
  if (input.budgetUsdc !== undefined && input.total > input.budgetUsdc) {
    risks.push("Budget is below the cheapest useful route; returned the minimum viable first hire anyway.");
  }
  if (input.chains.includes("solana")) {
    risks.push("Solana routing is currently limited to portfolio bots with explicit Solana support; verify downstream requirement shapes before hiring.");
  }
  if (input.riskTolerance === "high") {
    risks.push("High risk tolerance selected; route may omit conservative preflight checks to save cost.");
  }
  if (input.hasUnproven) {
    risks.push("One or more recommended offerings have no observable hire history on the Virtuals marketplace — their reliability is unproven.");
  }
  return risks;
}

function buildNextStep(stack: Candidate[]): string {
  if (stack.length === 0) return "No route found; refine the goal with the chain, wallet, asset, or protocol involved.";
  const first = stack[0];
  return `Hire ${first.agent} ${first.offering} first, then use its result to decide whether to continue the route.`;
}

function getTraction(candidate: Candidate): Traction {
  if (candidate.knownHires > 0) return "proven";
  if (candidate.knownHires === 0) return "unproven";
  return "unknown";
}

function stripCandidateFields(candidate: Candidate): StackRecommendation {
  return {
    agent: candidate.agent,
    offering: candidate.offering,
    reason: candidate.reason,
    estimatedCostUsdc: candidate.estimatedCostUsdc,
    requirementHint: candidate.requirementHint,
    traction: getTraction(candidate)
  };
}

function roundUsdc(value: number): number {
  return Math.round(value * 100) / 100;
}
