// mock-quoter — external RFM quoter agent.
//
// Polls the venue REST API for RFM requests and answers each with two sealed
// bonded quotes (one per configured identity). The venue signs on the agent's
// behalf via the bound Bearer session; the agent holds NO private keys.
//
// Determinism: (tick, size) is a pure function of the request params + the
// agent's index, so a restart between commit and reveal re-sends the identical
// values and the backend's deterministic salt makes the hash match.
//
// Transaction authority: a tri-state journal (intent -> submitted -> confirmed)
// per (requestId, agent). An `intent` row is treated as possibly-submitted after
// the commit deadline (a crash can occur after RPC acceptance but before the
// hash is persisted) and is revealed anyway — a `not committed` reveal reverts
// harmlessly, a mined-but-unobserved commit is saved. Rows are driven from their
// stored deadlines, not from the GET, and the journal is written atomically.
//
// See PLAN_MOCK_AGENTS.md §5.

import { readFile, writeFile, rename, mkdir } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const API = process.env.VENUE_API || 'http://backend:8080';
const ADDRESSES = (process.env.AGENT_ADDRESSES || '').split(',').map(s => s.trim()).filter(Boolean);
const POLL_MS = parseInt(process.env.POLL_MS || '2000', 10);
const JOURNAL_DIR = process.env.JOURNAL_DIR || '/data';
const JOURNAL_PATH = `${JOURNAL_DIR}/quoter-journal.json`;
const RFM_BOND = 500_000_000n;            // 500 USDC (6-dec), RFM.sol:23
const ENABLED = (process.env.ENABLED || '').toLowerCase() === 'true';

// Bounded Pending retry: poll a tx a few times; if still Pending, re-submit the
// identical deterministic tx through the legal window (Nethereum maps every
// missing receipt to Pending, so a single-hash poll can hang forever).
const TX_POLLS = 4;
const TX_POLL_DELAY_MS = 1500;

if (!ENABLED && import.meta.url === pathToFileURL(process.argv[1] || '').href) { console.error('[mock-quoter] ENABLED is not true; refusing to start (fail-closed).'); process.exit(1); }
if (ADDRESSES.length < 2 && import.meta.url === pathToFileURL(process.argv[1] || '').href) { console.error('[mock-quoter] AGENT_ADDRESSES must list >=2 identities'); process.exit(1); }

// Sort identities by address (lowercase) so tick/size assignment is deterministic
// across restarts. agent[0] -> (tickA, sizeA), agent[1] -> (tickB, sizeB).
const IDENTITIES = ADDRESSES.map(a => a.toLowerCase()).sort();
const tokens = {};                       // address -> Bearer token

export { quoteFor, counterLeg, outstandingLiabilities, hasOtherActiveAuction, journal };

// --------------------------------------------------------------- journal
let journal = [];
async function loadJournal() {
  try { journal = JSON.parse(await readFile(JOURNAL_PATH, 'utf8')); }
  catch { journal = []; }
}
async function saveJournal() {
  if (!existsSync(JOURNAL_DIR)) await mkdir(JOURNAL_DIR, { recursive: true });
  const tmp = `${JOURNAL_PATH}.${process.pid}.tmp`;
  await writeFile(tmp, JSON.stringify(journal, null, 2));   // atomic: temp + rename
  await rename(tmp, JOURNAL_PATH);
}
function rowFor(requestId, agent) { return journal.find(r => r.requestId === requestId && r.agent === agent); }
function ensureRow(requestId, agent, ctx) {
  let r = rowFor(requestId, agent);
  if (!r) {
    r = { requestId, agent, tick: ctx.tick, size: ctx.size, commitDeadline: ctx.commitDeadline, revealDeadline: ctx.revealDeadline,
          commit: { state: 'intent', txHash: null }, reveal: { state: 'intent', txHash: null }, terminal: false, expired: false };
    journal.push(r);
  }
  return r;
}

// --------------------------------------------------------------- http
let backendDown = false;
async function api(method, path, token, body) {
  const headers = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;
  const res = await fetch(`${API}${path}`, { method, headers, body: body ? JSON.stringify(body) : undefined, signal: AbortSignal.timeout(8000) });
  if (res.status === 401 && token) return { status: 401 };   // caller rebinds
  const text = await res.text();
  let json = null; try { json = text ? JSON.parse(text) : null; } catch {}
  return { status: res.status, json };
}
async function rebind(address) {
  const r = await api('POST', '/v1/session/bind', null, { Address: address });
  if (r.status === 200 && r.json?.token) { tokens[address] = r.json.token; return true; }
  return false;
}
async function call(address, method, path, body) {
  let token = tokens[address];
  if (!token) { if (!(await rebind(address))) return { status: 0 }; token = tokens[address]; }
  let r = await api(method, path, token, body);
  if (r.status === 401) { if (await rebind(address)) r = await api(method, path, tokens[address], body); }  // rebind on 401
  return r;
}

