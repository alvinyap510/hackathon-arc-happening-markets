# Request for Market (RFM)

Prediction market infrastructure for institutional hedging, built on Arc.

## What we are building

The infrastructure that bridges institutional hedging risk into prediction markets, through the Request for Market (RFM) mechanism.

1. **The bridge between institutional finance and prediction markets.** Institutions already hedge event risk through options desks. Prediction markets price the same events better, but there is no infrastructure connecting the two. We are building it.

2. **A novel primitive: Request for Market (RFM).** RFQ is how institutions trade options; RFM is the same workflow, applied to prediction markets. An institution requests a hedge on an outcome. Market makers answer with sealed quotes. The best quotes win, collateral locks, and the market is born already funded and priced.

3. **Speculative liquidity makes institutional hedging cheap.** Once a market goes live, public traders take the other side. Consumer speculation is not a bug: it is what compresses the spread and makes hedging on chain cheaper than an options desk.

## How Request for Market works

1. **Request.** An institution posts a hedge: market, quantity, minimum match, and the worst price it will accept. Collateral and a bond are escrowed on chain.
2. **Sealed quotes.** Market makers answer with commitments (a hash of their price and size) plus their own bond. Nobody can see a quote before it is revealed, so there is no front-running and no quote-shading.
3. **Reveal.** Makers open their commitments. A maker who does not reveal forfeits its bond. A maker who reveals out of range forfeits its bond. Honesty is the cheapest strategy.
4. **Match at the deadline.** When the reveal window closes, the cheapest quotes win, in price order. If the fill clears the institution's minimum, collateral locks, positions mint, and the market is born.
5. **Live market.** The new market opens as a public order book. The institution already has its hedge at a committed price. Everyone else provides the exit liquidity.

## Repository

| Path | What lives here |
|---|---|
| `contracts/` | Four Solidity contracts (Foundry): Vault, CTFExchangeLite, OutcomeTokens, RFM |
| `backend/` | Venue engine (C# / .NET 9): indexer, order book, settlement, RFM coordinator, API |
| `frontend/` | Trading UI (TypeScript): order book, buy/sell panel, positions, RFM and bridge flows |
| `agents/` | Autonomous market-maker agent (ERC-8004 identity, ERC-8183 jobs) |
| `docs/` | Deck, demo script, diagrams |

## Built on Arc

Arc is Circle's stablecoin-native chain, where USDC is the gas token. We use:

- **Circle Wallets** for accounts, so users sign in with an email and never touch a seed phrase
- **Gas Station** so every trade is gasless
- **CCTP** to bridge USDC in from another chain before a request is posted
- **Agent Stack (ERC-8004 / ERC-8183)** so the market-maker agent has a real on-chain identity and job record

Trading and collateral are denominated in USDC end to end.

## Deployed on Arc testnet

Chain ID `5042002` · RPC `https://rpc.testnet.arc.io` · explorer [`testnet.arcscan.app`](https://testnet.arcscan.app). Current testnet deployment (addresses are updated on each redeploy):

| Contract | Address |
|---|---|
| MockUSDC (collateral, 6-dec) | `0xE0685ecACd0CB011377Ecb65001995eEA10b9308` |
| OutcomeTokens | `0xE4E3EaBA1C944B8B17be1DE9f4a6BAD211Dd195C` |
| Vault | `0xAcfa83e4A9A147DfA5b6F4Bf8478192D4a82Eeed` |
| CTFExchangeLite | `0x353103Bda8f72411C91DAeb2962b2ffE31299b06` |
| RFM | `0x5FFf6dC4Dc4B0e164ad86144cA3E25C0E6867d72` |

Roles are wired once at deploy and frozen (Vault, OutcomeTokens, and RFM reference each other; the operator settles and resolves). Collateral is the MockUSDC above; production points the same `USDC_ADDRESS` parameter at the Arc system USDC face `0x3600...0000` with no contract change.

## Status

Early build for the Arc Programmable Money hackathon. Architecture is specified and audited; implementation is in progress. This is a scoped, from-scratch build, not a fork of any production system.

## Known Limitations

- **We use a separate mock ERC-20 as USDC.** On Arc testnet the native gas is tied to the system USDC (the same asset is both the gas token and the ERC-20 at `0x3600...0000`), issued by Circle and not mintable to our needs. Since we cannot mint it to fund positions at meaningful size, the venue trades a self-deployed mock ERC-20 USDC (6-dec) as its collateral token. Gas is still paid in real Arc USDC. The contracts take a USDC-address parameter, so pointing collateral at the real `0x3600` USDC is a one-line change.
