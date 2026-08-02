// Mock venue adapter: fixtures (2 seeded markets + a pre-staged auction mid-commit)
// over the simulation core. The entire UI runs against this with zero backend.

import type { VenueApi } from "./api";
import { MockVenue } from "./mockVenue";
import { RfmManager, type RfmRuntime } from "./mockRfm";
import type {
  Balances,
  Book,
  Market,
  MarketId,
  NewOrder,
  NewRfmRequest,
  Order,
  RequestId,
  RfmRequest,
  Session,
  TokenPosition,
  Trade,
  TxStatus,
  WsEvent,
} from "./types";

const SEEDED: { id: string; q: string; src: string; close: string; mid: number }[] = [
  {
    id: "mkt-btc-150k",
    q: "Will Bitcoin trade above $150,000 before 1 January 2027?",
    src: "Coinbase BTC-USD, any print",
    close: "2026-12-31T23:59:00Z",
    mid: 420,
  },
  {
    id: "mkt-arg-wc26",
    q: "Will Argentina win the 2026 FIFA World Cup?",
    src: "FIFA official result",
    close: "2026-07-19T20:00:00Z",
    mid: 260,
  },
];

function fakeHash(): string {
  return `0x${[...Array(8)].map(() => Math.floor(Math.random() * 0xffffffff).toString(16).padStart(8, "0")).join("")}`;
}

export class MockApi implements VenueApi {
  readonly mode = "mock" as const;
  private venue = new MockVenue({ now: () => Date.now() });
  private rfm = new RfmManager(this.venue);

  constructor() {
    for (const s of SEEDED) {
      this.venue.addMarket({
        marketId: s.id,
        questionText: s.q,
        resolutionSource: s.src,
        closeTime: s.close,
        status: "LIVE",
        bornFromRfm: false,
        mid: s.mid,
        midTick: s.mid,
        lastTradeTick: s.mid + 12,
      });
    }
    // pre-staged auction, already 60% through its commit window, so the RFM tab
    // is live on first paint (mirrors the on-stage choreography in the spec)
    this.rfm.launch(
      {
        questionText: "Will the Fed cut rates at the September 2026 FOMC meeting?",
        resolutionSource: "Federal Reserve statement",
        closeTime: "2026-09-16T18:00:00Z",
        side: "YES",
        quantity: "1000000000",
        minMatch: "500000000",
        maxPriceTick: 620,
      },
      12_000,
    );
    this.venue.start();
  }

  async login(email: string): Promise<Session> {
    let h = 0;
    for (const c of email) h = (h * 31 + c.charCodeAt(0)) >>> 0;
    const address = `0x${h.toString(16).padStart(8, "0")}d3m0${"0".repeat(28)}`.slice(0, 42);
    this.venue.sessionAddr = address;
    return { email, address, token: "mock-session" };
  }

  async getBalances(): Promise<Balances> {
    return this.venue.balances();
  }

  async listMarkets(): Promise<Market[]> {
    return this.venue.markets.map(({ orders: _o, trades: _t, mid: _m, ...m }) => m);
  }

  async getMarket(id: MarketId): Promise<Market> {
    const m = this.venue.markets.find((x) => x.marketId === id);
    if (!m) throw new Error("unknown market");
    const { orders: _o, trades: _t, mid: _mm, ...pub } = m;
    return pub;
  }

  async getBook(id: MarketId): Promise<Book> {
    const m = this.venue.markets.find((x) => x.marketId === id);
    if (!m) throw new Error("unknown market");
    return this.venue.bookOf(m);
  }

  async listTrades(id: MarketId): Promise<Trade[]> {
    return this.venue.markets.find((x) => x.marketId === id)?.trades ?? [];
  }

  async placeOrder(order: NewOrder): Promise<Order> {
    return this.venue.publicOrder(this.venue.place(order));
  }

  async cancelOrder(id: string): Promise<void> {
    this.venue.cancel(id);
  }

  async listOpenOrders(): Promise<Order[]> {
    return this.venue.userOrders.filter((o) => o.status === "OPEN").map((o) => this.venue.publicOrder(o));
  }

  async listPositions(): Promise<TokenPosition[]> {
    return this.venue.balances().positions;
  }

  async listRfmRequests(): Promise<RfmRequest[]> {
    return this.rfm.list();
  }

  async getRfmRequest(id: RequestId): Promise<RfmRequest> {
    const rt = this.rfm.get(id);
    if (!rt) throw new Error("unknown request");
    return rt.request;
  }

  async postRfmRequest(req: NewRfmRequest): Promise<RfmRequest> {
    const rt = this.rfm.launch(req);
    return rt.request;
  }

  private tx(): TxStatus {
    return { hash: fakeHash(), status: "MINED" };
  }

  async faucet(amount: string): Promise<TxStatus> {
    this.venue.faucet(amount);
    return this.tx();
  }
  async deposit(amount: string): Promise<TxStatus> {
    this.venue.deposit(amount);
    return this.tx();
  }
  async withdraw(amount: string): Promise<TxStatus> {
    this.venue.withdraw(amount);
    return this.tx();
  }
  async redeem(_marketId: MarketId): Promise<TxStatus> {
    return this.tx();
  }
  async getTxStatus(hash: string): Promise<TxStatus> {
    return { hash, status: "INDEXED" };
  }

  subscribe(channel: string, cb: (ev: WsEvent) => void): () => void {
    const [kind, id] = channel.split(":");
    const snapshot = (): unknown => {
      if (kind === "book") {
        const m = this.venue.markets.find((x) => x.marketId === id);
        return m ? this.venue.bookOf(m) : null;
      }
      if (kind === "trades") return this.venue.markets.find((x) => x.marketId === id)?.trades ?? [];
      if (kind === "rfm") {
        const rt: RfmRuntime | undefined = this.rfm.get(id);
        return rt ? { request: rt.request, reveals: rt.reveals, final: rt.final, bornMarketId: rt.bornMarketId } : null;
      }
      if (kind === "user") return { balances: this.venue.balances() };
      return null;
    };
    return this.venue.subscribe(channel, snapshot, cb);
  }

  /** Mock-only extras used by the auction visualizer. */
  rfmRuntime(id: RequestId): RfmRuntime | undefined {
    return this.rfm.get(id);
  }
  walletBalance(): string {
    return this.venue.wallet;
  }
}
