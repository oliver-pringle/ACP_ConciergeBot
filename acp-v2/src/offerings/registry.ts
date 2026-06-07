import type { Offering } from "./types.js";
import { routeStack } from "./route_stack.js";
import { portfolioRun } from "./portfolio_run.js";

export const OFFERINGS: Record<string, Offering> = {
  route_stack: routeStack,
  portfolio_run: portfolioRun
};

export function getOffering(name: string): Offering | undefined {
  return OFFERINGS[name];
}

export function listOfferings(): string[] {
  return Object.keys(OFFERINGS);
}
