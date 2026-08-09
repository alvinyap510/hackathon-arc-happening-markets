// mock-quoter tests — node:test, no deps. Covers the money-path regressions the Sol
// code CR raised: joint MinMatch sizing, the funding double-count (HCR2-2/HCR1-2),
// the one-active-auction gate, and domain rejection.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { quoteFor, counterLeg, outstandingLiabilities, hasOtherActiveAuction, journal } from './index.js';

// Sealed showcase request: maxPriceTick=600, minMatch=1000e6, quantity=2000e6.
const SHOWCASE = {
  maxPriceTick: '600', minMatch: '1000000000', quantity: '2000000000',
  minQuoteSize: '31250000', commitDeadline: String(Math.floor(Date.now()/1000)+40), revealDeadline: String(Math.floor(Date.now()/1000)+60),
};

test('quoteFor: two distinct ticks, joint sizes sum to exactly MinMatch, each in range', () => {
  const a = quoteFor(SHOWCASE, 0);
  const b = quoteFor(SHOWCASE, 1);
  assert.ok(a && b);
  assert.notEqual(a.tick, b.tick, 'ticks must be distinct');
  assert.ok(a.tick <= 600 && a.tick >= 1 && b.tick <= 600 && b.tick >= 1, 'ticks in [1,MaxPriceTick]');
  assert.equal(a.size + b.size, 1000000000n, 'sizes sum to MinMatch');
  assert.ok(a.size >= 31250000n && b.size >= 31250000n, 'each >= MinQuoteSize');
  assert.ok(a.size < 2000000000n && b.size < 2000000000n, 'each < Quantity');
  // tickA (agent 0) > tickB (agent 1): 510 vs 480
  assert.equal(a.tick, 510);
  assert.equal(b.tick, 480);
});

test('quoteFor: rejects the unsupported domain (MaxPriceTick < 40)', () => {
  assert.equal(quoteFor({ ...SHOWCASE, maxPriceTick: '30' }, 0), null);
});

test('counterLeg: size - floor(size*tick/1000)', () => {
  // 500e6 @ tick 480 -> 500e6 - 240e6 = 260e6 (the showcase maker B counter-leg)
  assert.equal(counterLeg(500000000n, 480), 260000000n);
  assert.equal(counterLeg(500000000n, 510), 245000000n);
});

test('HCR2-2/HCR1-2: reveal preflight does NOT double-count the current row counter-leg', () => {
  journal.length = 0;
  // current request, maker B (tick 480, size 500e6): commit submitted, reveal intent
  journal.push({ requestId: '7', agent: '0xb', tick: 480, size: '500000000',
    commitDeadline: 0, revealDeadline: 0, commit: { state: 'submitted', txHash: '0x1' }, reveal: { state: 'intent', txHash: null }, terminal: false, expired: false });
  // outstanding EXCLUDING the current request must be 0 (no other auctions)
  assert.equal(outstandingLiabilities('0xb', '7'), 0n, 'current row excluded -> no double-count');
  // reveal need = counterLeg(current) + outstanding = 260e6 + 0 = 260e6
  // available after commit (1000e6 - 500e6 bond) = 500e6 >= 260e6 -> reveal proceeds (was 520e6 before fix)
  assert.ok(500000000n >= counterLeg(500000000n, 480) + outstandingLiabilities('0xb', '7'), 'reveal must be affordable at the 1000-USDC floor');
});

test('outstandingLiabilities counts another auction (not the current) as a liability', () => {
  journal.length = 0;
  journal.push({ requestId: '7', agent: '0xb', tick: 480, size: '500000000', commitDeadline: 0, revealDeadline: 0,
    commit: { state: 'submitted', txHash: '0x1' }, reveal: { state: 'intent', txHash: null }, terminal: false, expired: false });
  journal.push({ requestId: '8', agent: '0xb', tick: 400, size: '500000000', commitDeadline: 0, revealDeadline: 0,
    commit: { state: 'intent', txHash: null }, reveal: { state: 'intent', txHash: null }, terminal: false, expired: false });
  // reserving for req 8 must count req 7's counter-leg (intent rows count too)
  assert.equal(outstandingLiabilities('0xb', '8'), counterLeg(500000000n, 480));
});

test('hasOtherActiveAuction: gates one active auction per identity', () => {
  journal.length = 0;
  journal.push({ requestId: '7', agent: '0xb', tick: 480, size: '500000000', commitDeadline: 0, revealDeadline: 0,
    commit: { state: 'submitted', txHash: '0x1' }, reveal: { state: 'intent', txHash: null }, terminal: false, expired: false });
  assert.equal(hasOtherActiveAuction('0xb', '8'), true, 'cannot open req 8 while req 7 active');
  assert.equal(hasOtherActiveAuction('0xb', '7'), false, 'resuming req 7 is allowed');
  assert.equal(hasOtherActiveAuction('0xa', '7'), false, 'other identity unaffected');
});

test('expired rows are retained as liabilities until terminalized', () => {
  journal.length = 0;
  journal.push({ requestId: '7', agent: '0xb', tick: 480, size: '500000000', commitDeadline: 0, revealDeadline: 0,
    commit: { state: 'submitted', txHash: '0x1' }, reveal: { state: 'intent', txHash: null }, terminal: false, expired: true });
  assert.equal(outstandingLiabilities('0xb', '8'), counterLeg(500000000n, 480), 'expired-but-nonterminal still counts');
});