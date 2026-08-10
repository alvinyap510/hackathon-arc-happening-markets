// Standalone venue simulation for mock mode: balances, books, fills, an MM bot
// quoting every live market, and the channel/seq plumbing of the real WS contract.
// Wire shapes mirror the as-built backend (orders outcome/side/price, book levels
// {price,size}, trades as lists with yesBasisTick, envelope field `type`).

import type {
  Balances,
  Book,
  BookLevel,
  Market,
  NewOrder,
  Order,
  TokenPosition,
  Trade,
  WsEvent,
} from "./types";
import { foldBook, touchPrice } from "./bookFold";

const LEVEL_SIZES = ["420000000", "260000000", "180000000", "120000000", "80000000"];
const USER = "user";

interface SimOrder extends Order {
  owner: string;
}

export interface SimMarket extends Market {
  mid: number;
  orders: SimOrder[]; // resting MM orders
  trades: Trade[];
}

function lid() {
  return Math.random().toString(16).slice(2, 10);
}

export class MockVenue {
  sessionAddr = "";
  wallet = "0";
  chainFree = "0";
  markets: SimMarket[] = [];
  userOrders: SimOrder[] = [];
  positions = new Map<string, TokenPosition>(); // key `${marketId}:${outcome}`
  private seqs = new Map<string, { generation: number; seq: number }>();
  private listeners = new Map<string, Set<(ev: WsEvent) => void>>();
  private timer: ReturnType<typeof setInterval> | null = null;

  start(): void {
    if (this.timer) return;
    this.timer = setInterval(() => this.mmTick(), 1600);
  }

  emit(channel: string, data: unknown): void {
    const cbs = this.listeners.get(channel);
    if (!cbs || cbs.size === 0) return;
    const s = this.seqs.get(channel) ?? { generation: 1, seq: 0 };
    s.seq += 1;
    this.seqs.set(channel, s);
    const ev: WsEvent = {
      channel,
      type: channel.split(":")[0],
      generation: s.generation,
      seq: s.seq,
      prevSeq: s.seq - 1,
      data,
    };
    for (const cb of cbs) cb(ev);
  }

  subscribe(channel: string, snapshot: () => unknown, cb: (ev: WsEvent) => void): () => void {
    let set = this.listeners.get(channel);
    if (!set) {
      set = new Set();
      this.listeners.set(channel, set);
    }
    set.add(cb);
    const s = this.seqs.get(channel) ?? { generation: 1, seq: 0 };
    this.seqs.set(channel, s);
    queueMicrotask(() =>
      cb({ channel, type: "snapshot", generation: s.generation, seq: s.seq, data: snapshot() }),
    );
    return () => {
      set.delete(cb);
      if (set.size === 0) this.listeners.delete(channel);
    };
  }

  // ----- balances -----

  /** Open SELL remaining per (market,outcome) — the mock's token reservation. Real
   *  parity: a SELL reserves its outcome-token size until fill/cancel (Ledger.cs). */
  private sellReserved(marketId: string, outcome: string): bigint {
    return this.userOrders
      .filter((o) => o.status === "OPEN" && o.side === "SELL" && o.marketId === marketId && o.outcome === outcome)
      .reduce((acc, o) => acc + BigInt(o.remaining), 0n);
  }

  balances(): Balances {
    const reserved = this.userOrders
      .filter((o) => o.status === "OPEN" && o.side === "BUY")
      .reduce((acc, o) => acc + (BigInt(o.remaining) * BigInt(o.price ?? 0)) / 1000n, 0n);
    const free = BigInt(this.chainFree);
    return {
      chainFree: this.chainFree,
      reserved: reserved.toString(),
      available: (free - reserved).toString(),
      wallet: this.wallet,
      positions: [...this.positions.values()]
        .filter((p) => p.size !== "0")
        .map((p) => ({ ...p, reserved: this.sellReserved(p.marketId, p.outcome).toString() })),
    };
  }

  faucet(amount: string): void {
    this.wallet = (BigInt(this.wallet) + BigInt(amount)).toString();
  }

  deposit(amount: string): void {
    if (BigInt(amount) > BigInt(this.wallet)) throw new Error("insufficient wallet balance");
    this.wallet = (BigInt(this.wallet) - BigInt(amount)).toString();
    this.chainFree = (BigInt(this.chainFree) + BigInt(amount)).toString();
  }

