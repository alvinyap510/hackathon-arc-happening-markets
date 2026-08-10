// The ONE canonical-book fold, shared by BookPanel, OrderTicket, and mock matching.
//
// The venue keeps a single YES-basis book per market; the /v1/book wire projects it
// into four DISJOINT arrays by each resting order's ORIGINAL outcome, with NO arrays
// complemented into NO ticks exactly once server-side (Engine.BookSnapshot):
//   yes.bids = BUY YES  (bid, YES ticks)     no.asks = SELL NO (bid, NO ticks)
//   yes.asks = SELL YES (ask, YES ticks)     no.bids = BUY NO  (ask, NO ticks)
// Reconstruction complements the NO arrays back ONCE and merges:
//   B (bids) = merge(yes.bids, C(no.asks))  desc     A (asks) = merge(yes.asks, C(no.bids))  asc
// All arithmetic is integer/BigInt; mid uses floor division to match the server's midTick.

import type { Book, BookLevel, OrderSide, OutcomeSide } from "./types";

export const complement = (p: number): number => 1000 - p;

/** Aggregate sizes at equal price (BigInt sums), then sort. */
function mergeLevels(a: BookLevel[], b: BookLevel[], desc: boolean): BookLevel[] {
  const acc = new Map<number, bigint>();
  for (const l of [...a, ...b]) acc.set(l.price, (acc.get(l.price) ?? 0n) + BigInt(l.size));
  return [...acc.entries()]
    .sort((x, y) => (desc ? y[0] - x[0] : x[0] - y[0]))
    .map(([price, size]) => ({ price, size: size.toString() }));
}

const c = (levels: BookLevel[]): BookLevel[] => levels.map((l) => ({ price: complement(l.price), size: l.size }));

export interface FoldedBook {
  /** Canonical YES-basis bids (BUY YES + complemented SELL NO), best (highest) first. */
  bids: BookLevel[];
  /** Canonical YES-basis asks (SELL YES + complemented BUY NO), best (lowest) first. */
  asks: BookLevel[];
  /** floor((bestBid + bestAsk) / 2) — identical to the server's midTick fold — or null. */
  mid: number | null;
}

export function foldBook(book: Book): FoldedBook {
  const bids = mergeLevels(book.yes.bids, c(book.no.asks), true);
  const asks = mergeLevels(book.yes.asks, c(book.no.bids), false);
  const mid = bids.length > 0 && asks.length > 0 ? Math.floor((bids[0].price + asks[0].price) / 2) : null;
  return { bids, asks, mid };
}

/** Touch (first-fill) price for an intent, in that OUTCOME's ticks, or null when empty.
 *  BUY YES -> asks[0]; SELL YES -> bids[0]; BUY NO -> C(bids[0]); SELL NO -> C(asks[0]). */
export function touchPrice(fold: FoldedBook, side: OrderSide, outcome: OutcomeSide): number | null {
  const canonical =
    side === "BUY" ? (outcome === "YES" ? fold.asks[0] : fold.bids[0]) : outcome === "YES" ? fold.bids[0] : fold.asks[0];
  if (!canonical) return null;
  return outcome === "YES" ? canonical.price : complement(canonical.price);
}

/** The FULL opposite-side levels a MARKET order sweeps, in the intent's OUTCOME ticks,
 *  best-first. BUY YES = A; BUY NO = C(B); SELL YES = B; SELL NO = C(A). */
export function sweepLevels(fold: FoldedBook, side: OrderSide, outcome: OutcomeSide): BookLevel[] {
  const canonical = side === "BUY" ? (outcome === "YES" ? fold.asks : fold.bids) : outcome === "YES" ? fold.bids : fold.asks;
  return outcome === "YES" ? canonical : c(canonical);
}
