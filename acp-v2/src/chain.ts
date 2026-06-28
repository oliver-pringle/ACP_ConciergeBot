import { base, baseSepolia, arbitrum, arbitrumSepolia, type Chain } from "viem/chains";
import type { ChainName } from "./env.js";

// Name -> viem Chain. Typing this Record<ChainName, Chain> makes it exhaustive:
// adding a ChainName the map does not cover becomes a compile error. viem
// supplies the numeric ids (base.id=8453, baseSepolia.id=84532,
// arbitrum.id=42161, arbitrumSepolia.id=421614), so no chain id is hardcoded.
const CHAINS: Record<ChainName, Chain> = {
  base,
  baseSepolia,
  arbitrum,
  arbitrumSepolia,
};

export function getChain(name: ChainName): Chain {
  return CHAINS[name];
}
