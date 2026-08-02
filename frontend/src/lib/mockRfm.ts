// Scripted sealed-bid auction for mock mode: OPEN -> COMMIT -> REVEAL -> FINALIZED -> born.
// Compressed timeline so the whole mechanism is visible standalone in about 35 seconds.
// The on-stage preset (2 min commit / 1 min reveal) is a backend concern; timing here is UI-only.

import type { MockVenue } from "./mockVenue";
import type { NewRfmRequest, RequestId, RfmFill, RfmFinal, RfmRequest, RfmReveal } from "./types";

export const MOCK_COMMIT_MS = 20_000;
export const MOCK_REVEAL_MS = 14_000;

const MMS = [
  "0x1a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c4d",
  "0x9f8e7d6c5b4a39281f0e9d8c7b6a59483726f1e0",
  "0x55aa66bb77cc88dd99ee00ff11aa22bb33cc44dd",
  "0xdeadbeef00112233445566778899aabbccddeeff",
  "0x0123456789abcdef0123456789abcdef01234567",
];

export interface RfmRuntime {
  request: RfmRequest;
  reveals: RfmReveal[];
  final: RfmFinal | null;
  bornMarketId: string | null;
  timers: ReturnType<typeof setTimeout>[];
}

let reqSeq = 0;

export class RfmManager {
  runtimes = new Map<RequestId, RfmRuntime>();

  constructor(private venue: MockVenue) {}

  list(): RfmRequest[] {
    return [...this.runtimes.values()].map((r) => r.request).sort((a, b) => b.requestId.localeCompare(a.requestId));
  }

  get(id: RequestId): RfmRuntime | undefined {
    return this.runtimes.get(id);
  }

  /** Launch a fully scripted auction. startOffsetMs<0 means "already running" (pre-staged). */
  launch(req: NewRfmRequest, elapsedMs = 0): RfmRuntime {
    const now = Date.now();
    const requestId = `req-${(++reqSeq).toString().padStart(3, "0")}`;
    const commitEnds = now - elapsedMs + MOCK_COMMIT_MS;
    const revealEnds = commitEnds + MOCK_REVEAL_MS;
    const escrow = ((BigInt(req.quantity) * BigInt(req.maxPriceTick)) / 1000n).toString();
    const request: RfmRequest = {
      requestId,
      marketHash: `0x${Math.random().toString(16).slice(2).padEnd(64, "0").slice(0, 64)}`,
      questionText: req.questionText,
      resolutionSource: req.resolutionSource,
      closeTime: req.closeTime,
      side: req.side,
      quantity: req.quantity,
      minMatch: req.minMatch,
      maxPriceTick: req.maxPriceTick,
      escrow,
      bond: "500000000",
      phase: "COMMIT",
      commitDeadline: new Date(commitEnds).toISOString(),
      revealDeadline: new Date(revealEnds).toISOString(),
      commitCount: 0,
    };
    const rt: RfmRuntime = { request, reveals: [], final: null, bornMarketId: null, timers: [] };
    this.runtimes.set(requestId, rt);
    const ch = `rfm:${requestId}`;

    // commits: 5 MMs arrive staggered through the commit window
    const commitAt = [0.08, 0.26, 0.45, 0.62, 0.8];
    commitAt.forEach((f, i) => {
      const at = commitEnds - MOCK_COMMIT_MS + f * MOCK_COMMIT_MS;
      const delay = Math.max(0, at - now);
      if (at <= now) {
        rt.request.commitCount = i + 1; // pre-staged: already committed
        return;
      }
      rt.timers.push(
        setTimeout(() => {
          rt.request.commitCount += 1;
          this.venue.emit(ch, { kind: "commit", count: rt.request.commitCount });
        }, delay),
      );
    });

    // reveal window opens
    rt.timers.push(
      setTimeout(() => {
        rt.request.phase = "REVEAL";
        this.venue.emit(ch, { kind: "phase", phase: "REVEAL" });
        this.scriptReveals(rt);
      }, Math.max(0, commitEnds - now)),
    );

    // finalize at the reveal deadline
    rt.timers.push(
      setTimeout(() => {
        this.finalize(rt);
      }, Math.max(0, revealEnds - now)),
    );

    if (elapsedMs > 0) this.venue.emit(ch, { kind: "commit", count: rt.request.commitCount });
    return rt;
  }

