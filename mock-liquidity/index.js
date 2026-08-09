// mock-liquidity — external resting-liquidity agent.
//
// Polls the venue REST API for born RFM markets and places ONE non-crossing
// bid+ask ladder per market (one bid + one ask). The venue signs on the agent's
// behalf via the bound Bearer session; the agent holds NO private keys.
//
// No-cross (PLAN §6.2): the API has no post-only flag, so a hard "never cross"
// guarantee is impossible — a concurrent order can arrive between the GET and
// the POST. This agent enforces what the API can: never self-cross, never
// intentionally cross the OBSERVED book, and treat "zero fills on placement" as
// a checked postcondition. The no-trade acceptance gate is run on a fresh
// uncontested (just-born) market where no third-party orders exist.
//
// Idempotent: a (marketId, rung) journal on a mounted volume; on restart the
// agent reads its open orders and places only what is missing.
//
// See PLAN_MOCK_AGENTS.md §6.

import { readFile, writeFile, rename, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';

const API = process.env.VENUE_API || 'http://backend:8080';
const ADDRESSES = (process.env.AGENT_ADDRESSES || '').split(',').map(s => s.trim().filter(Boolean)).filter(Boolean);
const POLL_MS = parseInt(process.env.POLL_MS || '2000', 10);
const JOURNAL_DIR = process.env.JOURNAL_DIR || '/data';
const JOURNAL_PATH = `${JOURNAL_DIR}/liquidity-journal.json`;
const RUNG_SIZE = 200_000_000n;          // 200 outcome-shares (6-dec); LegCost USDC = floor(size*price/1000)
const SPREAD = 10;                       // ±10 ticks around the born marginal
const ENABLED = (process.env.ENABLED || '').toLowerCase() === 'true';

if (!ENABLED) { console.error('[mock-liquidity] ENABLED is not true; refusing to start (fail-closed).'); process.exit(1); }
if (ADDRESSES.length < 1) { console.error('[mock-liquidity] AGENT_ADDRESSES must list >=1 identity'); process.exit(1); }
const AGENT = ADDRESSES[0].toLowerCase();
let token = null;

// --------------------------------------------------------------- journal
let journal = [];     // { marketId, bid: {placed, orderId}, ask: {placed, orderId} }
async function loadJournal() { try { journal = JSON.parse(await readFile(JOURNAL_PATH, 'utf8')); } catch { journal = []; } }
async function saveJournal() {
  if (!existsSync(JOURNAL_DIR)) await mkdir(JOURNAL_DIR, { recursive: true });
  const tmp = `${JOURNAL_PATH}.${process.pid}.tmp`;
  await writeFile(tmp, JSON.stringify(journal, null, 2));
  await rename(tmp, JOURNAL_PATH);
}
function rowFor(marketId) { let r = journal.find(x => x.marketId === marketId); if (!r) { r = { marketId, bid: { placed: false, orderId: null }, ask: { placed: false, orderId: null } }; journal.push(r); } return r; }

// --------------------------------------------------------------- http
async function api(method, path, tkn, body) {
  const headers = { 'Content-Type': 'application/json' };
  if (tkn) headers['Authorization'] = `Bearer ${tkn}`;
  const res = await fetch(`${API}${path}`, { method, headers, body: body ? JSON.stringify(body) : undefined });
  if (res.status === 401 && tkn) return { status: 401 };
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch {}
  return { status: res.status, json };
}
async function rebind() {
  const r = await api('POST', '/v1/session/bind', null, { Address: AGENT });
  if (r.status === 200 && r.json?.token) { token = r.json.token; return true; }
  return false;
}
async function call(method, path, body) {
  if (!token) { if (!(await rebind())) return { status: 0 }; }
  let r = await api(method, path, token, body);
  if (r.status === 401) { if (await rebind()) r = await api(method, path, token, body); }
  return r;
}

async function available() { const r = await call('GET', '/v1/balances'); return r.status === 200 && r.json ? BigInt(r.json.available || '0') : null; }
async function bookOf(marketId) { const r = await api('GET', `/v1/book/${marketId}`, null, null); return r.status === 200 ? r.json : null; }

// --------------------------------------------------------------- ladder (PLAN §6.3)
// One bid + one ask anchored on the born marginal (mid). yes_bid + no_price = 980 < 1000 (no self-cross).
//   BUY YES @ mid-10  -> bid at mid-10
//   BUY NO  @ 1000-mid-10 -> ask at mid+10
function rungs(mid) {
  const bidPrice = mid - SPREAD;          // BUY YES price (YES tick)
  const noPrice = 1000 - mid - SPREAD;    // BUY NO price (NO tick); stored as ask @ mid+SPREAD
  if (bidPrice < 1 || noPrice < 1) return null;        // mid-10 < 1 or 1000-mid-10 < 1
  if (bidPrice > 999 || noPrice > 999) return null;
  return {
    bid: { outcome: 'yes', side: 'buy', price: bidPrice },
    ask: { outcome: 'no', side: 'buy', price: noPrice },
  };
}

// LegCost USDC reserved by a BUY (OrderModels.cs:93): floor(size*price/1000).
function legCost(price) { return (RUNG_SIZE * BigInt(price)) / 1000n; }

// Check a rung would NOT cross the observed YES book. Bid (BUY YES @ p) crosses a resting ask <= p.
// Ask-from-BUY-NO (BUY NO @ u, ask @ 1000-u) crosses a resting bid >= 1000-u.
function wouldCross(book, rung, mid) {
  if (!book || !book.yes) return false;
  if (rung.outcome === 'yes') {                       // bid @ bidPrice
    const askMin = Math.min(...(book.yes.asks || []).map(l => l.price), Number.POSITIVE_INFINITY);
    return askMin <= rung.price;
  }
  // ask @ mid+SPREAD from BUY NO; crosses a resting YES bid >= mid+SPREAD
  const askYesPrice = mid + SPREAD;
  const bidMax = Math.max(...(book.yes.bids || []).map(l => l.price), Number.NEGATIVE_INFINITY);
  return bidMax >= askYesPrice;
}

async function placeRung(marketId, rung, mid, label) {
  // preflight: balance covers LegCost
  const avail = await available();
  if (avail == null) { console.log(`[mock-liquidity] ${marketId} ${label}: balance read failed`); return false; }
  if (avail < legCost(rung.price)) { console.log(`[mock-liquidity] ${marketId} ${label}: insufficient available ${avail} < ${legCost(rung.price)}`); return false; }
  // preflight: not crossing observed book
  const book = await bookOf(marketId);
  if (wouldCross(book, rung, mid)) { console.log(`[mock-liquidity] ${marketId} ${label}: would cross observed book, skip`); return false; }
  // submit (idempotent clientOrderId)
  const clientOrderId = `liq-${marketId.slice(0, 10)}-${label}`;
  const r = await call('POST', '/v1/orders', { MarketId: marketId, Outcome: rung.outcome, Side: rung.side, Size: RUNG_SIZE.toString(), Price: rung.price, Type: 'limit', ClientOrderId: clientOrderId });
  if (r.status !== 200 || !r.json) { console.log(`[mock-liquidity] ${marketId} ${label}: POST status=${r.status}`); return false; }
  // CHECKED POSTCONDITION: zero fills, resting, full remaining
  const fills = r.json.fills || [];
  const status = r.json.status;
  const remaining = BigInt(r.json.remaining || '0');
  if (fills.length > 0 || status !== 'resting' || remaining !== RUNG_SIZE) {
    console.log(`[mock-liquidity] ${marketId} ${label}: PLACEMENT CROSSED (fills=${fills.length} status=${status} remaining=${remaining}) — stopping rungs for this market`);
    return 'crossed';
  }
  console.log(`[mock-liquidity] ${marketId} ${label}: placed ${rung.outcome}@${rung.price} orderId=${r.json.orderId}`);
  return r.json.orderId;
}

async function handleMarket(market) {
  if (!market.bornFromRfm || !market.exists || market.closing || market.resolved) return;
  if (!market.born || market.born.marginalYesTick == null) return;
  const mid = Number(market.born.marginalYesTick);
  const rungs = rungs(mid);
  if (!rungs) { console.log(`[mock-liquidity] ${market.marketId}: mid=${mid} out of ladder range, skip`); return; }
  const row = rowFor(market.marketId);
  if (!row.bid.placed) {
    const res = await placeRung(market.marketId, rungs.bid, mid, 'bid');
    if (res === 'crossed') { row.bid.placed = true; row.ask.placed = true; await saveJournal(); return; }
    if (res) { row.bid.placed = true; row.bid.orderId = res; await saveJournal(); }
  }
  if (!row.ask.placed) {
    const res = await placeRung(market.marketId, rungs.ask, mid, 'ask');
    if (res === 'crossed') { row.ask.placed = true; await saveJournal(); return; }
    if (res) { row.ask.placed = true; row.ask.orderId = res; await saveJournal(); }
  }
}

// --------------------------------------------------------------- loop
let backendDown = false;
async function tick() {
  try {
    const r = await api('GET', '/v1/markets', null, null);
    if (r.status !== 200 || !Array.isArray(r.json)) { if (!backendDown) { backendDown = true; console.log(`[mock-liquidity] GET /v1/markets -> ${r.status}`); } return; }
    if (backendDown) { backendDown = false; console.log('[mock-liquidity] backend reachable again'); }
    for (const m of r.json) await handleMarket(m);
  } catch (e) { console.error('[mock-liquidity] loop error', e.message); }
}

async function main() {
  await loadJournal();
  console.log(`[mock-liquidity] start; api=${API} agent=${AGENT} poll=${POLL_MS}ms`);
  if (!(await rebind())) console.error(`[mock-liquidity] bind failed for ${AGENT}`);
  for (;;) { await tick(); await sleep(POLL_MS); }
}
function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }
main().catch(e => { console.error('[mock-liquidity] fatal', e); process.exit(1); });