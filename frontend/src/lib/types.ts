// Venue API contract types. Mirror of PLAN_FRONTEND section 5 / PLAN_BACKEND section 5.
// Money: 6-decimal USDC base units as integers (strings over the wire to avoid float loss).
// Prices: integer ticks 0-1000 on the canonical YES basis.

export type Address = string;
export type MarketId = string;
export type RequestId = string;
export type OrderId = string;

export type OutcomeSide = "YES" | "NO";

/** The 4-direction trading surface. */
export type OrderDirection = "BUY_YES" | "BUY_NO" | "SELL_YES" | "SELL_NO";
export type OrderType = "LIMIT" | "MARKET";
export type OrderStatus = "OPEN" | "FILLED" | "PARTIAL" | "CANCELLED" | "REJECTED";

export type MarketStatus = "LIVE" | "RESOLVED";
export type RfmPhase = "OPEN" | "COMMIT" | "REVEAL" | "FINALIZED" | "FAILED" | "CANCELLED";

export interface Session {
  email: string;
  address: Address;
  token: string;
}

export interface TokenPosition {
  marketId: MarketId;
  outcome: OutcomeSide;
  size: string;
}

export interface Balances {
  chainFree: string;
  reserved: string;
  available: string;
  positions: TokenPosition[];
}

export interface Market {
  marketId: MarketId;
  questionText: string;
  resolutionSource: string;
  closeTime: string; // ISO
  status: MarketStatus;
  winningOutcome?: OutcomeSide;
  bornFromRfm: boolean;
  /** Pay-as-bid birth marks, present on RFM-born markets (G5). */
  birth?: { marginalTick: number; vwapTick: number; filledQty: string };
  /** Current YES mid tick, null if the book is empty. */
  midTick: number | null;
  lastTradeTick: number | null;
}

export interface BookLevel {
  tick: number;
  size: string;
}

export interface BookSide {
  bids: BookLevel[];
  asks: BookLevel[];
}

/** YES and NO projections of the one canonical book. */
export interface Book {
  marketId: MarketId;
  yes: BookSide;
  no: BookSide;
  generation: number;
  seq: number;
}

export interface Order {
  orderId: OrderId;
  marketId: MarketId;
  direction: OrderDirection;
  type: OrderType;
  tick: number | null;
  size: string;
  filled: string;
  status: OrderStatus;
  createdAt: string;
}

export interface NewOrder {
  marketId: MarketId;
  direction: OrderDirection;
  type: OrderType;
  /** Required for LIMIT. MARKET is emulated client-side as a far-touch limit. */
  tick?: number;
  size: string;
}

export interface Trade {
  marketId: MarketId;
  tick: number;
  size: string;
  takerDirection: OrderDirection;
  at: string;
  txHash?: string;
}

export interface RfmRequest {
  requestId: RequestId;
  marketHash: string;
  questionText: string;
  resolutionSource: string;
  closeTime: string;
  side: OutcomeSide;
  quantity: string;
  minMatch: string;
  maxPriceTick: number;
  escrow: string;
  bond: string;
  phase: RfmPhase;
  commitDeadline: string;
  revealDeadline: string;
  commitCount: number;
  bornMarketId?: MarketId;
}

export interface RfmReveal {
  mm: Address;
  priceTick: number;
  size: string;
  valid: boolean;
}

export interface RfmFill {
  mm: Address;
  priceTick: number;
  size: string;
}

export interface RfmFinal {
  requestId: RequestId;
  filledQty: string;
  marginalTick: number;
  vwapTick: number;
  fills: RfmFill[];
  slashCount: number;
  slashed: { mm: Address; amount: string }[];
  marketId?: MarketId;
}

export interface TxStatus {
  hash: string;
  status: "SUBMITTED" | "MINED" | "INDEXED" | "FAILED";
}

export interface NewRfmRequest {
  questionText: string;
  resolutionSource: string;
  closeTime: string;
  side: OutcomeSide;
  quantity: string;
  minMatch: string;
  maxPriceTick: number;
}

/** WS event envelopes. Channels: book:<mkt> trades:<mkt> rfm:<reqId> user:<addr>. */
export type WsEvent =
  | { channel: string; kind: "snapshot"; generation: number; seq: number; data: unknown }
  | { channel: string; kind: "delta"; generation: number; seq: number; prevSeq: number; data: unknown };
