# frontend/ — RFM trading UI (TypeScript + React + Vite + Tailwind)

The demo surface for the Request for Market venue: live order books, the sealed
commit-reveal auction visualizer with the born-market moment, and a funding faucet.

- **Markets** — list + full trading view: YES/NO book projections, buy/sell panel
  (BUY YES / BUY NO / SELL YES / SELL NO, limit + market), open orders, positions,
  redeem after resolution.
- **Request a Market** — post a hedge request (escrow + bond), then watch the sealed
  auction: commits arrive as bonded hashes, quotes reveal one by one, non-revealers
  slash, and the winning quotes birth a funded, priced market.
- **Faucet** — mint demo collateral (self-deployed 6-dec mock USDC) and deposit to
  the venue.

Identity is a Circle smart account behind an email login; every on-chain action is
submitted by the backend from the user's wallet, gas-sponsored. The frontend is a
REST + WebSocket client only: no browser wallet, no seed phrase, no chain SDK.

## Run

```bash
npm install
npm run dev        # mock mode by default: fully standalone, no backend needed
npm run build
```

## Mock vs real backend

One env flag (`frontend/.env.local`, see `.env.example`):

- `VITE_API_MODE=mock` (default) — an in-memory venue simulation drives everything:
  2 seeded markets quoted by a bot, a scripted sealed auction that plays
  COMMIT → REVEAL → FINALIZED → market born (the auction starts when you first open
  the RFM tab), fills, positions and balances. This is the standalone demo mode.
- `VITE_API_MODE=real` — talks to `submission/backend` over REST + WS. The wire
  surface is exactly the mock's: `src/lib/api.ts` (`VenueApi`) is the single seam;
  swap happens via dynamic import, no UI code changes.

## Layout

```
src/lib/       api seam, venue types, mock venue + auction script, store, formatting
src/components/ shell, book, order ticket, auction card, phase stepper, born flight
src/pages/     Markets, TradingView, RfmPage, FaucetPage
public/brand/  Happening marks
```

Conventions: money is 6-dec USDC base units (strings); prices are integer ticks
0–1000 on the canonical YES basis; the NO book is a complement projection of the
one YES book. Market orders are emulated client-side as an aggressive limit at the
far touch.
