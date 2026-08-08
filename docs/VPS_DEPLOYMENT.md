# VPS Deployment — Operations Manual (hackathon)

State + operational notes for the hackathon venue backend deployed on the Arc testnet VPS.
**No secrets here** — keys live only in VPS `/opt/venue-arc/backend.env` (root-readable, never committed).

## Current deployment

- **VPS:** `178.105.112.193` (Ubuntu, Docker, root SSH). Compose at `/opt/venue-arc/docker-compose.yml`.
- **Caddy:** `venue-arc.178.105.112.193.sslip.io` → `reverse_proxy backend:8080` (TLS :80/:443, proxies REST + WS `/ws`).
- **Backend container:** `venue-arc-backend-1` (image `venue-backend:arc`, .NET 9). `restart: unless-stopped`.
- **Backend config (`/opt/venue-arc/backend.env`):**
  - `Venue__WalletProvider=nethereum` (EOA demo keys via `Venue__DemoUsers__<addr>`). Circle config backed up at `/opt/venue-arc/backend.env.circle.bak`.
  - `Venue__Chain__RpcUrl` = **Alchemy** Arc testnet endpoint (free tier). The public `rpc.testnet.arc.io` rate-limits the VPS datacenter IP for sustained `eth_getLogs`; Alchemy does not IP-throttle but caps `eth_getLogs` at a **10-block range** on the free tier.
  - `Venue__Indexer__MaxBlockSpan=10` (fits Alchemy free tier's 10-block cap; the indexer still catches up across successive polls).
  - `Venue__Chain__StartBlock` = **fast-forwarded to the deploy-time head** (see caveat below).
  - `Venue__SeedMarketsEnabled=false` (the startup market-seeder is non-idempotent vs a truncated replay ledger; disabled for the fresh-seed deploy).
  - `Venue__Cors__AllowedOrigins=https://frontend-iota-murex-45.vercel.app` (double underscore; pinned to the stable Vercel production alias).
- **Frontend:** Vercel `frontend-iota-murex-45.vercel.app` (real mode, `VITE_API_URL=https://venue-arc.178.105.112.193.sslip.io`).

## ⚠️ Restart caveat (read before restarting the backend)

The indexer (`EventIndexer`) has **no persisted cursor** — it always replays from `StartBlock` on every container start. `StartBlock` was fast-forwarded to the head **at deploy time**, so:

- **While the container stays up:** the poll loop (every 2s, 10-block spans) keeps the ledger current — it runs + moves forward automatically. `restart: unless-stopped` keeps it up across process crashes + VPS reboots.
- **After a restart (crash/reboot) once the chain has advanced:** the replay restarts from the stale deploy-time `StartBlock` → a growing catch-up gap. On free-tier Alchemy (10-block spans) that catch-up is slow, and if it exceeds the .NET host startup timeout it can **crash-loop** (`TaskCanceledException` in `EventIndexer.ReplayAsync`).

### Recovery: re-fast-forward on restart

If the backend crash-loops or lags after a restart, re-fast-forward `StartBlock` to the current head, then restart:

```sh
ssh root@178.105.112.193 '
  HEAD=$(curl -s --max-time 12 -X POST "<ALCHEMY_URL>" -H "Content-Type: application/json" \
    -d "{\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[],\"id\":1}" | grep -oE "0x[0-9a-f]+")
  HEADDEC=$((16#${HEAD#0x}))
  sed -i "s|^Venue__Chain__StartBlock=.*|Venue__Chain__StartBlock=$HEADDEC|" /opt/venue-arc/backend.env
  cd /opt/venue-arc && docker compose restart backend
'
```

(Replace `<ALCHEMY_URL>` with the endpoint in `backend.env`; do not echo the key.)

This makes the replay ~0 blocks → instant start, no crash-loop. Trade-off: the ledger is fresh-seeded (historical markets before the new `StartBlock` are not in the in-memory ledger until the next full replay). For a demo this is fine; for full history use a paid RPC with `MaxBlockSpan=2000` (fast catch-up) or build the persisted-cursor + snapshot fix.

## Load caveat (free tier)

Light/demo traffic is fine. A heavy burst (e.g. an automated E2E driver hammering the backend with a full run's worth of deposits/orders/RFM) can spike Alchemy's free-tier compute-units/sec → HTTP 429 (Nethereum hides it under the generic `Error occurred when trying to send rpc requests` wrapper). The steady-state poll alone does not trigger this. For sustained heavy load, upgrade to Alchemy PAYG + set `MaxBlockSpan=2000`.

## Restoring production Circle-login mode

The deployed backend runs in `nethereum` mode (EOA demo keys) for the proof. To restore the production Circle Wallets path:

```sh
ssh root@178.105.112.193 '
  cp /opt/venue-arc/backend.env.circle.bak /opt/venue-arc/backend.env
  # re-add CORS + (optionally) MaxBlockSpan + fast-forward StartBlock as above
  cd /opt/venue-arc && docker compose restart backend
'
```

Then smoke Circle login + a gasless faucet/deposit through the Vercel frontend (the E2E driver does not prove the Circle path — it proves the venue engine in nethereum mode).