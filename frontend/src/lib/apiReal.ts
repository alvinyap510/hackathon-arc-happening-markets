// Real venue client, aligned to the AS-BUILT backend (INTEGRATION_CONTRACT.md).
// REST under {base}/v1, one WS at {base}/ws. Client->server: {op:"subscribe"|"unsubscribe",
// channel}. Server->client: WsEvent frames with `type` + (generation, seq, prevSeq).
// Gap or generation bump -> drop the frame and REST-resnapshot the channel.
// Reconnect -> resubscribe + resnapshot everything.

import type { VenueApi } from "./api";
import type {
  Balances,
  BalancesView,
  Book,
  Market,
  MarketId,
  MarketView,
  NewOrder,
  NewRfmRequest,
  NewRfmResponse,
  Order,
  RequestId,
  RfmRequest,
  Session,
  TokenPosition,
  Trade,
  TxStatus,
  TxView,
  WsEvent,
} from "./types";

const BASE: string = (import.meta.env.VITE_API_URL ?? "").replace(/\/$/, "");

export class RealApi implements VenueApi {
  readonly mode = "real" as const;
  private token: string | null = null;
  private ws: WebSocket | null = null;
  private listeners = new Map<string, Set<(ev: WsEvent) => void>>();
  private chanState = new Map<string, { generation: number; seq: number } | null>();

  // ----- REST -----

  private async req<T>(method: string, path: string, body?: unknown): Promise<T> {
    const res = await fetch(`${BASE}/v1${path}`, {
      method,
      headers: {
        "content-type": "application/json",
        ...(this.token ? { authorization: `Bearer ${this.token}` } : {}),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!res.ok) throw new Error(`${method} /v1${path} -> ${res.status}`);
    return (await res.json()) as T;
  }

  async login(email: string): Promise<Session> {
    // backend maps ref -> a signable demo account; the UI stays email-only
    const s = await this.req<{ token: string; address: string; gasless?: boolean }>("POST", "/session", { ref: email });
    this.token = s.token;
    return { email, address: s.address, token: s.token, gasless: s.gasless };
  }

  async getBalances(): Promise<Balances> {
    const v = await this.req<BalancesView>("GET", "/balances");
    return {
      chainFree: v.chainFree,
      reserved: v.reserved,
      available: v.available,
      wallet: v.wallet,
      positions: v.positions.map((p) => this.decodePosition(p.tokenId, p.amount)),
    };
  }

  /**
   * tokenId -> {marketId, outcome}. Assets.TokenId composite form "marketId:YES|NO"
   * decodes directly. NOTE: if the backend ships keccak-form token ids (the
   * contract form), they are one-way hashes; the raw id is kept as marketId so
   * positions still group, and a tokenId map from the backend is the real fix.
   */
  private decodePosition(tokenId: string, amount: string): TokenPosition {
    const m = tokenId.match(/^(.+):(YES|NO)$/);
    if (m) return { marketId: m[1], outcome: m[2] as TokenPosition["outcome"], size: amount };
    return { marketId: tokenId, outcome: "YES", size: amount };
  }

  private toMarket(v: MarketView): Market {
    return {
      marketId: v.marketId,
      questionText: v.questionText ?? v.marketId,
      resolutionSource: v.resolutionSource ?? "",
      closeTime: v.closeTime ?? "",
      status: v.resolved ? "RESOLVED" : "LIVE",
      winningOutcome: v.winningOutcome,
      bornFromRfm: v.bornFromRfm ?? v.born != null,
      birth: v.born
        ? { marginalTick: v.born.marginalYesTick, vwapTick: v.born.vwapYesTick, filledQty: v.born.filled }
        : undefined,
      midTick: v.midTick ?? null,
      lastTradeTick: null,
    };
  }

  async listMarkets(): Promise<Market[]> {
    const vs = await this.req<MarketView[]>("GET", "/markets");
    return vs.map((v) => this.toMarket(v));
  }

  async getMarket(id: MarketId): Promise<Market> {
    return this.toMarket(await this.req<MarketView>("GET", `/markets/${id}`));
  }

  getBook(id: MarketId): Promise<Book> {
    return this.req("GET", `/book/${id}`);
  }

  async listTrades(id: MarketId): Promise<Trade[]> {
    const v = await this.req<MarketView & { trades?: Trade[] }>("GET", `/markets/${id}`);
    return v.trades ?? [];
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

  async listPositions(): Promise<TokenPosition[]> {
    return (await this.getBalances()).positions;
  }

  listRfmRequests(): Promise<RfmRequest[]> {
    return this.req("GET", "/rfm/requests");
  }

  getRfmRequest(id: RequestId): Promise<RfmRequest> {
    return this.req("GET", `/rfm/requests/${id}`);
  }

  async postRfmRequest(body: NewRfmRequest): Promise<RfmRequest> {
    // G6: returns {requestId, txHash}; metadata (G1) rides in the same body
    const res = await this.req<NewRfmResponse>("POST", "/rfm/requests", body);
    try {
      const full = await this.getRfmRequest(res.requestId);
      return { ...full, txHash: res.txHash };
    } catch {
      return {
        requestId: res.requestId,
        marketHash: "",
        questionText: body.questionText,
        resolutionSource: body.resolutionSource,
        closeTime: body.closeTime,
        side: body.side,
        quantity: body.quantity,
        minMatch: body.minMatch,
        maxPriceTick: body.maxPriceTick,
        escrow: "0",
        bond: "500000000",
        phase: "COMMIT",
        commitDeadline: "",
        revealDeadline: "",
        commitCount: 0,
        txHash: res.txHash,
      };
    }
  }

  private toTx(v: TxView): TxStatus {
    return { hash: v.txHash, status: v.status };
  }

  async faucet(amount: string): Promise<TxStatus> {
    return this.toTx(await this.req<TxView>("POST", "/faucet", { amount }));
  }
  async deposit(amount: string): Promise<TxStatus> {
    return this.toTx(await this.req<TxView>("POST", "/vault/deposit", { amount }));
  }
  async withdraw(amount: string): Promise<TxStatus> {
    return this.toTx(await this.req<TxView>("POST", "/vault/withdraw", { amount }));
  }
  async redeem(marketId: MarketId): Promise<TxStatus> {
    return this.toTx(await this.req<TxView>("POST", `/markets/${marketId}/redeem`, {}));
  }
  async getTxStatus(hash: string): Promise<TxStatus> {
    return this.toTx(await this.req<TxView>("GET", `/tx/${hash}/status`));
  }

  // ----- WS -----

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
        this.chanState.delete(channel);
        this.send({ op: "unsubscribe", channel });
      }
    };
  }

