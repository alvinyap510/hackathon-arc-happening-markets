# backend/ — venue engine (C# / .NET 9)

One process, five modules: STATE (indexer + ledger) · ENGINE (price-time book, 4 directions) ·
SETTLEMENT (batcher) · RFM COORDINATOR (auction cranker) · API (REST + WS).

Chain via Nethereum, HTTP/WS via ASP.NET, Circle products via REST. Indexes contract events only.

Status: complete and deployed — this is the engine behind the live demo at
https://arc-hackathon.happening.markets (Arc testnet 5042002, VPS behind Caddy; see
`../docs/VPS_DEPLOYMENT.md`). Covered by the xUnit suite in `tests/`; run with
`dotnet test` (.NET 9 SDK, or the dockerized run in the repo README).

Module map:

| Module | Where | What |
|---|---|---|
| STATE | `src/Venue.Core` (indexer, ledger) | replays contract events from `StartBlock`, mirrors balances/positions |
| ENGINE | `src/Venue.Core/Engine` | ONE consolidated YES-basis book per market, price-time priority, 4 intake directions folded |
| SETTLEMENT | `src/Venue.Core/Settlement` | batches matched trades, submits on-chain, confirmed-only tx provenance |
| RFM COORDINATOR | `src/Venue.Core/Rfm` | mirrors auction events, cranks finalize, serves commit/reveal |
| API | `src/Venue.Api` | REST + WS, session auth, CORS-pinned origins |