  withdraw(amount: string): void {
    if (BigInt(amount) > BigInt(this.balances().available)) throw new Error("insufficient available balance");
    this.chainFree = (BigInt(this.chainFree) - BigInt(amount)).toString();
    this.wallet = (BigInt(this.wallet) + BigInt(amount)).toString();
  }

  // ----- markets + book -----

  addMarket(m: Omit<SimMarket, "orders" | "trades">): SimMarket {
    const market: SimMarket = { ...m, orders: this.mmBook(m.marketId, m.mid), trades: [] };
    this.markets.push(market);
    return market;
  }

  private mmBook(marketId: string, mid: number): SimOrder[] {
    const out: SimOrder[] = [];
    for (let i = 0; i < 5; i++) {
      out.push(this.mkMm(marketId, "BUY", mid - (i + 1) * 12, LEVEL_SIZES[i]));
      out.push(this.mkMm(marketId, "SELL", mid + (i + 1) * 12, LEVEL_SIZES[i]));
    }
    return out;
  }

  private mkMm(marketId: string, side: "BUY" | "SELL", price: number, size: string): SimOrder {
    return {
      orderId: `mm-${lid()}`,
      marketId,
      outcome: "YES",
      side,
      size,
      remaining: size,
      price: Math.min(990, Math.max(10, price)),
      type: "LIMIT",
      status: "OPEN",
      createdAt: new Date().toISOString(),
      owner: "mm",
    };
  }

  private openOrders(m: SimMarket, excludeId?: string): SimOrder[] {
    return [
      ...m.orders,
      ...this.userOrders.filter((o) => o.marketId === m.marketId && o.status === "OPEN" && o.orderId !== excludeId),
    ];
  }

  bookOf(m: SimMarket, excludeId?: string): Book {
    const live = this.openOrders(m, excludeId);
    // Real-wire parity (Engine.BookSnapshot): the ONE canonical book is projected into
    // FOUR DISJOINT arrays by each order's ORIGINAL outcome, NO arrays complemented into
    // NO ticks exactly once. Canonical side: bid = (BUY == YES); NO orders flip side.
    //   BUY YES -> yes.bids · SELL NO -> no.asks (bid) · SELL YES -> yes.asks · BUY NO -> no.bids (ask)
    const parts = { yesBids: new Map<number, bigint>(), yesAsks: new Map<number, bigint>(), noBids: new Map<number, bigint>(), noAsks: new Map<number, bigint>() };
    for (const o of live) {
      if (o.price === null) continue;
      const isBid = (o.side === "BUY") === (o.outcome === "YES");
      const map =
        o.outcome === "YES" ? (isBid ? parts.yesBids : parts.yesAsks) : isBid ? parts.noAsks : parts.noBids;
      map.set(o.price, (map.get(o.price) ?? 0n) + BigInt(o.remaining));
    }
    const toLevels = (map: Map<number, bigint>, desc: boolean): BookLevel[] =>
      [...map.entries()]
        .sort((a, b) => (desc ? b[0] - a[0] : a[0] - b[0]))
        .slice(0, 8)
        .map(([price, size]) => ({ price, size: size.toString() }));
    const s = this.seqs.get(`book:${m.marketId}`) ?? { generation: 1, seq: 0 };
    return {
      marketId: m.marketId,
      yes: { bids: toLevels(parts.yesBids, true), asks: toLevels(parts.yesAsks, false) },
      no: { bids: toLevels(parts.noBids, true), asks: toLevels(parts.noAsks, false) },
      generation: s.generation,
      seq: s.seq,
    };
  }

  // ----- orders + fills -----

  place(order: NewOrder): SimOrder {
    const m = this.markets.find((x) => x.marketId === order.marketId);
    if (!m) throw new Error("unknown market");
    const o: SimOrder = {
      orderId: `${USER}-${lid()}`,
      marketId: order.marketId,
      outcome: order.outcome,
      side: order.side,
      size: order.size,
      remaining: order.size,
      price: order.price,
      type: order.type,
      status: "OPEN",
      createdAt: new Date().toISOString(),
      owner: USER,
    };
    this.assertFundable(o);
    this.userOrders.push(o);
    this.tryFill(m, o);
    // parity with the real backend's BookChanged-on-rest: a pure rest changes the
    // book too, so push a frame (applyFill already emits for the fill path).
    if (o.status === "OPEN") this.emit(`book:${o.marketId}`, this.bookOf(m));
    this.emit(`user:${this.sessionAddr}`, { order: this.publicOrder(o) });
    return o;
  }

