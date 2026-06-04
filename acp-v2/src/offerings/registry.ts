import type { Offering } from "./types.js";
import { echo } from "./echo.js";
import { routeStack } from "./route_stack.js";
import { tickEcho } from "./tick_echo.js";
import { tickStreamEcho } from "./tick_stream_echo.js";
import { portfolioRun } from "./portfolio_run.js";

export const OFFERINGS: Record<string, Offering> = {
  echo,
  route_stack: routeStack,
  tick_echo: tickEcho,
  tick_stream_echo: tickStreamEcho,
  portfolio_run: portfolioRun
};

export function getOffering(name: string): Offering | undefined {
  return OFFERINGS[name];
}

export function listOfferings(): string[] {
  return Object.keys(OFFERINGS);
}
