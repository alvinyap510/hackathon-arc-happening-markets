// Deterministic fixture from PLAN_CONSOLIDATED_BOOK_UI v5 (CR-sealed).
// Book: BUY YES 500x100 · SELL NO 500-NO (bid 500) x50 · BUY NO 480-NO (ask 520) x120
//       · SELL YES 530x80  -> wire arrays are DISJOINT by original outcome.
import { describe, expect, it } from "vitest";
import { foldBook, touchPrice, sweepLevels, complement } from "./bookFold";
import type { Book } from "./types";

const fixture: Book = {
  marketId: "0xfixture",
  yes: {
    bids: [{ price: 500, size: "100000000" }], // BUY YES 500 x100
    asks: [{ price: 530, size: "80000000" }], // SELL YES 530 x80
  },
  no: {
    bids: [{ price: 480, size: "120000000" }], // BUY NO 480-NO -> canonical ask 520 x120
    asks: [{ price: 500, size: "50000000" }], // SELL NO 500-NO -> canonical bid 500 x50
  },
  generation: 1,
  seq: 0,
};

const empty: Book = { marketId: "0xe", yes: { bids: [], asks: [] }, no: { bids: [], asks: [] }, generation: 1, seq: 0 };

describe("foldBook (plan fixture)", () => {
  it("aggregates the bid collision and sorts asks asc: bid 500x150, asks 520x120 then 530x80", () => {
    const f = foldBook(fixture);
    expect(f.bids).toEqual([{ price: 500, size: "150000000" }]); // 100 + 50 aggregated
    expect(f.asks).toEqual([
      { price: 520, size: "120000000" },
      { price: 530, size: "80000000" },
    ]);
  });

  it("sorts bids desc when two distinct bid prices exist", () => {
    const twoBids: Book = { ...fixture, yes: { ...fixture.yes, bids: [...fixture.yes.bids, { price: 470, size: "10000000" }] } };
    const f = foldBook(twoBids);
    expect(f.bids.map((l) => l.price)).toEqual([500, 470]);
  });

  it("mid = floor((500+520)/2) = 510, matching the server midTick fold", () => {
    expect(foldBook(fixture).mid).toBe(510);
  });

  it("empty book: no crash, null mid, empty sides", () => {
    const f = foldBook(empty);
    expect(f.bids).toEqual([]);
    expect(f.asks).toEqual([]);
    expect(f.mid).toBeNull();
  });

  it("one-sided book: null mid, present side kept", () => {
    const oneSided: Book = { ...empty, yes: { bids: [{ price: 400, size: "1" }], asks: [] } };
    const f = foldBook(oneSided);
    expect(f.bids.length).toBe(1);
    expect(f.mid).toBeNull();
  });
});

describe("touchPrice (plan check 3: BuyYES 52.0, BuyNO 50.0, SellYES 50.0, SellNO 48.0)", () => {
  const f = foldBook(fixture);
  it("BUY YES = best ask 520", () => expect(touchPrice(f, "BUY", "YES")).toBe(520));
  it("BUY NO = C(best bid) = 500", () => expect(touchPrice(f, "BUY", "NO")).toBe(500));
  it("SELL YES = best bid 500", () => expect(touchPrice(f, "SELL", "YES")).toBe(500));
  it("SELL NO = C(best ask) = C(520) = 480", () => expect(touchPrice(f, "SELL", "NO")).toBe(480));
  it("empty side -> null", () => {
    const e = foldBook(empty);
    expect(touchPrice(e, "BUY", "YES")).toBeNull();
    expect(touchPrice(e, "SELL", "NO")).toBeNull();
  });
});

describe("sweepLevels (plan check 4: full opposite side, incl. the cross-outcome maker)", () => {
  const f = foldBook(fixture);
  it("BUY YES sweeps ALL asks incl. the BUY-NO-sourced 520 (the old code's missing case)", () => {
    expect(sweepLevels(f, "BUY", "YES").map((l) => l.price)).toEqual([520, 530]);
  });
  it("BUY NO sweeps C(B) in NO ticks", () => {
    expect(sweepLevels(f, "BUY", "NO").map((l) => l.price)).toEqual([complement(500)]);
  });
  it("SELL YES sweeps ALL bids incl. the SELL-NO-sourced 500 collision", () => {
    const levels = sweepLevels(f, "SELL", "YES");
    expect(levels).toEqual([{ price: 500, size: "150000000" }]);
  });
  it("SELL NO sweeps C(A): 48.0 then 47.0 (the round-4 corrected order)", () => {
    expect(sweepLevels(f, "SELL", "NO").map((l) => l.price)).toEqual([480, 470]);
  });
});
