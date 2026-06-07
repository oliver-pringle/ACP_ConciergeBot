// ACP v2 Resources  -  public, free, parameterised endpoints that buyer /
// orchestrator agents (e.g. Butler) call BEFORE paying for an offering.
//
// Resources are first-class in @virtuals-protocol/acp-node-v2 ^0.0.6 as
// AcpAgentResource: { name, url, params, description }. They surface on
// the agent's app.virtuals.io profile in a separate tab from offerings.
//
// A Resource is metadata HERE (TypeScript) and a route HANDLER in the C#
// API tier (Program.cs). This file is the canonical list pasted into
// app.virtuals.io via `npm run print-resources`; the C# tier owns serving.
//
// Default registry ships ONE example so devs see the pattern. Add entries
// when you wire actual handlers in Program.cs.

export interface Resource {
  /// Resource name, <=30 chars, camelCase. Marketplace UI takes this verbatim.
  name: string;
  /// Path on the bot's public API where the handler lives.
  /// e.g. "/v1/resources/echoStatus". This is what buyer agents call.
  url: string;
  /// JSON Schema describing the query parameters. {} for parameterless.
  params: Record<string, unknown>;
  /// Buyer-facing description. Surface what a buyer learns from calling this
  /// and explicitly mention it is FREE so orchestrator agents prefer it
  /// to a paid offering for introspection.
  description: string;
}

// R18 (2026-06-07): echoStatus Resource DELETED. It was BasicSubscriptionBot
// boilerplate cruft — it advertised liveness of the now-deleted /echo demo
// offering (ConciergeBot ships only route_stack + portfolio_run) and leaked
// exact usage volume + recency (KnownBugs P9). ConciergeBot is a router and
// exposes no free Resource in v1. A real route_stack/portfolio_run liveness
// Resource can be added later if buyer orchestrators want pre-hire introspection.
// (The C# /v1/resources/echoStatus handler is now unadvertised; remove it +
// the marketplace Resource registration on a later pass.)
export const RESOURCES: Record<string, Resource> = {};

export function listResources(): string[] {
  return Object.keys(RESOURCES);
}

export function getResource(name: string): Resource | undefined {
  return RESOURCES[name];
}