  private ensureWs(): void {
    if (this.ws && this.ws.readyState <= WebSocket.OPEN) return;
    const url = BASE
      ? `${BASE.replace(/^http/, "ws")}/ws`
      : `${location.protocol === "https:" ? "wss" : "ws"}://${location.host}/ws`;
    this.ws = new WebSocket(url);
    this.ws.onmessage = (msg) => {
      try {
        this.handleFrame(JSON.parse(msg.data as string) as WsEvent);
      } catch {
        // malformed frame: ignore
      }
    };
    this.ws.onopen = () => {
      // reconnect: resubscribe + rebase every channel from REST
      for (const channel of this.listeners.keys()) this.send({ op: "subscribe", channel });
      this.chanState.clear();
      for (const channel of this.listeners.keys()) void this.resnapshot(channel);
    };
    this.ws.onclose = () => {
      this.ws = null;
      setTimeout(() => {
        if (this.listeners.size > 0) this.ensureWs();
      }, 1500);
    };
  }

  private handleFrame(ev: WsEvent): void {
    if (!ev || typeof ev.channel !== "string") return;
    if (ev.type === "snapshot") {
      this.chanState.set(ev.channel, { generation: ev.generation, seq: ev.seq });
      this.dispatch(ev);
      return;
    }
    const last = this.chanState.get(ev.channel);
    if (last == null) {
      // no baseline (fresh subscribe or post-resnapshot rebase): accept
      this.chanState.set(ev.channel, { generation: ev.generation, seq: ev.seq });
      this.dispatch(ev);
      return;
    }
    const gap = ev.generation !== last.generation || (typeof ev.prevSeq === "number" && ev.prevSeq !== last.seq);
    if (gap) {
      // drop the gapped frame and rebase from REST; the next frame becomes the baseline
      this.chanState.set(ev.channel, null);
      void this.resnapshot(ev.channel);
      return;
    }
    this.chanState.set(ev.channel, { generation: ev.generation, seq: ev.seq });
    this.dispatch(ev);
  }

  /** REST-resnapshot a channel and dispatch a synthesized snapshot frame. */
  private async resnapshot(channel: string): Promise<void> {
    const [kind, id] = channel.split(":");
    try {
      let data: unknown = null;
      if (kind === "book") data = await this.getBook(id);
      else if (kind === "trades") data = await this.listTrades(id);
      else if (kind === "rfm") {
        const request = await this.getRfmRequest(id);
        data = { request, reveals: [], final: null, bornMarketId: request.bornMarketId ?? null };
      } else if (kind === "user") data = { balances: await this.getBalances() };
      this.dispatch({ channel, type: "snapshot", generation: 0, seq: 0, data });
    } catch {
      // snapshot fetch failed; the WS flow will re-gap and retry
    }
  }

  private dispatch(ev: WsEvent): void {
    for (const cb of this.listeners.get(ev.channel) ?? []) cb(ev);
  }

  private send(payload: unknown): void {
    if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(payload));
  }
}
