// Real venue client: REST + WebSocket to the backend (PLAN_BACKEND section 5).
// Assumed wire shape: REST under {base}/..., one WS at {base}/ws with client->server
// {op:"subscribe"|"unsubscribe", channel} and server->server WsEvent envelopes.
// If the backend settles on a different WS handshake, only this file changes.

import type { VenueApi } from "./api";
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

const BASE: string = import.meta.env.VITE_API_URL || "/api";

export class RealApi implements VenueApi {
  readonly mode = "real" as const;
  private token: string | null = null;
  private ws: WebSocket | null = null;
  private listeners = new Map<string, Set<(ev: WsEvent) => void>>();

  private async req<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(`${BASE}${path}`, {
      method,
      headers: {
        "content-type": "application/json",
        ...(this.token ? { authorization: `Bearer ${this.token}` } : {}),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`${method} ${path} -> ${res.status}`);
    return (await res.json()) as T;
  }

  async login(email: string): Promise<Session> {
    const s = await this.req<Session>("POST", "/session", { email });
    this.token = s.token;
    return s;
  }

  getBalances(): Promise<Balances> {
    return this.req("GET", "/balances");
  }

  listMarkets(): Promise<Market[]> {
    return this.req("GET", "/markets");
  }
  getMarket(id: MarketId): Promise<Market> {
    return this.req("GET", `/markets/${id}`);
  }
  getBook(id: MarketId): Promise<Book> {
    return this.req("GET", `/book/${id}`);
  }
  listTrades(id: MarketId): Promise<Trade[]> {
    return this.req("GET", `/markets/${id}`).then((m) => (m as Market & { trades: Trade[] }).trades ?? []);
  }

  placeOrder(order: NewOrder): Promise<Order> {
    return this.req("POST", "/orders", order);
  }
  cancelOrder(id: string): Promise<void> {
    return this.req("DELETE", `/orders/${id}`);
  }
  listOpenOrders(): Promise<Order[]> {
    return this.req("GET", "/orders?status=open");
  }
  listPositions(): Promise<TokenPosition[]> {
    return this.req("GET", "/positions");
  }

  listRfmRequests(): Promise<RfmRequest[]> {
    return this.req("GET", "/rfm/requests");
  }
  getRfmRequest(id: RequestId): Promise<RfmRequest> {
    return this.req("GET", `/rfm/requests/${id}`);
  }
  postRfmRequest(body: NewRfmRequest): Promise<RfmRequest> {
    return this.req("POST", "/rfm/requests", body);
  }

  faucet(amount: string): Promise<TxStatus> {
    return this.req("POST", "/faucet", { amount });
  }
  deposit(amount: string): Promise<TxStatus> {
    return this.req("POST", "/vault/deposit", { amount });
  }
  withdraw(amount: string): Promise<TxStatus> {
    return this.req("POST", "/vault/withdraw", { amount });
  }
  redeem(marketId: MarketId): Promise<TxStatus> {
    return this.req("POST", `/markets/${marketId}/redeem`, {});
  }
  getTxStatus(hash: string): Promise<TxStatus> {
    return this.req("GET", `/tx/${hash}/status`);
  }

  subscribe(channel: string, cb: (ev: WsEvent) => void): () => void {
    let set = this.listeners.get(channel);
    if (!set) {
      set = new Set();
      this.listeners.set(channel, set);
    }
    set.add(cb);
    this.ensureWs();
    this.send({ op: "subscribe", channel });
    return () => {
      set.delete(cb);
      if (set.size === 0) {
        this.listeners.delete(channel);
        this.send({ op: "unsubscribe", channel });
      }
    };
  }

  private ensureWs(): void {
    if (this.ws && this.ws.readyState <= WebSocket.OPEN) return;
    const url = BASE.replace(/^http/, "ws").replace(/\/api$/, "/api/ws");
    this.ws = new WebSocket(url);
    this.ws.onmessage = (msg) => {
      try {
        const ev = JSON.parse(msg.data as string) as WsEvent;
        for (const cb of this.listeners.get(ev.channel) ?? []) cb(ev);
      } catch {
        // malformed frame: ignore
      }
    };
    this.ws.onopen = () => {
      for (const channel of this.listeners.keys()) this.send({ op: "subscribe", channel });
    };
    this.ws.onclose = () => {
      this.ws = null;
      setTimeout(() => {
        if (this.listeners.size > 0) this.ensureWs();
      }, 1500);
    };
  }

  private send(payload: unknown): void {
    if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(payload));
  }
}
