// mock-liquidity restart-reconciliation test (node:test, no deps) — exercises
// reconcileRow() with a mocked /v1/orders so a crash after the bid POST (before the
// journal saved) is recovered on restart instead of double-placing or going one-sided
// (HCR1-5/HCR2-4). This is the control path test.js's pure helpers could not cover.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dir = mkdtempSync(join(tmpdir(), 'liq-rec-'));
process.env.JOURNAL_DIR = dir;
process.env.AGENT_ADDRESSES = '0x2b3efc21d8d8e724e9b5a9ef6fc5ca4e8c95c40d';
process.env.ENABLED = 'true';
process.env.VENUE_API = 'http://backend:8080';
const mod = await import('./index.js');
const MID = 560;
const LAD = mod.rungs(MID);   // bid: yes@550, ask: no@430

test.after(() => { rmSync(dir, { recursive: true, force: true }); });

test('HCR1-5/HCR2-4: reconcileRow marks a rung placed from a matching resting order (crash-after-POST recovery)', async () => {
  globalThis.fetch = async (url, opts) => {
    const u = String(url).replace(process.env.VENUE_API, '');
    if (u === '/v1/session/bind') return new Response(JSON.stringify({ token: 'tok', address: '0x2b3e' }), { status: 200 });
    if (u.includes('/v1/orders')) {
      // the bid already rests (orderId real), the ask is absent (crash before its journal save)
      return new Response(JSON.stringify([
        { orderId: 'o_bid', marketId: '0xM', outcome: 'yes', side: 'buy', price: 550, status: 'resting' },
      ]), { status: 200 });
    }
    return new Response('', { status: 404 });
  };
  mod.journal.length = 0;
  const row = mod.rowFor('0xM');          // fresh row: neither placed
  assert.equal(row.bid.placed, false);
  await mod.reconcileRow(row, '0xM', LAD);
  assert.equal(row.bid.placed, true, 'bid matched the resting order -> marked placed (no double-POST)');
  assert.equal(row.bid.orderId, 'o_bid');
  assert.equal(row.ask.placed, false, 'ask absent -> still missing, will be POSTed next');
});

test('reconcileRow: both rungs placed when both resting orders exist', async () => {
  globalThis.fetch = async (url) => {
    const u = String(url).replace(process.env.VENUE_API, '');
    if (u === '/v1/session/bind') return new Response(JSON.stringify({ token: 'tok' }), { status: 200 });
    if (u.includes('/v1/orders')) return new Response(JSON.stringify([
      { orderId: 'o1', marketId: '0xN', outcome: 'yes', side: 'buy', price: 550, status: 'resting' },
      { orderId: 'o2', marketId: '0xN', outcome: 'no', side: 'buy', price: 430, status: 'resting' },
    ]), { status: 200 });
    return new Response('', { status: 404 });
  };
  mod.journal.length = 0;
  const row = mod.rowFor('0xN');
  await mod.reconcileRow(row, '0xN', LAD);
  assert.equal(row.bid.placed && row.ask.placed, true, 'both sides reconciled -> no re-POST');
});