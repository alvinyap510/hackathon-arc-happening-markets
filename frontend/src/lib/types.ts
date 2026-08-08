// Venue types. Aligned to the AS-BUILT backend (INTEGRATION_CONTRACT.md):
// routes under /v1, WS at /ws with envelope field "type", orders as
// {outcome,side,price}, book levels {price,size}, positions {tokenId,amount},
// trades as lists carrying yesBasisTick, MarketView exists/closing/resolved.
// Money: 6-dec USDC base units as strings. Prices: integer ticks 0-1000, YES basis.

export type Address = string;
export type MarketId = string;
export type RequestId = string;

export type OutcomeSide = "YES" | "NO";
export type OrderSide = "BUY" | "SELL";
export type OrderType = "LIMIT" | "MARKET";
export type OrderStatus = "OPEN" | "FILLED" | "PARTIAL" | "CANCELLED" | "REJECTED";

/** UI-only: the 4-direction ticket surface. Maps to outcome+side at the wire. */
export type OrderDirection = "BUY_YES" | "BUY_NO" | "SELL_YES" | "SELL_NO";

export type MarketStatus = "LIVE" | "RESOLVED";
export type RfmPhase = "OPEN" | "COMMIT" | "REVEAL" | "FINALIZED" | "FAILED" | "CANCELLED";

// ----- session -----

/** Wire: POST /v1/session {ref} -> {token, address, gasless}. */
export interface Session {
  email: string;
  address: Address;
  token: string;
  gasless?: boolean;
}

// ----- balances -----

/** Internal position form, derived from the wire {tokenId, amount}. */
export interface TokenPosition {
  marketId: MarketId;
  outcome: OutcomeSide;
  size: string;
}

/** Wire: GET /v1/balances. G4 adds `wallet` (on-chain MockUSDC balance).
 *  Positions carry marketId + outcome (lowercase) alongside tokenId; the
 *  composite tokenId parse is only a fallback for older payloads. */
export interface BalancesView {
  chainFree: string;
  reserved: string;
  available: string;
  wallet?: string;
  positions: { tokenId: string; marketId?: string; outcome?: string; amount: string }[];
}

export interface Balances {
  chainFree: string;
  reserved: string;
  available: string;
  wallet?: string;
  positions: TokenPosition[];
}

// ----- markets -----

/** Wire: GET /v1/markets[/:id] (MarketView + G1 metadata + G5 summary fields). */
export interface MarketView {
  marketId: MarketId;
  exists: boolean;
  closing: boolean;
  resolved: boolean;
  winningOutcome?: string; // lowercase on the wire; normalized in toMarket
  born?: { requestId: RequestId; marginalYesTick: number; vwapYesTick: number; filled: string };
  bornFromRfm?: boolean; // G5
  midTick?: number | null; // G5, server-computed, null if one-sided
  questionText?: string; // G1
  resolutionSource?: string; // G1
  closeTime?: string; // G1, unix-seconds string
}

/** Internal market form used across the UI. */
export interface Market {
  marketId: MarketId;
  questionText: string;
  resolutionSource: string;
  closeTime: string; // ISO
  status: MarketStatus;
  winningOutcome?: OutcomeSide;
  bornFromRfm: boolean;
  /** Pay-as-bid birth marks on RFM-born markets (wire born.{marginalYesTick, vwapYesTick, filled}). */
  birth?: { marginalTick: number; vwapTick: number; filledQty: string };
  midTick: number | null;
  lastTradeTick: number | null;
}

// ----- book -----

/** Wire level: {price, size}. */
export interface BookLevel {
  price: number;
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

// ----- orders -----

/** Wire: POST /v1/orders body (OrderRequest). */
export interface NewOrder {
  marketId: MarketId;
  outcome: OutcomeSide;
  side: OrderSide;
  price: number | null;
  size: string;
  type: OrderType;
}

/** Wire: OrderView. Filled size = size - remaining. */
export interface Order {
  orderId: string;
  marketId: MarketId;
  outcome: OutcomeSide;
  side: OrderSide;
  size: string;
  remaining: string;
  price: number | null;
  type: OrderType;
  status: OrderStatus;
  createdAt: string;
}

// ----- trades -----

/** Wire: trades delta item (WS Fills frame) or REST trade record
 *  ({tradeId, tradeClass, size, yesBasisTick, batchId, at: unixSec}). */
export interface FillView {
  tradeId?: string;
  marketId?: MarketId;
  tradeClass?: string;
  outcome?: string | null; // delta only, lowercase; NO => yesBasis = 1000 - outcomeTick
  outcomeTick?: number; // delta only, on the outcome basis
  yesBasisTick?: number; // REST form
  size: string;
  at?: number | string; // REST: unix seconds; mock: ISO
  txHash?: string;
}

/** Internal trade form. The trades:<mkt> channel pushes a LIST per frame. */
export interface Trade {
  marketId: MarketId;
  yesBasisTick: number;
  size: string;
  at: string; // ISO
  txHash?: string;
}

// ----- RFM -----

/**
 * Wire: RfmView (REST /v1/rfm/requests[/:id]) and the FLAT full-state rfm WS
 * frame (WsHub BuildRfmAsync). Differences from the internal RfmRequest:
 * `market` (not marketHash), `escrowAmount` (not escrow), no `bond`, lowercase
 * phase/side, numeric-string commitCount/maxPriceTick, unix-seconds deadlines,
 * nested `born` (not bornMarketId). The WS frame omits questionText,
 * resolutionSource, closeTime and fills (REST only).
 */
export interface RfmView {
  requestId: string;
  market?: string;
  questionText?: string | null;
  resolutionSource?: string | null;
  closeTime?: string | null; // unix-seconds string
  side?: string; // lowercase
  quantity?: string;
  maxPriceTick?: string; // numeric string
  minMatch?: string;
  commitDeadline?: string; // unix-seconds string
  revealDeadline?: string; // unix-seconds string
  escrowAmount?: string;
  minQuoteSize?: string;
  commitCount?: string; // numeric string
  phase?: string; // lowercase
  born?: { marketId: MarketId; marginalYesTick: number; vwapYesTick: number; filled?: string | null } | null;
  reveals?: { mm: Address; tick: string; size: string; inRange: boolean }[];
  fills?: { mm: Address; tick: string; size: string }[]; // REST only
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
  txHash?: string; // G6: the postRequest tx
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

/** Wire: POST /v1/rfm/requests response (G6). */
export interface NewRfmResponse {
  requestId: RequestId;
  txHash: string;
}

/** Wire: optional auction duration on POST /v1/rfm/requests (total; server splits 2/3 commit, 1/3 reveal). */
export type RfmDuration = "1m" | "15m" | "1h" | "4h" | "24h";

export interface NewRfmRequest {
  questionText: string;
  resolutionSource: string;
  closeTime: string;
  side: OutcomeSide;
  quantity: string;
  minMatch: string;
  maxPriceTick: number;
  duration?: RfmDuration;
}

// ----- tx status -----

export type TxState = "pending" | "confirmed" | "reverted";

/** Wire: GET /v1/tx/:hash/status -> {txHash, status}. */
export interface TxView {
  txHash: string;
  status: TxState;
}

export interface TxStatus {
  hash: string;
  status: TxState;
}

// ----- WS -----

/**
 * Wire envelope: field `type` ("snapshot" or a semantic delta name like "book",
 * "trades", "rfm", "user") plus (generation, seq, prevSeq) for gap detection.
 */
export interface WsEvent {
  channel: string;
  type: string;
  generation: number;
  seq: number;
  prevSeq?: number;
  data: unknown;
}
