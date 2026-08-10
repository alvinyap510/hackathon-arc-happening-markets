// placeWithWatcher boundary tests (code-CR round 2, both seats):
// 1. successful POST + failed refresh -> watcher NOT failed, honest copy, no "rejected"
// 2. synchronously-throwing arm() -> no strand (error returned), no watcher.failed()
// 3. POST rejection -> watcher.failed() exactly once, "rejected" copy
import { describe, expect, it, vi } from "vitest";
import { placeWithWatcher } from "./placeWithWatcher";
import type { VenueApi } from "./api";
import type { NewOrder } from "./types";

const order: NewOrder = { marketId: "m", outcome: "YES", side: "BUY", price: 500, size: "1000000", type: "LIMIT" };
const okResult = { orderId: "o", status: "filled", size: "1000000", remaining: "0", fills: [{ tradeId: "t1", tradeClass: "transfer", size: "1000000", priceTick: 500 }] };

function makeWatcher() {
  return { placed: vi.fn(), failed: vi.fn() };
}

describe("placeWithWatcher boundaries", () => {
  it("successful POST + failing refresh: watcher.placed called, failed NOT called, honest copy", async () => {
    const w = makeWatcher();
    const api = { placeOrder: vi.fn().mockResolvedValue(okResult) } as unknown as VenueApi;
    const { error } = await placeWithWatcher(api, () => w, order, () => Promise.reject(new Error("balances 502")));
    expect(w.placed).toHaveBeenCalledWith(["t1"]);
    expect(w.failed).not.toHaveBeenCalled();
    expect(error).toContain("Order placed");
    expect(error).not.toContain("rejected");
  });

  it("synchronously-throwing arm: returns an error, never strands, no watcher.failed", async () => {
    const api = { placeOrder: vi.fn() } as unknown as VenueApi;
    const { error } = await placeWithWatcher(
      api,
      () => {
        throw new Error("ws construct failed");
      },
      order,
      () => Promise.resolve(),
    );
    expect(error).toBe("ws construct failed");
    expect((api.placeOrder as ReturnType<typeof vi.fn>)).not.toHaveBeenCalled();
  });

  it("POST rejection: watcher.failed exactly once, rejected copy, no placed", async () => {
    const w = makeWatcher();
    const api = { placeOrder: vi.fn().mockRejectedValue(new Error("insufficient_available")) } as unknown as VenueApi;
    const { error } = await placeWithWatcher(api, () => w, order, () => Promise.resolve());
    expect(w.failed).toHaveBeenCalledTimes(1);
    expect(w.placed).not.toHaveBeenCalled();
    expect(error).toBe("insufficient_available");
  });

  it("clean success: no error", async () => {
    const w = makeWatcher();
    const api = { placeOrder: vi.fn().mockResolvedValue(okResult) } as unknown as VenueApi;
    const { error } = await placeWithWatcher(api, () => w, order, () => Promise.resolve());
    expect(error).toBeNull();
    expect(w.placed).toHaveBeenCalledWith(["t1"]);
  });
});