  private scriptReveals(rt: RfmRuntime): void {
    const ch = `rfm:${rt.request.requestId}`;
    const max = rt.request.maxPriceTick;
    // 3 valid quotes inside the cap, 1 out-of-range (slashes at finalize), 1 stays sealed
    const script: { atMs: number; reveal: RfmReveal }[] = [
      { atMs: 1200, reveal: { mm: MMS[0], priceTick: Math.max(50, max - 42), size: "400000000", valid: true } },
      { atMs: 3200, reveal: { mm: MMS[1], priceTick: Math.max(50, max - 18), size: "350000000", valid: true } },
      { atMs: 5600, reveal: { mm: MMS[2], priceTick: Math.max(50, max - 30), size: "300000000", valid: true } },
      { atMs: 8400, reveal: { mm: MMS[3], priceTick: max + 60, size: "200000000", valid: false } },
    ];
    for (const s of script) {
      rt.timers.push(
        setTimeout(() => {
          rt.reveals.push(s.reveal);
          this.venue.emit(ch, { kind: "reveal", reveal: s.reveal });
        }, s.atMs),
      );
    }
  }

  private finalize(rt: RfmRuntime): void {
    const ch = `rfm:${rt.request.requestId}`;
    const valid = [...rt.reveals].filter((r) => r.valid).sort((a, b) => a.priceTick - b.priceTick);
    const target = BigInt(rt.request.quantity);
    let filled = 0n;
    const fills: RfmFill[] = [];
    let cost = 0n;
    for (const r of valid) {
      if (filled >= target) break;
      const size = BigInt(r.size);
      const take = filled + size > target ? target - filled : size;
      fills.push({ mm: r.mm, priceTick: r.priceTick, size: take.toString() });
      cost += (take * BigInt(r.priceTick)) / 1000n;
      filled += take;
    }
    const minOk = filled >= BigInt(rt.request.minMatch);
    const slashCount = rt.request.commitCount - valid.length; // non-revealers + out-of-range
    if (!minOk) {
      rt.request.phase = "FAILED";
      this.venue.emit(ch, { kind: "phase", phase: "FAILED" });
      return;
    }
    const marginal = fills[fills.length - 1]?.priceTick ?? 0;
    const vwap = filled > 0n ? Number((cost * 1000n) / filled) : 0;
    rt.final = {
      requestId: rt.request.requestId,
      filledQty: filled.toString(),
      marginalTick: marginal,
      vwapTick: vwap,
      fills,
      slashCount,
      slashed: [{ mm: MMS[4], amount: "500000000" }, { mm: MMS[3], amount: "500000000" }],
    };
    rt.request.phase = "FINALIZED";
    this.venue.emit(ch, { kind: "final", final: rt.final });

    // the birth: market appears funded + priced at the auction marks
    rt.timers.push(
      setTimeout(() => {
        const yesMid = rt.request.side === "YES" ? vwap : 1000 - vwap;
        const market = this.venue.addMarket({
          marketId: `mkt-born-${rt.request.requestId}`,
          questionText: rt.request.questionText,
          resolutionSource: rt.request.resolutionSource,
          closeTime: rt.request.closeTime,
          status: "LIVE",
          bornFromRfm: true,
          birth: { marginalTick: marginal, vwapTick: vwap, filledQty: filled.toString() },
          mid: yesMid,
          midTick: yesMid,
          lastTradeTick: null,
        });
        rt.bornMarketId = market.marketId;
        this.venue.emit(ch, { kind: "born", marketId: market.marketId });
      }, 1500),
    );
  }
}
