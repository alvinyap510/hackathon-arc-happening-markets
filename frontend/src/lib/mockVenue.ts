// Standalone venue simulation for mock mode: balances, books, fills, an MM bot
// quoting every live market, and the channel/seq plumbing of the real WS contract.

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

export interface SimConfig {
  now: () => number;
}

const LEVEL_SIZES = ["420000000", "260000000", "180000000", "120000000", "80000000"];
const USER = "user";

interface SimOrder extends Order {
  owner: string;
}

export interface SimMarket extends Market {
  mid: number;
  orders: SimOrder[]; // resting orders, both owners
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

  constructor(private cfg: SimConfig) {}

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
    const ev: WsEvent = { channel, kind: "delta", generation: s.generation, seq: s.seq, prevSeq: s.seq - 1, data };
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
    queueMicrotask(() => cb({ channel, kind: "snapshot", generation: s.generation, seq: s.seq, data: snapshot() }));
    return () => {
      set.delete(cb);
      if (set.size === 0) this.listeners.delete(channel);
    };
  }

  // ----- balances -----

  balances(): Balances {
    const reserved = this.userOrders
      .filter((o) => o.status === "OPEN")
      .reduce((acc, o) => acc + this.reserveOf(o), 0n);
    const free = BigInt(this.chainFree);
    return {
      chainFree: this.chainFree,
      reserved: reserved.toString(),
      available: (free - reserved).toString(),
      positions: [...this.positions.values()].filter((p) => p.size !== "0"),
    };
  }

  private reserveOf(o: SimOrder): bigint {
    const remaining = BigInt(o.size) - BigInt(o.filled);
    if (o.direction.startsWith("BUY")) return (remaining * BigInt(o.tick ?? 0)) / 1000n;
    return 0n; // sells reserve tokens, not USDC
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
    const market: SimMarket = { ...m, orders: this.mmBook(m.mid), trades: [] };
    this.markets.push(market);
    return market;
  }

  private mmBook(mid: number): SimOrder[] {
    const out: SimOrder[] = [];
    for (let i = 0; i < 5; i++) {
      out.push(this.mkMmOrder("BUY_YES", mid - (i + 1) * 12, LEVEL_SIZES[i]));
      out.push(this.mkMmOrder("SELL_YES", mid + (i + 1) * 12, LEVEL_SIZES[i]));
    }
    return out;
  }

  private mkMmOrder(direction: "BUY_YES" | "SELL_YES", tick: number, size: string): SimOrder {
    return {
      orderId: `mm-${lid()}`,
      marketId: "",
      direction,
      type: "LIMIT",
      tick: Math.min(990, Math.max(10, tick)),
      size,
      filled: "0",
      status: "OPEN",
      createdAt: new Date().toISOString(),
      owner: "mm",
    };
  }

  bookOf(m: SimMarket): Book {
    const live = [...m.orders, ...this.userOrders.filter((o) => o.marketId === m.marketId && o.status === "OPEN")];
    const agg = (dirs: string[], pred: (t: number) => boolean, sort: (a: number, b: number) => number): BookLevel[] => {
      const byTick = new Map<number, bigint>();
      for (const o of live) {
        if (!dirs.includes(o.direction) || o.tick === null || !pred(o.tick)) continue;
        byTick.set(o.tick, (byTick.get(o.tick) ?? 0n) + (BigInt(o.size) - BigInt(o.filled)));
      }
      return [...byTick.entries()]
        .sort((a, b) => sort(a[0], b[0]))
        .slice(0, 8)
        .map(([tick, size]) => ({ tick, size: size.toString() }));
    };
    // YES basis: bids = BUY_YES resting, asks = SELL_YES resting. NO is the complement projection.
    const yesBids = agg(["BUY_YES"], () => true, (a, b) => b - a);
    const yesAsks = agg(["SELL_YES"], () => true, (a, b) => a - b);
    const noBids: BookLevel[] = yesAsks.map((l) => ({ tick: 1000 - l.tick, size: l.size })).sort((a, b) => b.tick - a.tick);
    const noAsks: BookLevel[] = yesBids.map((l) => ({ tick: 1000 - l.tick, size: l.size })).sort((a, b) => a.tick - b.tick);
    const s = this.seqs.get(`book:${m.marketId}`) ?? { generation: 1, seq: 0 };
    return { marketId: m.marketId, yes: { bids: yesBids, asks: yesAsks }, no: { bids: noBids, asks: noAsks }, generation: s.generation, seq: s.seq };
  }

  // ----- orders + fills -----

  place(order: NewOrder): SimOrder {
    const m = this.markets.find((x) => x.marketId === order.marketId);
    if (!m) throw new Error("unknown market");
    const o: SimOrder = {
      orderId: `${USER}-${lid()}`,
      marketId: order.marketId,
      direction: order.direction,
      type: "LIMIT",
      tick: order.tick ?? null,
      size: order.size,
      filled: "0",
      status: "OPEN",
      createdAt: new Date().toISOString(),
      owner: USER,
    };
    this.assertFundable(o);
    this.userOrders.push(o);
    this.tryFill(m, o);
    this.emit(`user:${this.sessionAddr}`, { kind: "order", order: this.publicOrder(o) });
    return o;
  }

  private assertFundable(o: SimOrder): void {
    if (o.direction.startsWith("BUY")) {
      const cost = (BigInt(o.size) * BigInt(o.tick ?? 0)) / 1000n;
      if (cost > BigInt(this.balances().available)) throw new Error("insufficient available balance");
    } else {
      const outcome = o.direction.endsWith("YES") ? "YES" : "NO";
      const pos = this.positions.get(`${o.marketId}:${outcome}`);
      if (!pos || BigInt(pos.size) < BigInt(o.size)) throw new Error("insufficient position");
    }
  }

  private tryFill(m: SimMarket, o: SimOrder): void {
    const book = this.bookOf(m);
    const buy = o.direction.startsWith("BUY");
    const t = o.tick ?? 0;
    const crossTick = this.crossPrice(m, o);
    const crosses = buy ? t >= crossTick : t <= crossTick;
    void book;
    if (!crosses || crossTick <= 0) return;
    this.applyFill(m, o, crossTick);
  }

  /** Best executable tick for taker order o, expressed in o's own tick basis. */
  crossPrice(m: SimMarket, o: SimOrder): number {
    const bestBid = m.mid - 12;
    const bestAsk = m.mid + 12;
    switch (o.direction) {
      case "BUY_YES":
        return bestAsk;
      case "SELL_YES":
        return bestBid;
      case "BUY_NO":
        return 1000 - bestBid; // NO ask = complement of YES bid
      case "SELL_NO":
        return 1000 - bestAsk;
    }
  }

  applyFill(m: SimMarket, o: SimOrder, tick: number): void {
    o.filled = o.size;
    o.status = "FILLED";
    const size = BigInt(o.size);
    const cost = (size * BigInt(tick)) / 1000n;
    const outcome = o.direction.endsWith("YES") ? "YES" : "NO";
    const key = `${o.marketId}:${outcome}`;
    const pos = this.positions.get(key) ?? { marketId: o.marketId, outcome, size: "0" };
    if (o.direction.startsWith("BUY")) {
      this.chainFree = (BigInt(this.chainFree) - cost).toString();
      pos.size = (BigInt(pos.size) + size).toString();
    } else {
      this.chainFree = (BigInt(this.chainFree) + cost).toString();
      pos.size = (BigInt(pos.size) - size).toString();
    }
    this.positions.set(key, pos);
    const trade: Trade = {
      marketId: o.marketId,
      tick,
      size: o.size,
      takerDirection: o.direction,
      at: new Date().toISOString(),
      txHash: `0x${lid()}${lid()}${lid()}`,
    };
    m.trades.unshift(trade);
    m.lastTradeTick = outcome === "YES" ? tick : 1000 - tick;
    this.emit(`trades:${o.marketId}`, trade);
    this.emit(`book:${o.marketId}`, this.bookOf(m));
    this.emit(`user:${this.sessionAddr}`, { kind: "fill", order: this.publicOrder(o), trade });
  }

  cancel(orderId: string): void {
    const o = this.userOrders.find((x) => x.orderId === orderId);
    if (o && o.status === "OPEN") {
      o.status = "CANCELLED";
      this.emit(`book:${o.marketId}`, this.bookOf(this.markets.find((m) => m.marketId === o.marketId)!));
      this.emit(`user:${this.sessionAddr}`, { kind: "order", order: this.publicOrder(o) });
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
      m.orders = this.mmBook(m.mid);
      this.emit(`book:${m.marketId}`, this.bookOf(m));
      // sweep resting user orders that now cross
      for (const o of this.userOrders) {
        if (o.marketId === m.marketId && o.status === "OPEN") this.tryFill(m, o);
      }
      if (Math.random() < 0.3) {
        const buy = Math.random() < 0.5;
        const trade: Trade = {
          marketId: m.marketId,
          tick: buy ? m.mid + 12 : m.mid - 12,
          size: ((Math.floor(Math.random() * 40) + 5) * 10 ** 6).toString(),
          takerDirection: buy ? "BUY_YES" : "SELL_YES",
          at: new Date().toISOString(),
          txHash: `0x${lid()}${lid()}${lid()}`,
        };
        m.trades.unshift(trade);
        m.lastTradeTick = trade.tick;
        this.emit(`trades:${m.marketId}`, trade);
      }
    }
  }
}
