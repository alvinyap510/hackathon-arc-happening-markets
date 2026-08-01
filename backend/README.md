# backend/ — venue engine (C# / .NET 9)

One process, five modules: STATE (indexer + ledger) · ENGINE (price-time book, 4 directions) ·
SETTLEMENT (batcher) · RFM COORDINATOR (auction cranker) · API (REST + WS).

Chain via Nethereum, HTTP/WS via ASP.NET, Circle products via REST. Indexes contract events only.

Status: scaffolding. Spec: `../../PLAN_BACKEND.md` (FINAL, two-round audited).
