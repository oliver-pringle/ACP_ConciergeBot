// Supported ACP settlement chains. Base is primary; Arbitrum (One + Sepolia)
// added 2026-06-28 for the Virtuals ACP Arbitrum integration. NOTE: actually
// settling on an Arbitrum chain ALSO needs an acp-node-v2 build that registers
// 42161 / 421614 - the pinned 0.0.6 only knows Base + bscTestnet, so
// AssetToken.usdc(_, 42161) throws until that SDK is bumped. Selecting an
// Arbitrum value here compiles and runs today; it only transacts once the SDK
// lands. See ACP_Arbitrum_implmentation.txt (sections 2 + 6).
export const CHAIN_NAMES = [
  "base",
  "baseSepolia",
  "arbitrum",
  "arbitrumSepolia",
] as const;
export type ChainName = (typeof CHAIN_NAMES)[number];

export interface AcpEnv {
  walletAddress: string;
  walletId: string;
  signerPrivateKey: string;
  chain: ChainName;
  apiUrl: string;
  apiKey?: string;
  builderCode?: string;
  baseRpcUrl: string;
  deployerPrivateKey?: string;
  streamPushPort: number;
  /// Network interface streamPush binds to. Defaults to 127.0.0.1
  /// (loopback-only). Docker multi-container deploys set this to "0.0.0.0"
  /// via the CONCIERGEBOT_STREAM_PUSH_BIND_HOST env var; the
  /// docker-internal bridge is then the trust boundary. Never publish the
  /// stream-push port to the host (no docker-compose `ports:` entry).
  streamPushBindHost: string;
}

const REQUIRED = [
  "ACP_WALLET_ADDRESS",
  "ACP_WALLET_ID",
  "ACP_SIGNER_PRIVATE_KEY",
  "ACP_CHAIN",
  "CONCIERGEBOT_API_URL",
] as const;

// Default public RPCs per chain. Used by walletDelegation's eth_getBytecode
// probe. Override with BASE_RPC_URL if you have a private/paid RPC; the
// probe is one call per boot so even free RPCs are fine.
const DEFAULT_RPC: Record<ChainName, string> = {
  base: "https://base-rpc.publicnode.com",
  baseSepolia: "https://base-sepolia-rpc.publicnode.com",
  arbitrum: "https://arb1.arbitrum.io/rpc",
  arbitrumSepolia: "https://sepolia-rollup.arbitrum.io/rpc",
};

// inJobStream PushMode internal HTTP listener default port. Matches the C#
// tier's InJobStreamDeliveryService default BaseUrl (http://localhost:6001).
// Override with CONCIERGEBOT_STREAM_PUSH_PORT for non-default deploys
//  -  must also update Services:StreamPush:BaseUrl on the C# side in lockstep.
const DEFAULT_STREAM_PUSH_PORT = 6001;

export function loadEnv(source: NodeJS.ProcessEnv = process.env): AcpEnv {
  for (const name of REQUIRED) {
    const value = source[name];
    if (!value || value.trim() === "") {
      throw new Error(`Missing required env var: ${name}`);
    }
  }

  const chainRaw = source.ACP_CHAIN;
  if (!CHAIN_NAMES.includes(chainRaw as ChainName)) {
    throw new Error(
      `ACP_CHAIN must be one of ${CHAIN_NAMES.join(", ")}, got "${chainRaw}"`
    );
  }
  const chain: ChainName = chainRaw as ChainName;

  const builderCodeRaw = source.ACP_BUILDER_CODE;
  const builderCode =
    builderCodeRaw && builderCodeRaw.trim() !== "" ? builderCodeRaw : undefined;

  const apiKeyRaw = source.CONCIERGEBOT_API_KEY;
  const apiKey = apiKeyRaw && apiKeyRaw.trim() !== "" ? apiKeyRaw : undefined;

  // RPC for the selected chain. Arbitrum chains prefer ARBITRUM_RPC_URL; Base
  // chains prefer BASE_RPC_URL; both fall back to a public default above. Kept
  // under the field name baseRpcUrl for back-compat (it is simply "the EVM RPC
  // for env.chain", consumed by the walletDelegation bytecode probe).
  const isArbitrum = chain === "arbitrum" || chain === "arbitrumSepolia";
  const rpcRaw = isArbitrum ? source.ARBITRUM_RPC_URL : source.BASE_RPC_URL;
  const baseRpcUrl = rpcRaw && rpcRaw.trim() !== "" ? rpcRaw : DEFAULT_RPC[chain];

  // Optional sponsor key for EIP-7702 auto-recovery on boot. When set, the
  // walletDelegation guard re-delegates the ACP wallet to Alchemy
  // ModularAccountV2 via a sponsored type-4 tx if Privy WaaS has drifted to
  // a different impl. Without it, the guard throws on drift with a clear
  // recovery message. See acp-v2/src/walletDelegation.ts and
  // memory/reference_acp_wallet_provisioning.md.
  const deployerRaw = source.DEPLOYER_PRIVATE_KEY;
  const deployerPrivateKey =
    deployerRaw && deployerRaw.trim() !== "" ? deployerRaw : undefined;

  const streamPortRaw = source.CONCIERGEBOT_STREAM_PUSH_PORT;
  let streamPushPort = DEFAULT_STREAM_PUSH_PORT;
  if (streamPortRaw && streamPortRaw.trim() !== "") {
    const parsed = Number.parseInt(streamPortRaw, 10);
    if (!Number.isFinite(parsed) || parsed < 1 || parsed > 65535)
      throw new Error(`CONCIERGEBOT_STREAM_PUSH_PORT must be 1..65535, got "${streamPortRaw}"`);
    streamPushPort = parsed;
  }

  const bindRaw = source.CONCIERGEBOT_STREAM_PUSH_BIND_HOST;
  const streamPushBindHost =
    bindRaw && bindRaw.trim() !== "" ? bindRaw.trim() : "127.0.0.1";

  return {
    walletAddress: source.ACP_WALLET_ADDRESS!,
    walletId: source.ACP_WALLET_ID!,
    signerPrivateKey: source.ACP_SIGNER_PRIVATE_KEY!,
    chain,
    apiUrl: source.CONCIERGEBOT_API_URL!,
    apiKey,
    builderCode,
    baseRpcUrl,
    deployerPrivateKey,
    streamPushPort,
    streamPushBindHost,
  };
}
