// mock-quoter control-path tests (node:test, no deps) — exercise tick()/driveRow with
// a mocked venue API. Covers HCR1-3/HCR2-3 (a stored row reveals WITHOUT any GET) and
// HCR2-6 (drain blocks new rows while existing rows continue). These are the
// regressions the pure-helper suite in test.js could not catch.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

const dir = mkdtempSync(join(tmpdir(), 'quoter-rec-'));
process.env.JOURNAL_DIR = dir;
process.env.AGENT_ADDRESSES = '0x806a04e96f9a5ada3ba53b630c6232f7cd142402,0x67001f904df5ce0957bc121016ce51c5e71fc782';
process.env.ENABLED = 'true';
process.env.VENUE_API = 'http://backend:8080';
process.env.POLL_MS = '10';
const mod = await import('./index.js');
const A = '0x67001f904df5ce0957bc121016ce51c5e71fc782';   // IDENTITIES[0] after sort+lowercase

function mockFetch(routes) {
  globalThis.fetch = async (url, opts) => {
    const u = String(url).replace(process.env.VENUE_API, '');
    const m = (opts?.method) || 'GET';
    routes._calls.push(`${m} ${u}`);
    for (const r of routes.handlers) {
      if (r.match(u, m)) return new Response(JSON.stringify(r.body(u, m, opts)), { status: r.status });
    }
    return new Response('', { status: 404 });
  };
}

test.after(() => { rmSync(dir, { recursive: true, force: true }); });

test('HCR1-3/HCR2-3: a stored reveal-window row reveals even when BOTH GETs fail', async () => {
  const routes = { _calls: [], handlers: [
    { match: (u) => u === '/v1/session/bind', status: 200, body: () => ({ token: 'tok', address: A }) },
    { match: (u) => u === '/v1/balances', status: 200, body: () => ({ available: '100000000000' }) },
    { match: (u) => u.includes('/v1/tx/'), status: 200, body: () => ({ status: 'confirmed' }) },
    { match: (u) => u === '/v1/rfm/requests', status: 500, body: () => 'err' },   // collection DOWN
    { match: (u, m) => u === '/v1/rfm/reveal' && m === 'POST', status: 200, body: () => ({ txHash: '0xdead' }) },
    { match: (u, m) => u === '/v1/rfm/commit' && m === 'POST', status: 200, body: () => ({ txHash: '0xfeed' }) },
  ] };
  mockFetch(routes);
  mod.journal.length = 0;
  const now = Math.floor(Date.now() / 1000);
  mod.journal.push({ requestId: '99', agent: A, tick: 510, size: '500000000',
    commitDeadline: now - 10, revealDeadline: now + 50,
    commit: { state: 'submitted', txHash: '0xfeed' }, reveal: { state: 'intent', txHash: null },
    terminal: false, expired: false });
  await mod.tick();
  const revealed = routes._calls.some(c => c.startsWith('POST /v1/rfm/reveal'));
  assert.ok(revealed, 'reveal POST must fire even with the collection GET down');
  assert.notEqual(mod.journal[0].reveal.state, 'intent', 'reveal state must advance');
});

test('HCR2-6: drain blocks NEW rows but existing rows keep driving', async () => {
  writeFileSync(join(dir, '.drain'), '');   // drain signal present
  const now = Math.floor(Date.now() / 1000);
  const fresh = { requestId: '77', market: '0x', side: 'yes', phase: 'open',
    quantity: '2000000000', maxPriceTick: '600', minMatch: '1000000000', minQuoteSize: '31250000',
    commitDeadline: now + 40, revealDeadline: now + 60, reveals: [] };
  const routes = { _calls: [], handlers: [
    { match: (u) => u === '/v1/session/bind', status: 200, body: () => ({ token: 'tok', address: A }) },
    { match: (u) => u === '/v1/balances', status: 200, body: () => ({ available: '100000000000' }) },
    { match: (u) => u.includes('/v1/tx/'), status: 200, body: () => ({ status: 'confirmed' }) },
    { match: (u) => u === '/v1/rfm/requests', status: 200, body: () => [fresh] },   // a fresh request is visible
    { match: (u, m) => u === '/v1/rfm/commit' && m === 'POST', status: 200, body: () => ({ txHash: '0xfeed' }) },
    { match: (u, m) => u === '/v1/rfm/reveal' && m === 'POST', status: 200, body: () => ({ txHash: '0xdead' }) },
  ] };
  mockFetch(routes);
  mod.journal.length = 0;
  // pre-existing row for an OLD request — must keep driving (reveal)
  mod.journal.push({ requestId: '55', agent: A, tick: 510, size: '500000000',
    commitDeadline: now - 10, revealDeadline: now + 50,
    commit: { state: 'submitted', txHash: '0xfeed' }, reveal: { state: 'intent', txHash: null },
    terminal: false, expired: false });
  await mod.tick();
  assert.equal(routes._calls.some(c => c.startsWith('POST /v1/rfm/reveal')), true, 'existing row still drives');
  assert.equal(mod.journal.some(r => r.requestId === '77'), false, 'drain must NOT create a row for the fresh request');
  rmSync(join(dir, '.drain'));
});