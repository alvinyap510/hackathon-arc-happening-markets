// VenueApi: the single seam between UI and backend. PLAN_FRONTEND section 5.
// Real vs mock is chosen once here by VITE_API_MODE; nothing else in the app knows.

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

export interface VenueApi {
  readonly mode: "mock" | "real";

  login(email: string): Promise<Session>;
  getBalances(): Promise<Balances>;

  listMarkets(): Promise<Market[]>;
  getMarket(id: MarketId): Promise<Market>;
  getBook(id: MarketId): Promise<Book>;
  listTrades(id: MarketId): Promise<Trade[]>;

  placeOrder(order: NewOrder): Promise<Order>;
  cancelOrder(id: string): Promise<void>;
  listOpenOrders(): Promise<Order[]>;
  listPositions(): Promise<TokenPosition[]>;

  listRfmRequests(): Promise<RfmRequest[]>;
  getRfmRequest(id: RequestId): Promise<RfmRequest>;
  postRfmRequest(req: NewRfmRequest): Promise<RfmRequest>;

  faucet(amount: string): Promise<TxStatus>;
  deposit(amount: string): Promise<TxStatus>;
  withdraw(amount: string): Promise<TxStatus>;
  redeem(marketId: MarketId): Promise<TxStatus>;
  getTxStatus(hash: string): Promise<TxStatus>;

  /** Subscribe to a WS channel (book:<mkt>, trades:<mkt>, rfm:<reqId>, user:<addr>). */
  subscribe(channel: string, cb: (ev: WsEvent) => void): () => void;
}

export const API_MODE: "mock" | "real" =
  (import.meta.env.VITE_API_MODE ?? "mock") === "real" ? "real" : "mock";

let apiPromise: Promise<VenueApi> | null = null;

export function createApi(): Promise<VenueApi> {
  // singleton: StrictMode double-mount must not spin up two mock venues
  apiPromise ??=
    API_MODE === "real"
      ? import("./apiReal").then((m) => new m.RealApi())
      : import("./apiMock").then((m) => new m.MockApi());
  return apiPromise;
}