// --------------------------------------------------------------- pricing (PLAN §5.2)
// Deterministic, distinct, MinMatch-covering. Integer-only.
function quoteFor(request, agentIndex) {
  const maxTick = Number(request.maxPriceTick);
  const minMatch = BigInt(request.minMatch);
  const quantity = BigInt(request.quantity);
  const minQuoteSize = BigInt(request.minQuoteSize);
  // Declared domain: need MaxPriceTick >= 40 for two distinct in-range ticks and
  // a born marginal the liquidity ladder can act on.
  if (maxTick < 40) return null;
  let tickA = Math.floor(maxTick * 0.85);
  let tickB = Math.floor(maxTick * 0.80);
  if (tickA === tickB) tickB = Math.max(1, tickA - 1);
  if (tickA < 1 || tickA > maxTick || tickB < 1 || tickB > maxTick) return null;
  // Joint sizes summing to exactly MinMatch; each >= MinQuoteSize, each < Quantity.
  const half = (minMatch + 1n) / 2n;          // ceil(MinMatch/2)
  const sizeA = half;
  const sizeB = minMatch - half;
  if (sizeA < minQuoteSize || sizeB < minQuoteSize) return null;
  if (sizeA >= quantity || sizeB >= quantity) return null;
  if (sizeA + sizeB < minMatch) return null;
  // Assign by index; agent[0] -> A (the higher tick / more aggressive YES price).
  const tick = agentIndex === 0 ? tickA : tickB;
  const size = agentIndex === 0 ? sizeA : sizeB;
  return { tick, size };
}

// counter-leg locked on a valid reveal (RFM.sol:224)
function counterLeg(size, tick) { return size - (size * BigInt(tick)) / 1000n; }

// --------------------------------------------------------------- tx status
async function txStatus(address, hash) {
  const r = await call(address, 'GET', `/v1/tx/${hash}/status`);
  return r.status === 200 ? r.json?.status : null;     // confirmed | reverted | pending
}

// --------------------------------------------------------------- phase machine
async function availableFor(address) {
  const r = await call(address, 'GET', '/v1/balances');
  if (r.status !== 200 || !r.json) return null;
  return BigInt(r.json.available || '0');
}

function outstandingLiabilities(agent, excludeRequestId) {
  // Counter-legs for OTHER non-terminal auctions of this identity. The current row's
  // own counter-leg is added separately in the preflight, so it MUST be excluded here
  // (double-counting it stranded the second reveal at the 1000-USDC floor — HCR2-2).
  // Count intent/submitted/confirmed (intent may land; expired still holds a bond
  // until finalize) — every non-terminal liability, not only submitted/confirmed.
  let total = 0n;
  for (const r of journal) {
    if (r.agent !== agent || r.terminal) continue;
    if (r.requestId === excludeRequestId) continue;
    if (r.commit.state === 'confirmed' && r.reveal.state === 'confirmed') continue;
    total += counterLeg(BigInt(r.size), r.tick);
  }
  return total;
}
// One-active-auction gate (PLAN §5.5): an identity may not open a new commit while
// it has another non-terminal auction. Prevents spreading thin / stranding bonds.
function hasOtherActiveAuction(agent, requestId) {
  return journal.some(r => r.agent === agent && !r.terminal && r.requestId !== requestId);
}

