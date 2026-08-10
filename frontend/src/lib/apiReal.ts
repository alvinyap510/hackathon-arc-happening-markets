// Real venue client, aligned to the AS-BUILT backend (INTEGRATION_CONTRACT.md).
// REST under {base}/v1, one WS at {base}/ws. Client->server: {op:"subscribe"|"unsubscribe",
// channel}. Server->client: WsEvent frames with `type` + (generation, seq, prevSeq).
// Gap or generation bump -> drop deltas and REST-resnapshot the channel (with backoff
// retry); deltas arriving while a resnapshot is in flight are dropped. Reconnect ->
// resubscribe + resnapshot everything.

import type { VenueApi } from "./api";
import type {
  Balances,
  PlaceOrderResult,
  BalancesView,
  Book,
  FillView,
  Market,
  MarketId,
  MarketView,
  NewOrder,
  NewRfmRequest,
  NewRfmResponse,
  Order,
  OutcomeSide,
  RequestId,
  RfmFill,
  RfmPhase,
  RfmRequest,
  RfmReveal,
  RfmView,
  Session,
  TokenPosition,
  Trade,
  TxStatus,
  TxView,
  WsEvent,
} from "./types";

const BASE: string = (import.meta.env.VITE_API_URL ?? "").replace(/\/$/, "");

/** Demo bond, 500 USDC in 6-dec base units. The RfmView wire omits bond. */
const BOND_FALLBACK = "500000000";

/** State the useRfm hook assembles per request (store.tsx). */
interface RfmStateData {
  request: RfmRequest | null;
  reveals: RfmReveal[];
  final: import("./types").RfmFinal | null;
  bornMarketId: string | null;
}

export class RealApi implements VenueApi {
  readonly mode = "real" as const;
  private token: string | null = null;
  private ws: WebSocket | null = null;
  private listeners = new Map<string, Set<(ev: WsEvent) => void>>();
  private chanState = new Map<string, { generation: number; seq: number } | null>();
  /** Channels whose stream lost continuity; a REST resnapshot is needed/in flight. */
  private dirty = new Set<string>();
  /** Monotonic per-channel token invalidating stale resnapshot results. */
  private snapToken = new Map<string, number>();
  /** Dedup for background RFM REST enrichment fetches. */
  private rfmInflight = new Set<string>();
  /** RfmView fields the flat WS frame lacks (questionText/resolutionSource/closeTime). */
  private rfmMeta = new Map<string, { questionText: string; resolutionSource: string; closeTime: string }>();

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

  /** Drop the bearer and tear the socket down so no stale user channel survives
   *  the next login. onclose is cleared first: the reconnect timer must not
   *  revive a socket for a session that no longer exists. */
  logout(): void {
    this.token = null;
    const ws = this.ws;
    this.ws = null;
    if (ws) {
      ws.onclose = null;
      ws.onmessage = null;
      ws.onopen = null;
      try {
        ws.close();
      } catch {
        // already closing: nothing to do
      }
    }
    this.listeners.clear();
    this.chanState.clear();
    this.dirty.clear();
    this.snapToken.clear();
    this.rfmInflight.clear();
  }

  async getBalances(): Promise<Balances> {
    const v = await this.req<BalancesView>("GET", "/balances");
    return {
      chainFree: v.chainFree,
      reserved: v.reserved,
      available: v.available,
      wallet: v.wallet,
      positions: v.positions.map((p) => this.toPosition(p)),
    };
  }

  /** Wire positions carry marketId + outcome (lowercase) directly; the composite
   *  tokenId form "marketId:YES|NO" is only a fallback for older payloads. */
  private toPosition(p: { tokenId: string; marketId?: string; outcome?: string; amount: string; reserved?: string }): TokenPosition {
    const reserved = p.reserved ?? "0";
    if (p.marketId && p.outcome) {
      return { marketId: p.marketId, outcome: p.outcome.toUpperCase() === "NO" ? "NO" : "YES", size: p.amount, reserved };
    }
    const m = p.tokenId.match(/^(.+):(YES|NO)$/i);
    if (m) return { marketId: m[1], outcome: m[2].toUpperCase() as OutcomeSide, size: p.amount, reserved };
    return { marketId: p.tokenId, outcome: "YES", size: p.amount, reserved };
  }

