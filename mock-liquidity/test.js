// mock-liquidity tests — node:test, no deps. Covers the boot-crash regression (HCR2-1/
// HCR1-1), the ladder no-cross geometry, LegCost, and the fail-closed book preflight
// (HCR2-5/HCR1-4).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { rungs, legCost, wouldCross } from './index.js';

test('HCR2-1/HCR1-1: module imports without crashing (the .filter-on-string + TDZ fix)', () => {
  // importing ./index.js above already executed module top-level; reaching here means
  // the parse line and the function declarations did not throw.
  assert.ok(true, 'mock-liquidity module imported cleanly');
});

test('rungs: one bid + one ask anchored on mid, sum 980 (no self-cross), in range', () => {
  const l = rungs(560);
  assert.ok(l, 'mid=560 in range');
  assert.equal(l.bid.outcome, 'yes'); assert.equal(l.bid.side, 'buy'); assert.equal(l.bid.price, 550);
  assert.equal(l.ask.outcome, 'no');  assert.equal(l.ask.side, 'buy');  assert.equal(l.ask.price, 430);
  // YES bid price + NO price = 550 + 430 = 980 < 1000 -> no self-cross; spread = 20
  assert.equal(l.bid.price + l.ask.price, 980);
});

test('rungs: out-of-range mids return null (skip, do not place)', () => {
  assert.equal(rungs(5), null, 'mid-10 < 1');
  assert.equal(rungs(995), null, '1000-mid-10 < 1');
});

test('legCost: floor(size*price/1000) USDC reserved by a BUY', () => {
  assert.equal(legCost(550), 110000000n, '200 shares @ 550 -> 110 USDC');
  assert.equal(legCost(430), 86000000n, '200 shares @ 430 -> 86 USDC');
});

test('HCR2-5/HCR1-4: wouldCross fails CLOSED on a missing/malformed book', () => {
  assert.equal(wouldCross(null, { outcome: 'yes', price: 550 }, 560), true, 'null book -> do not place');
  assert.equal(wouldCross({}, { outcome: 'yes', price: 550 }, 560), true, 'no yes-side book -> do not place');
});

test('wouldCross: bid crosses a resting ask at or below it', () => {
  const book = { yes: { bids: [], asks: [{ price: 550, size: '1' }, { price: 560, size: '1' }] } };
  assert.equal(wouldCross(book, { outcome: 'yes', price: 550 }, 560), true, 'ask 550 <= bid 550 -> cross');
  assert.equal(wouldCross(book, { outcome: 'yes', price: 540 }, 560), false, 'best ask 550 > bid 540 -> no cross');
});

test('wouldCross: ask (from BUY NO) crosses a resting bid at or above it', () => {
  // BUY NO @ 430 -> ask @ mid+10 = 570; crosses a resting YES bid >= 570
  const book = { yes: { bids: [{ price: 570, size: '1' }], asks: [] } };
  assert.equal(wouldCross(book, { outcome: 'no', price: 430 }, 560), true, 'bid 570 >= askYes 570 -> cross');
  const book2 = { yes: { bids: [{ price: 560, size: '1' }], asks: [] } };
  assert.equal(wouldCross(book2, { outcome: 'no', price: 430 }, 560), false, 'bid 560 < askYes 570 -> no cross');
});