async function handleRequest(request) {
  const now = Math.floor(Date.now() / 1000);
  const requestId = request.requestId;
  const phase = request.phase;
  if (phase === 'finalized' || phase === 'failed' || phase === 'cancelled') {
    // mark all our rows for this request terminal
    for (const r of journal) if (r.requestId === requestId) r.terminal = true;
    return;
  }
  for (let i = 0; i < IDENTITIES.length; i++) {
    const agent = IDENTITIES[i];
    const q = quoteFor(request, i);
    if (!q) { console.log(`[mock-quoter] req ${requestId} ${agent}: outside supported domain, skip`); continue; }
    const ctx = { tick: q.tick, size: q.size.toString(),
                  commitDeadline: Number(request.commitDeadline), revealDeadline: Number(request.revealDeadline) };
    // One-active-auction gate: do not OPEN a new commit for an identity that already
    // has another non-terminal auction (PLAN §5.5). Resuming an existing row is allowed.
    let row = rowFor(requestId, agent);
    if (!row && hasOtherActiveAuction(agent, requestId)) { console.log(`[mock-quoter] req ${requestId} ${agent}: another auction active, skip`); continue; }
    row = ensureRow(requestId, agent, ctx);
    if (row.terminal) continue;
    const revealedHere = (request.reveals || []).some(rv => String(rv.mm).toLowerCase() === agent);
    if (row.reveal.state === 'confirmed' || revealedHere) { row.reveal.state = 'confirmed'; continue; }

    // COMMIT window
    if (now <= ctx.commitDeadline && row.commit.state !== 'confirmed') {
      const need = RFM_BOND + counterLeg(q.size, q.tick) + outstandingLiabilities(agent, requestId);
      const avail = await availableFor(agent);
      if (avail == null) { console.log(`[mock-quoter] req ${requestId} ${agent}: balance read failed`); continue; }
      if (avail < need) { console.log(`[mock-quoter] req ${requestId} ${agent}: insufficient available ${avail} < ${need}, skip`); continue; }
      // submit commit (omit salt -> backend derives deterministic salt)
      row.commit.state = 'intent'; await saveJournal();
      const r = await call(agent, 'POST', '/v1/rfm/commit', { RequestId: requestId, PriceTick: q.tick, Size: ctx.size });
      if (r.status === 200 && r.json?.txHash) {
        row.commit.state = 'submitted'; row.commit.txHash = r.json.txHash; await saveJournal();
        // bounded poll; if it confirms, great; if still pending, the next loop tick re-submits (idempotent overwrite, RFM.sol:188-203)
        for (let p = 0; p < TX_POLLS; p++) {
          const st = await txStatus(agent, r.json.txHash);
          if (st === 'confirmed') { row.commit.state = 'confirmed'; await saveJournal(); break; }
          if (st === 'reverted') { row.commit.state = 'intent'; await saveJournal(); break; }
          await sleep(TX_POLL_DELAY_MS);
        }
      } else {
        console.log(`[mock-quoter] req ${requestId} ${agent}: commit POST failed status=${r.status}`);
      }
      continue;
    }

    // REVEAL window: attempt for every commit intent|submitted|confirmed (not only confirmed).
    // Preflight excludes the current row from outstanding (its counter-leg is counted directly).
    if (now > ctx.commitDeadline && now <= ctx.revealDeadline &&
        (row.commit.state === 'intent' || row.commit.state === 'submitted' || row.commit.state === 'confirmed')) {
      const need = counterLeg(q.size, q.tick) + outstandingLiabilities(agent, requestId);
      const avail = await availableFor(agent);
      if (avail != null && avail < need) { console.log(`[mock-quoter] req ${requestId} ${agent}: insufficient for reveal, skip`); continue; }
      row.reveal.state = 'intent'; await saveJournal();
      const r = await call(agent, 'POST', '/v1/rfm/reveal', { RequestId: requestId, PriceTick: q.tick, Size: ctx.size });
      if (r.status === 200 && r.json?.txHash) {
        row.reveal.state = 'submitted'; row.reveal.txHash = r.json.txHash; await saveJournal();
        for (let p = 0; p < TX_POLLS; p++) {
          const st = await txStatus(agent, r.json.txHash);
          if (st === 'confirmed') { row.reveal.state = 'confirmed'; await saveJournal(); break; }
          if (st === 'reverted') { row.reveal.state = 'intent'; await saveJournal(); break; }
          await sleep(TX_POLL_DELAY_MS);
        }
      } else if (r.status === 400) {
        // `not committed` (reveal of an intent that never landed) reverts harmlessly;
        // a duplicate/already-revealed is also a 400. Either way stop retrying this hash.
        console.log(`[mock-quoter] req ${requestId} ${agent}: reveal 400 (${r.json?.error || ''})`);
      } else {
        console.log(`[mock-quoter] req ${requestId} ${agent}: reveal POST status=${r.status}`);
      }
      continue;
    }

    // EXPIRED: past reveal window, reveal not confirmed, request not terminal
    if (now > ctx.revealDeadline) {
      row.expired = true; await saveJournal();
    }
  }
}

// --------------------------------------------------------------- loop
async function tick() {
  try {
    let seen = new Set();
    const r = await api('GET', '/v1/rfm/requests', null, null);
    if (r.status === 200 && Array.isArray(r.json)) {
      if (backendDown) { backendDown = false; console.log('[mock-quoter] backend reachable again'); }
      for (const req of r.json) await handleRequest(req);
      seen = new Set(r.json.map(x => x.requestId));
    } else {
      if (!backendDown) { backendDown = true; console.log(`[mock-quoter] GET /v1/rfm/requests -> ${r.status}; driving journal rows from stored deadlines`); }
      // collection GET failed — do NOT return; fall through to drive journal rows.
    }
    // Drive journal rows not seen in the collection from their stored deadlines
    // (PLAN §5.3). A torn/missing GET is a RETRY, never a terminal decision (HCR2-3):
    // only an observed terminal phase (via handleRequest) marks a row terminal.
    for (const row of journal) {
      if (row.terminal || seen.has(row.requestId)) continue;
      const rr = await api('GET', `/v1/rfm/requests/${row.requestId}`, null, null);
      if (rr.status === 200 && rr.json) await handleRequest(rr.json);
      // else: retry next tick — do NOT mark terminal.
    }
    await saveJournal();
  } catch (e) { console.error('[mock-quoter] loop error', e.message); }
}

function sleep(ms) { return new Promise(r => setTimeout(r, ms)); }

// --------------------------------------------------------------- boot
async function main() {
  await loadJournal();
  console.log(`[mock-quoter] start; api=${API} identities=${IDENTITIES.join(',')} poll=${POLL_MS}ms`);
  for (const a of IDENTITIES) { if (!(await rebind(a))) console.error(`[mock-quoter] bind failed for ${a}`); }
  for (;;) { await tick(); await sleep(POLL_MS); }
}
if (import.meta.url === pathToFileURL(process.argv[1] || '').href) {
  main().catch(e => { console.error('[mock-quoter] fatal', e); process.exit(1); });
}