  private static isoFromUnix(s?: string | null): string {
    if (!s) return "";
    const n = Number(s);
    if (!Number.isFinite(n) || n <= 0) return "";
    return new Date(n * 1000).toISOString();
  }

  private toMarket(v: MarketView): Market {
    return {
      marketId: v.marketId,
      questionText: v.questionText ?? v.marketId,
      resolutionSource: v.resolutionSource ?? "",
      closeTime: RealApi.isoFromUnix(v.closeTime) || v.closeTime || "",
      status: v.resolved ? "RESOLVED" : "LIVE",
      winningOutcome: v.winningOutcome ? (v.winningOutcome.toUpperCase() as OutcomeSide) : undefined,
      bornFromRfm: v.bornFromRfm ?? v.born != null,
      birth: v.born
        ? {
            marginalTick: v.born.marginalYesTick,
            vwapTick: v.born.vwapYesTick,
            filledQty: v.born.filled,
            postedTxHash: v.born.postedTxHash ?? null,
            txHash: v.born.txHash ?? null,
          }
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
    // GET /v1/markets/:id returns {market, trades}, not a bare MarketView
    const v = await this.req<{ market: MarketView }>("GET", `/markets/${id}`);
    return this.toMarket(v.market);
  }

  getBook(id: MarketId): Promise<Book> {
    return this.req("GET", `/book/${id}`);
  }

  async listTrades(id: MarketId): Promise<Trade[]> {
    const v = await this.req<{ trades?: FillView[] }>("GET", `/markets/${id}`);
    return (v.trades ?? []).map((t) => this.toTrade(t, id));
  }

  /** Normalize a REST trade record or a WS Fills delta item to the internal Trade. */
  private toTrade(x: FillView, marketIdHint: string): Trade {
    const outcome = x.outcome?.toLowerCase();
    const yesBasisTick =
      x.yesBasisTick ??
      (typeof x.outcomeTick === "number" ? (outcome === "no" ? 1000 - x.outcomeTick : x.outcomeTick) : 0);
    const at =
      typeof x.at === "number"
        ? new Date(x.at * 1000).toISOString()
        : typeof x.at === "string"
          ? x.at
          : new Date().toISOString(); // delta frames carry no timestamp: arrival time
    return { marketId: x.marketId ?? marketIdHint, yesBasisTick, size: x.size, at, txHash: x.txHash };
  }

  placeOrder(order: NewOrder): Promise<PlaceOrderResult> {
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

  // ----- RFM -----

  /** RfmView -> internal RfmRequest: uppercase phase/side, marketHash<-market,
   *  escrow<-escrowAmount, bond default, bornMarketId<-born?.marketId, numeric
   *  commitCount, ISO deadlines. WS frames lack metadata; the rfmMeta cache
   *  (warmed by REST calls) fills those fields. */
  private toRfm(v: RfmView): RfmRequest {
    if (v.questionText) {
      this.rfmMeta.set(v.requestId, {
        questionText: v.questionText,
        resolutionSource: v.resolutionSource ?? "",
        closeTime: RealApi.isoFromUnix(v.closeTime),
      });
    }
    const meta = this.rfmMeta.get(v.requestId);
    return {
      requestId: v.requestId,
      marketHash: v.market ?? "",
      questionText: v.questionText ?? meta?.questionText ?? "",
      resolutionSource: v.resolutionSource ?? meta?.resolutionSource ?? "",
      closeTime: RealApi.isoFromUnix(v.closeTime) || meta?.closeTime || "",
      side: (v.side ?? "yes").toUpperCase() as OutcomeSide,
      quantity: v.quantity ?? "0",
      minMatch: v.minMatch ?? "0",
      maxPriceTick: Number(v.maxPriceTick ?? 0),
      escrow: v.escrowAmount ?? "0",
      bond: BOND_FALLBACK,
      phase: (v.phase ?? "commit").toUpperCase() as RfmPhase,
      commitDeadline: RealApi.isoFromUnix(v.commitDeadline),
      revealDeadline: RealApi.isoFromUnix(v.revealDeadline),
      commitCount: Number(v.commitCount ?? 0),
      bornMarketId: v.born?.marketId,
      // from the indexed RequestPosted event, so it survives a refetch/reload
      // (the POST response hash is only in hand for the tab that created it)
      txHash: v.postedTxHash ?? undefined,
    };
  }

  /** Build the useRfm state from a full RfmView (REST or flat WS frame). */
  private toRfmState(v: RfmView): RfmStateData {
    const request = this.toRfm(v);
    const reveals: RfmReveal[] = (v.reveals ?? []).map((r) => ({
      mm: r.mm,
      priceTick: Number(r.tick),
      size: r.size,
      valid: r.inRange,
      txHash: r.txHash ?? null,
    }));
    let final: RfmStateData["final"] = null;
    if (request.phase === "FINALIZED") {
      // REST carries fills; the WS frame does not, so fall back to in-range reveals
      const rawFills: { mm: string; tick: string; size: string }[] =
        v.fills ?? (v.reveals ?? []).filter((r) => r.inRange);
      const fills: RfmFill[] = rawFills.map((f) => ({ mm: f.mm, priceTick: Number(f.tick), size: f.size }));
      const slashed = reveals.filter((r) => !r.valid).map((r) => ({ mm: r.mm, amount: BOND_FALLBACK }));
      const filledQty =
        v.born?.filled ?? fills.reduce((a, f) => a + BigInt(f.size), 0n).toString();
      final = {
        requestId: request.requestId,
        filledQty,
        marginalTick: v.born?.marginalYesTick ?? 0,
        vwapTick: v.born?.vwapYesTick ?? 0,
        fills,
        slashCount: slashed.length,
        slashed,
        marketId: v.born?.marketId,
        txHash: v.born?.txHash ?? null,
      };
    }
    return { request, reveals, final, bornMarketId: v.born?.marketId ?? null };
  }

  private async fetchRfmState(id: string): Promise<RfmStateData> {
    return this.toRfmState(await this.req<RfmView>("GET", `/rfm/requests/${id}`));
  }

  async listRfmRequests(): Promise<RfmRequest[]> {
    const vs = await this.req<RfmView[]>("GET", "/rfm/requests");
    return vs.map((v) => this.toRfm(v));
  }

  async getRfmRequest(id: RequestId): Promise<RfmRequest> {
    return this.toRfm(await this.req<RfmView>("GET", `/rfm/requests/${id}`));
  }

  async postRfmRequest(body: NewRfmRequest): Promise<RfmRequest> {
    // Backend binds long? CloseTime (unix-seconds) and hashes `Market` as the
    // marketHash preimage (PostRequestReq); send both in wire form.
    const wire = {
      ...body,
      market: body.questionText,
      closeTime: Math.floor(Date.parse(body.closeTime) / 1000),
    };
    // G6: returns {requestId, txHash}; metadata (G1) rides in the same body
    const res = await this.req<NewRfmResponse>("POST", "/rfm/requests", wire);
    this.rfmMeta.set(res.requestId, {
      questionText: body.questionText,
      resolutionSource: body.resolutionSource,
      closeTime: body.closeTime,
    });
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
        bond: BOND_FALLBACK,
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
  async redeem(marketId: MarketId, amount: string): Promise<TxStatus> {
    // backend AmountBody requires the redeemable amount
    return this.toTx(await this.req<TxView>("POST", `/markets/${marketId}/redeem`, { amount }));
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
        this.dirty.delete(channel);
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
      // reconnect: resubscribe + rebase every channel from REST. Mark channels
      // dirty so deltas arriving before the resnapshot lands are dropped; the
      // server's subscribe snapshot also clears dirty (whichever lands first).
      for (const channel of this.listeners.keys()) {
        this.send({ op: "subscribe", channel });
        this.chanState.set(channel, null);
        this.dirty.add(channel);
        void this.resnapshot(channel);
      }
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
      // a server snapshot rebases the channel and supersedes any REST resnapshot
      this.dirty.delete(ev.channel);
      this.snapToken.set(ev.channel, (this.snapToken.get(ev.channel) ?? 0) + 1);
      this.chanState.set(ev.channel, { generation: ev.generation, seq: ev.seq });
      this.dispatch(this.mapFrame(ev));
      return;
    }
    if (this.dirty.has(ev.channel)) return; // resnapshot in flight: drop deltas
    const last = this.chanState.get(ev.channel);
    if (last == null) {
      // no baseline (fresh subscribe or post-resnapshot rebase): accept
      this.chanState.set(ev.channel, { generation: ev.generation, seq: ev.seq });
      this.dispatch(this.mapFrame(ev));
      return;
    }
    const gap = ev.generation !== last.generation || (typeof ev.prevSeq === "number" && ev.prevSeq !== last.seq);
    if (gap) {
      // drop the gapped frame, block further deltas, and rebase from REST
      this.chanState.set(ev.channel, null);
      this.dirty.add(ev.channel);
      void this.resnapshot(ev.channel);
      return;
    }
    this.chanState.set(ev.channel, { generation: ev.generation, seq: ev.seq });
    this.dispatch(this.mapFrame(ev));
  }

  /** Map wire frames to the internal shapes the store consumes. */
  private mapFrame(ev: WsEvent): WsEvent {
    const [kind, id] = ev.channel.split(":");
    if (kind === "trades" && Array.isArray(ev.data)) {
      // both REST-shaped snapshot items and Fills delta items normalize to Trade
      return { ...ev, data: (ev.data as FillView[]).map((t) => this.toTrade(t, id)) };
    }
    if (kind === "rfm" && ev.data && typeof ev.data === "object") {
      // the rfm channel pushes a FLAT full-state frame (WsHub BuildRfmAsync) for
      // both snapshots and deltas; rebuild the useRfm state from it
      const view = ev.data as RfmView;
      const state = this.toRfmState(view);
      this.enrichRfm(ev.channel, view);
      return { ...ev, type: "snapshot", data: state };
    }
    return ev;
  }

  /** The flat rfm WS frame lacks metadata (and fills when finalized); fetch the
   *  REST view once per transition and dispatch the enriched state. */
  private enrichRfm(channel: string, v: RfmView): void {
    const needsMeta = !v.questionText && !this.rfmMeta.has(v.requestId);
    const needsFinal = v.phase?.toLowerCase() === "finalized";
    if ((!needsMeta && !needsFinal) || this.rfmInflight.has(channel)) return;
    this.rfmInflight.add(channel);
    void this.fetchRfmState(v.requestId)
      .then((state) => {
        // REST is a full-state read taken after the frame: at least as fresh
        this.dispatch({ channel, type: "snapshot", generation: 0, seq: 0, data: state });
      })
      .catch(() => {
        // enrichment is best-effort; the next frame retries
      })
      .finally(() => this.rfmInflight.delete(channel));
  }

  /** REST-resnapshot a channel with backoff; stale results are discarded via token. */
  private async resnapshot(channel: string): Promise<void> {
    const token = (this.snapToken.get(channel) ?? 0) + 1;
    this.snapToken.set(channel, token);
    let delay = 500;
    while (this.dirty.has(channel) && this.listeners.has(channel)) {
      if (this.snapToken.get(channel) !== token) return; // superseded by a newer snapshot
      try {
        const data = await this.fetchChannelData(channel);
        if (this.snapToken.get(channel) !== token) return; // a fresher state already landed
        this.dirty.delete(channel);
        this.dispatch({ channel, type: "snapshot", generation: 0, seq: 0, data });
        return;
      } catch {
        await new Promise((r) => setTimeout(r, delay));
        delay = Math.min(delay * 2, 5000);
      }
    }
  }

  private async fetchChannelData(channel: string): Promise<unknown> {
    const [kind, id] = channel.split(":");
    if (kind === "book") return this.getBook(id);
    if (kind === "trades") return this.listTrades(id);
    if (kind === "rfm") return this.fetchRfmState(id);
    if (kind === "user") return { balances: await this.getBalances() };
    return null;
  }

  private dispatch(ev: WsEvent): void {
    for (const cb of this.listeners.get(ev.channel) ?? []) cb(ev);
  }

  private send(payload: unknown): void {
    if (this.ws?.readyState === WebSocket.OPEN) this.ws.send(JSON.stringify(payload));
  }
}
