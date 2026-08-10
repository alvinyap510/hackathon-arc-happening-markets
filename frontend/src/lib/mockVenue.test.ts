// MockVenue parity tests (plan v5 deterministic cases): disjoint real-wire book
// projection, SELL admission against amount-reserved, and a book frame on pure rest.
import { beforeEach, describe, expect, it } from "vitest";
import { MockVenue, type SimMarket } from "./mockVenue";
import { foldBook } from "./bookFold";
import type { Market } from "./types";

const BASE: Omit<Market, "marketId"> = {
  questionText: "t?",
  resolutionSource: "s",
  closeTime: new Date(Date.now() + 86400000).toISOString(),
  status: "LIVE",
  bornFromRfm: false,
  midTick: 500,
  lastTradeTick: null,
};

function bareMarket(v: MockVenue, id: string): SimMarket {
  // addMarket seeds MM orders; strip them so fixtures are exact.
  const m = v.addMarket({ ...BASE, marketId: id, mid: 500 });
  m.orders = [];
  return m;
}

describe("MockVenue real-wire parity", () => {
  let v: MockVenue;
  let m: SimMarket;
  beforeEach(() => {
    v = new MockVenue();
    v.sessionAddr = "0xmock";
    v.wallet = "100000000000";
    v.deposit("50000000000");
    m = bareMarket(v, "0xM");
  });

  it("bookOf emits DISJOINT original-outcome arrays (no mirroring/doubling)", () => {
    // the four intents: BUY YES 500, SELL NO 500-NO, SELL YES 530, BUY NO 480-NO
    v.place({ marketId: "0xM", outcome: "YES", side: "BUY", price: 500, size: "100000000", type: "LIMIT" });
    // seed positions so SELLs are fundable
    v.positions.set("0xM:NO", { marketId: "0xM", outcome: "NO", size: "50000000", reserved: "0" });
    v.positions.set("0xM:YES", { marketId: "0xM", outcome: "YES", size: "80000000", reserved: "0" });
    v.place({ marketId: "0xM", outcome: "NO", side: "SELL", price: 500, size: "50000000", type: "LIMIT" });
    v.place({ marketId: "0xM", outcome: "YES", side: "SELL", price: 530, size: "80000000", type: "LIMIT" });
    v.place({ marketId: "0xM", outcome: "NO", side: "BUY", price: 480, size: "120000000", type: "LIMIT" });
    const book = v.bookOf(m);
    expect(book.yes.bids).toEqual([{ price: 500, size: "100000000" }]);
    expect(book.no.asks).toEqual([{ price: 500, size: "50000000" }]); // SELL NO stays in NO ticks
    expect(book.yes.asks).toEqual([{ price: 530, size: "80000000" }]);
    expect(book.no.bids).toEqual([{ price: 480, size: "120000000" }]); // BUY NO stays in NO ticks
    // and the shared fold reconstructs the sealed canonical fixture
    const f = foldBook(book);
    expect(f.bids).toEqual([{ price: 500, size: "150000000" }]);
    expect(f.asks.map((l) => l.price)).toEqual([520, 530]);
    expect(f.mid).toBe(510);
  });

  it("SELL admission uses amount - open-SELL reserved (oversell blocked)", () => {
    v.positions.set("0xM:YES", { marketId: "0xM", outcome: "YES", size: "100000000", reserved: "0" });
    v.place({ marketId: "0xM", outcome: "YES", side: "SELL", price: 900, size: "60000000", type: "LIMIT" }); // rests, reserves 60
    expect(() =>
      v.place({ marketId: "0xM", outcome: "YES", side: "SELL", price: 900, size: "60000000", type: "LIMIT" }),
    ).toThrow(/insufficient position/); // only 40 available
    // and balances expose the derived reservation
    const pos = v.balances().positions.find((p) => p.marketId === "0xM" && p.outcome === "YES")!;
    expect(pos.reserved).toBe("60000000");
  });

  it("a pure no-fill rest emits a book frame (no reload needed)", () => {
    let frames = 0;
    v.subscribe(`book:0xM`, () => v.bookOf(m), (ev) => { if (ev.type === "book") frames++; });
    v.place({ marketId: "0xM", outcome: "YES", side: "BUY", price: 300, size: "10000000", type: "LIMIT" }); // far from any ask -> pure rest
    expect(frames).toBe(1);
  });
});
