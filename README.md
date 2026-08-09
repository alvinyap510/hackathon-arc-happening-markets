# Happening Markets — Arc Programmable Money Hackathon Submission 

We built a full-stack prediction market platform around a new primitive: **Request for Market (RFM)** - turning institutional hedge demand into funded, tradable markets from day one.

Built from the ground up on Arc.

## The Problem

1. Institutions have real event risk they want to hedge. Today they use RFQ terminals, through swaps and options priced privately, desk to desk.

2. Event contracts are a powerful financial instrument and the primitive is proven. But they are fueled by speculative volume, not designed for institutions.

## Our Solution

A Request for Market (RFM) platform that matches institutions and market makers in the form of event contracts, through a commit-and-reveal mechanism. Once matched, the market goes live and secondary speculative volume can participate in trading it.

1. Demand and supply are proven, not invented.

2. Speculative volume is what makes hedging cheap and liquid.

## Deck

https://happening-markets-arc-hackathon.netlify.app

## Frontend

https://frontend-iota-murex-45.vercel.app

## Deployed Contracts

Arc testnet · chain ID `5042002` · explorer [`testnet.arcscan.app`](https://testnet.arcscan.app)

| Contract | Address | What it does |
|---|---|---|
| `Vault.sol` | [`0xAcfa83e4...4a82Eeed`](https://testnet.arcscan.app/address/0xAcfa83e4A9A147DfA5b6F4Bf8478192D4a82Eeed) | Custody and all physical asset movement. Holds user USDC and outcome tokens; the engine and RFM can only command it through narrow conservation-preserving primitives. Withdraw and redeem can never be frozen. |
| `OutcomeTokens.sol` | [`0xE4E3EaBA...11Dd195C`](https://testnet.arcscan.app/address/0xE4E3EaBA1C944B8B17be1DE9f4a6BAD211Dd195C) | Binary YES/NO conditional tokens plus the collateral pool. Split and merge are Vault-only, resolution is operator-only and one-shot, redemption is permissionless. |
| `CTFExchangeLite.sol` | [`0x353103Bd...31299b06`](https://testnet.arcscan.app/address/0x353103Bda8f72411C91DAeb2962b2ffE31299b06) | Settlement router over the Vault. Holds no funds; settles matched trades in whole-batch atomic operations where any invalid trade reverts the entire batch. |
| `RFM.sol` | [`0x5FFf6dC4...E6867d72`](https://testnet.arcscan.app/address/0x5FFf6dC4Dc4B0e164ad86144cA3E25C0E6867d72) | The Request for Market auction. Institutions post hedge requests, market makers commit sealed bonded quotes then reveal, and the best quotes are matched at the deadline so the market is born pre-funded and pre-priced. |
| `MockUSDC.sol` | [`0xE0685ecA...eA10b9308`](https://testnet.arcscan.app/token/0xE0685ecACd0CB011377Ecb65001995eEA10b9308) | Testnet collateral token (6-decimal ERC-20). Stands in for Arc system USDC, which is not mintable at test size. Gas is still paid in real Arc USDC. |

## What Have We Built

Visualization: https://happening-markets-arc-hackathon.netlify.app/7?clicks=3

**1. An RFM platform**

An institution posts the hedge it wants: the outcome, the size, and the worst price it will accept. Market makers answer with sealed quotes and a bond, so nobody can see a price before it is revealed. At the deadline the best quotes win, collateral locks on chain, and the market is born already funded and priced.

**2. A full-fledged order book (CLOB) prediction market**

Every born market opens as a real trading venue: limit orders, a matching engine, and on-chain settlement of every fill. Buying YES against buying NO mints new shares, a buyer against a seller transfers them, and two sellers merge them back into collateral. Markets resolve and winners redeem one to one.

## How Request for Market works

Visualization: https://happening-markets-arc-hackathon.netlify.app/6

1. **Request.** An institution posts the hedge it wants: the outcome, the size, the least it will accept, and the worst price. Its collateral and a bond are locked on chain.
2. **Sealed quotes.** Market makers reply with a sealed price and post their own bond. Nobody can see a quote before it opens, so no one can front-run or shade a price.
3. **Reveal.** Makers open their quotes. Anyone who stays silent, or opens a price outside what they promised, loses their bond. Honesty is the cheapest option.
4. **Match.** When the window closes, the cheapest quotes win in price order. If enough is filled, collateral locks, positions are created, and the market is born.
5. **Live market.** The market opens to the public. The institution already holds its hedge at an agreed price, and everyone else trades around it.

## Why Arc

1. We believe in Arc's vision of bringing institutional finance on chain, and we are building the same bridge one layer up: institutional hedging demand into prediction markets.

2. Arc's USDC-native gas and its surrounding stack, such as Gas Station, make it far easier for us to onboard both institutions and everyday users, with the seamless web2-like experience each of them expects.

## Repository Layout

| Path | What lives here |
|---|---|
| `contracts/` | Solidity contracts (Foundry): Vault, OutcomeTokens, CTFExchangeLite, RFM |
| `backend/` | Venue engine (C# / .NET 9): indexer, order book, settlement, RFM coordinator, API |
| `frontend/` | Trading UI (TypeScript): order book, buy/sell panel, positions, RFM flows |
| `e2e/` | End-to-end lifecycle proof driver that runs the full flow against real Arc |
| `docs/` | Deployment and operations notes |

## Known Limitations

- **We trade a mock USDC as collateral.** Arc's system USDC is issued by Circle and cannot be minted at test size, so the venue uses a self-deployed 6-decimal mock instead. Gas is still paid in real Arc USDC. The contracts take the collateral address as a parameter, so production points at the real USDC with no code change.

- **The market makers are ours.** There are no third-party desks on testnet, so we run the makers as our own EOAs quoting algorithmically. They go through the real RFM process end to end: sealed commit, bonded, revealed on chain, matched by the contract. The competition is simulated, the mechanism is real.
