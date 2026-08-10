// Orchestrates a placement with the settlement watcher lifecycle (CR-sealed
// boundaries): arm INSIDE the caller-visible failure boundary (a synchronously
// throwing subscribe must not strand UI state), watcher.failed() ONLY on POST
// rejection, watcher survives a post-success balance-refresh hiccup.
import type { VenueApi } from "./api";
import type { NewOrder } from "./types";

interface Watcher {
  placed(tradeIds: string[]): void;
  failed(): void;
}

export async function placeWithWatcher(
  api: VenueApi,
  arm: () => Watcher,
  order: NewOrder,
  refresh: () => Promise<void>,
): Promise<{ error: string | null }> {
  let watcher: Watcher | null = null;
  try {
    watcher = arm();
    let res;
    try {
      res = await api.placeOrder(order);
    } catch (e) {
      watcher.failed(); // placement-only failure boundary
      return { error: e instanceof Error ? e.message : "Order rejected" };
    }
    watcher.placed(res.fills.map((f) => f.tradeId));
    try {
      await refresh();
    } catch {
      return { error: "Order placed; balance refresh failed — it will catch up." };
    }
    return { error: null };
  } catch (e) {
    // pre-placement setup failure (e.g. subscribe threw): no watcher armed, no POST made
    return { error: e instanceof Error ? e.message : "Could not start the order" };
  }
}