  private assertFundable(o: SimOrder): void {
    if (o.side === "BUY") {
      const cost = (BigInt(o.size) * BigInt(o.price ?? 0)) / 1000n;
      if (cost > BigInt(this.balances().available)) throw new Error("insufficient available balance");
    } else {
      // outcome-scoped available = position - open SELL reservation (real Ledger parity)
      const pos = this.positions.get(`${o.marketId}:${o.outcome}`);
      const availPos = (pos ? BigInt(pos.size) : 0n) - this.sellReserved(o.marketId, o.outcome);
      if (availPos < BigInt(o.size)) throw new Error("insufficient position");
    }
  }

  /** Best executable price for taker order o, in o's own tick basis, excluding itself.
   *  Sweeps the FULL canonical opposite side (shared fold), incl. cross-outcome makers. */
  crossPrice(m: SimMarket, o: SimOrder): number | null {
    const fold = foldBook(this.bookOf(m, o.orderId));
    return touchPrice(fold, o.side, o.outcome);
  }

  private tryFill(m: SimMarket, o: SimOrder): void {
    if (o.price === null) return;
    const cross = this.crossPrice(m, o);
    if (cross === null) return;
    const crosses = o.side === "BUY" ? o.price >= cross : o.price <= cross;
    if (crosses) this.applyFill(m, o, cross);
  }

  applyFill(m: SimMarket, o: SimOrder, price: number): void {
    o.remaining = "0";
    o.status = "FILLED";
    const size = BigInt(o.size);
    const cost = (size * BigInt(price)) / 1000n;
    const key = `${o.marketId}:${o.outcome}`;
    const pos = this.positions.get(key) ?? { marketId: o.marketId, outcome: o.outcome, size: "0", reserved: "0" };
    if (o.side === "BUY") {
      this.chainFree = (BigInt(this.chainFree) - cost).toString();
      pos.size = (BigInt(pos.size) + size).toString();
    } else {
      this.chainFree = (BigInt(this.chainFree) + cost).toString();
      pos.size = (BigInt(pos.size) - size).toString();
    }
    this.positions.set(key, pos);
    const trade: Trade = {
      marketId: o.marketId,
      yesBasisTick: o.outcome === "YES" ? price : 1000 - price,
      size: o.size,
      at: new Date().toISOString(),
      txHash: `0x${lid()}${lid()}${lid()}`,
    };
    m.trades.unshift(trade);
    m.lastTradeTick = trade.yesBasisTick;
    this.emit(`trades:${o.marketId}`, [trade]);
    this.emit(`book:${o.marketId}`, this.bookOf(m));
    this.emit(`user:${this.sessionAddr}`, { order: this.publicOrder(o), fill: trade });
  }

  cancel(orderId: string): void {
    const o = this.userOrders.find((x) => x.orderId === orderId);
    if (o && o.status === "OPEN") {
      o.status = "CANCELLED";
      this.emit(`book:${o.marketId}`, this.bookOf(this.markets.find((m) => m.marketId === o.marketId)!));
      this.emit(`user:${this.sessionAddr}`, { order: this.publicOrder(o) });
    }
  }

  publicOrder(o: SimOrder): Order {
    const { owner: _owner, ...pub } = o;
    return pub;
  }

  // ----- MM bot -----

  private mmTick(): void {
    for (const m of this.markets) {
      if (m.status !== "LIVE") continue;
      m.mid = Math.min(970, Math.max(30, m.mid + Math.round((Math.random() - 0.5) * 14)));
      m.midTick = m.mid;
      m.orders = this.mmBook(m.marketId, m.mid);
      this.emit(`book:${m.marketId}`, this.bookOf(m));
      // sweep resting user orders that now cross
      for (const o of this.userOrders) {
        if (o.marketId === m.marketId && o.status === "OPEN") this.tryFill(m, o);
      }
      if (Math.random() < 0.3) {
        const trade: Trade = {
          marketId: m.marketId,
          yesBasisTick: Math.random() < 0.5 ? m.mid + 12 : m.mid - 12,
          size: ((Math.floor(Math.random() * 40) + 5) * 10 ** 6).toString(),
          at: new Date().toISOString(),
          txHash: `0x${lid()}${lid()}${lid()}`,
        };
        m.trades.unshift(trade);
        m.lastTradeTick = trade.yesBasisTick;
        this.emit(`trades:${m.marketId}`, [trade]);
      }
    }
  }
}
