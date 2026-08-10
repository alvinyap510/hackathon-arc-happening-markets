// useSettlementToast lifecycle tests (PLAN_PROVENANCE_POLISH v3 B.7b, CR-sealed
// matrix). Renders the hook via a probe component in jsdom; the api is a fake whose
// subscribe records the listener so tests can push frames and observe unsubscribes.
// Mutation bar: deleting the unsubscribe or the status gate must fail these tests.
// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { useSettlementToast } from "./useSettlementToast";
import type { VenueApi } from "./api";
import type { WsEvent } from "./types";

type Handlers = { placed: (ids: string[]) => void; failed: () => void };

function makeFakeApi() {
  const listeners = new Set<(ev: WsEvent) => void>();
  let unsubs = 0;
  const api = {
    subscribe(_c: string, cb: (ev: WsEvent) => void) {
      listeners.add(cb);
      return () => {
        listeners.delete(cb);
        unsubs++;
      };
    },
  } as unknown as VenueApi;
  const push = (data: unknown) =>
    act(() => {
      for (const cb of [...listeners]) cb({ channel: "trades:m", type: "settlement", generation: 1, seq: 1, data });
    });
  return { api, push, count: () => listeners.size, unsubs: () => unsubs };
}

let root: Root;
let host: HTMLDivElement;
let hookRef: { current: ReturnType<typeof useSettlementToast> | null };

function Probe({ api }: { api: VenueApi }) {
  const h = useSettlementToast(api, "m");
  hookRef.current = h;
  return h.toast ? <div data-testid="toast">{h.toast.message}|{h.toast.href}</div> : null;
}

function mount(api: VenueApi) {
  host = document.createElement("div");
  document.body.appendChild(host);
  root = createRoot(host);
  act(() => root.render(<Probe api={api} />));
}

const frame = (over: Record<string, unknown> = {}) => ({
  status: "confirmed",
  tradeIds: ["t1"],
  txHash: "0xsettle",
  ...over,
});

beforeEach(() => {
  vi.useFakeTimers();
  hookRef = { current: null };
});
afterEach(() => {
  act(() => root.unmount());
  host.remove();
  vi.useRealTimers();
});

describe("useSettlementToast lifecycle", () => {
  it("buffered-before-response frame matches once the response supplies own ids", () => {
    const f = makeFakeApi();
    mount(f.api);
    let h!: Handlers;
    act(() => { h = hookRef.current!.arm(); });
    f.push(frame()); // arrives BEFORE the POST response
    expect(host.querySelector('[data-testid="toast"]')).toBeNull();
    act(() => h.placed(["t1"]));
    expect(host.textContent).toContain("A fill settled on-chain");
    expect(host.textContent).toContain("0xsettle");
    expect(f.unsubs()).toBe(1); // cleaned up after match
  });

  it("live frame after the response matches; foreign tradeIds are ignored", () => {
    const f = makeFakeApi();
    mount(f.api);
    let h!: Handlers;
    act(() => { h = hookRef.current!.arm(); });
    act(() => h.placed(["mine"]));
    f.push(frame({ tradeIds: ["theirs"] }));
    expect(host.querySelector('[data-testid="toast"]')).toBeNull();
    f.push(frame({ tradeIds: ["theirs", "mine"] }));
    expect(host.textContent).toContain("A fill settled on-chain");
  });

  it("status gate: unconfirmed or hash-less frames never toast (even with own ids)", () => {
    const f = makeFakeApi();
    mount(f.api);
    let h!: Handlers;
    act(() => { h = hookRef.current!.arm(); });
    act(() => h.placed(["t1"]));
    f.push(frame({ status: "reverted" }));
    f.push(frame({ txHash: null }));
    expect(host.querySelector('[data-testid="toast"]')).toBeNull();
    expect(f.unsubs()).toBe(0); // still watching until timeout
  });

  it("first match fires exactly one toast (second confirmed frame ignored after cleanup)", () => {
    const f = makeFakeApi();
    mount(f.api);
    let h!: Handlers;
    act(() => { h = hookRef.current!.arm(); });
    act(() => h.placed(["t1"]));
    f.push(frame());
    f.push(frame({ txHash: "0xother" }));
    expect(host.textContent).toContain("0xsettle");
    expect(host.textContent).not.toContain("0xother");
    expect(f.unsubs()).toBe(1);
  });

  it("POST error -> immediate cleanup", () => {
    const f = makeFakeApi();
    mount(f.api);
    let h!: Handlers;
    act(() => { h = hookRef.current!.arm(); });
    act(() => h.failed());
    expect(f.unsubs()).toBe(1);
    expect(f.count()).toBe(0);
  });

  it("zero-fill placement (pure rest) -> immediate cleanup, no toast", () => {
    const f = makeFakeApi();
    mount(f.api);
    let h!: Handlers;
    act(() => { h = hookRef.current!.arm(); });
    act(() => h.placed([]));
    expect(f.unsubs()).toBe(1);
    f.push(frame());
    expect(host.querySelector('[data-testid="toast"]')).toBeNull();
  });

  it("timeout -> cleanup, no toast", () => {
    const f = makeFakeApi();
    mount(f.api);
    act(() => { hookRef.current!.arm(); });
    act(() => vi.advanceTimersByTime(61_000));
    expect(f.unsubs()).toBe(1);
    expect(host.querySelector('[data-testid="toast"]')).toBeNull();
  });

  it("unmount -> unsubscribe", () => {
    const f = makeFakeApi();
    mount(f.api);
    act(() => { hookRef.current!.arm(); });
    act(() => root.unmount());
    expect(f.unsubs()).toBe(1);
    root = createRoot(host); // afterEach unmounts again safely
  });
});
