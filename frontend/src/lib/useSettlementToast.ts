// Transient settlement watcher (PLAN_PROVENANCE_POLISH v3 B.7): after a placement
// with fills, watch the market's trades channel for the settlement frame that
// contains one of OUR tradeIds and surface the on-chain receipt as a toast.
//
// Lifecycle contract (CR-sealed): the subscription is created BEFORE the POST so a
// fast settlement frame cannot be missed; frames are buffered until the placement
// response supplies the order's own tradeIds; the FIRST frame with
// status==="confirmed" && txHash covering an own tradeId fires exactly one toast;
// cleanup (unsubscribe + timer) on match, POST error, zero-fill placement, ~60s
// timeout, or unmount. A quiet timeout shows nothing: absence of a frame is not
// evidence of success or failure.
import { useCallback, useEffect, useRef, useState } from "react";
import type { VenueApi } from "./api";
import type { WsEvent } from "./types";
import { txUrl } from "./format";
import type { ToastData } from "../components/Toast";

interface SettlementFrame {
  status?: string;
  tradeIds?: string[];
  txHash?: string | null;
}

const TIMEOUT_MS = 60_000;

export function useSettlementToast(api: VenueApi, marketId: string) {
  const [toast, setToast] = useState<ToastData | null>(null);
  const watch = useRef<{
    unsub: () => void;
    timer: ReturnType<typeof setTimeout>;
    ownIds: Set<string> | null; // null until the POST response arrives
    buffer: SettlementFrame[];
    done: boolean;
  } | null>(null);

  const cleanup = useCallback(() => {
    const w = watch.current;
    if (!w) return;
    w.done = true;
    clearTimeout(w.timer);
    w.unsub();
    watch.current = null;
  }, []);

  useEffect(() => cleanup, [cleanup]); // unmount

  const tryMatch = useCallback((frame: SettlementFrame) => {
    const w = watch.current;
    if (!w || w.done) return;
    if (w.ownIds === null) {
      w.buffer.push(frame); // response not in yet: buffer
      return;
    }
    if (frame.status !== "confirmed" || !frame.txHash) return; // status-gated
    if (!frame.tradeIds?.some((id) => w.ownIds!.has(id))) return; // own fills only
    setToast({ message: "A fill settled on-chain", href: txUrl(frame.txHash), linkLabel: "view tx" });
    cleanup();
  }, [cleanup]);

  /** Call BEFORE the POST. Returns handlers to feed the placement outcome. */
  const arm = useCallback(() => {
    cleanup(); // one watcher at a time
    const unsub = api.subscribe(`trades:${marketId}`, (ev: WsEvent) => {
      if (ev.type !== "settlement" || !ev.data || typeof ev.data !== "object") return;
      tryMatch(ev.data as SettlementFrame);
    });
    const timer = setTimeout(cleanup, TIMEOUT_MS); // quiet timeout: no toast
    watch.current = { unsub, timer, ownIds: null, buffer: [], done: false };
    return {
      /** Placement succeeded: supply the order's own tradeIds (empty -> cleanup). */
      placed(tradeIds: string[]) {
        const w = watch.current;
        if (!w || w.done) return;
        if (tradeIds.length === 0) return cleanup(); // pure rest: nothing settles
        w.ownIds = new Set(tradeIds);
        const buffered = w.buffer.splice(0);
        for (const f of buffered) tryMatch(f);
      },
      /** Placement failed: nothing to watch. */
      failed: cleanup,
    };
  }, [api, marketId, cleanup, tryMatch]);

  return { toast, dismiss: () => setToast(null), arm };
}
