// App state: one provider owning the VenueApi + session + core slices, plus
// per-channel subscription hooks that assemble local state from snapshot+delta.

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import { createApi, type VenueApi } from "./api";
import type {
  Balances,
  Book,
  Market,
  Order,
  RfmFinal,
  RfmRequest,
  RfmReveal,
  Session,
  Trade,
  WsEvent,
} from "./types";

interface Store {
  api: VenueApi;
  session: Session | null;
  balances: Balances | null;
  markets: Market[];
  bornPulse: number;
  wallet: string | null; // mock mode only
  login(email: string): Promise<void>;
  refreshBalances(): Promise<void>;
  refreshMarkets(): Promise<void>;
  notifyBorn(): void;
}

const Ctx = createContext<Store | null>(null);

export function StoreProvider({ children }: { children: ReactNode }) {
  const [api, setApi] = useState<VenueApi | null>(null);
  const [session, setSession] = useState<Session | null>(null);
  const [balances, setBalances] = useState<Balances | null>(null);
  const [markets, setMarkets] = useState<Market[]>([]);
  const [bornPulse, setBornPulse] = useState(0);
  const [wallet, setWallet] = useState<string | null>(null);

  useEffect(() => {
    void createApi().then(setApi);
  }, []);

  const refreshBalances = useCallback(async () => {
    if (!api) return;
    const b = await api.getBalances();
    setBalances(b);
    setWallet(b.wallet ?? null);
  }, [api]);

  const refreshMarkets = useCallback(async () => {
    if (!api) return;
    setMarkets(await api.listMarkets());
  }, [api]);

  const login = useCallback(
    async (email: string) => {
      if (!api) return;
      const s = await api.login(email);
      setSession(s);
      await Promise.all([refreshBalances(), refreshMarkets()]);
    },
    [api, refreshBalances, refreshMarkets],
  );

  // initial market load once the api exists (markets render even pre-login)
  useEffect(() => {
    if (api) void refreshMarkets();
  }, [api, refreshMarkets]);

  // user channel: balances move on fills, orders, deposits
  useEffect(() => {
    if (!api || !session) return;
    return api.subscribe(`user:${session.address}`, () => void refreshBalances());
  }, [api, session, refreshBalances]);

  const value = useMemo<Store | null>(() => {
    if (!api) return null;
    return {
      api,
      session,
      balances,
      markets,
      bornPulse,
      wallet,
      login,
      refreshBalances,
      refreshMarkets,
      notifyBorn: () => {
        setBornPulse((n) => n + 1);
        void refreshMarkets();
      },
    };
  }, [api, session, balances, markets, bornPulse, wallet, login, refreshBalances, refreshMarkets]);

  if (!value) return null;
  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useStore(): Store {
  const s = useContext(Ctx);
  if (!s) throw new Error("store not ready");
  return s;
}

/** Subscribe to a channel for the lifetime of the component; handler gets every event. */
export function useChannel(channel: string | null, onEvent: (ev: WsEvent) => void): void {
  const { api } = useStore();
  const cb = useRef(onEvent);
  cb.current = onEvent;
  useEffect(() => {
    if (!channel) return;
    return api.subscribe(channel, (ev) => cb.current(ev));
  }, [api, channel]);
}

export function useBook(marketId: string | null): Book | null {
  const [book, setBook] = useState<Book | null>(null);
  useChannel(marketId ? `book:${marketId}` : null, (ev) => {
    // mock and real both deliver full-book frames (snapshot or delta)
    setBook(ev.data as Book);
  });
  return book;
}

export function useTrades(marketId: string | null): Trade[] {
  const [trades, setTrades] = useState<Trade[]>([]);
  useChannel(marketId ? `trades:${marketId}` : null, (ev) => {
    if (ev.type === "snapshot") {
      setTrades(Array.isArray(ev.data) ? (ev.data as Trade[]) : []);
      return;
    }
    // the trades channel pushes a LIST per frame (INTEGRATION_CONTRACT), but
    // settlement/rejection frames carry a non-array payload: skip those
    if (Array.isArray(ev.data)) setTrades((t) => [...(ev.data as Trade[]), ...t].slice(0, 50));
  });
  return trades;
}

export interface RfmState {
  request: RfmRequest | null;
  reveals: RfmReveal[];
  final: RfmFinal | null;
  bornMarketId: string | null;
}

export function useRfm(requestId: string | null): RfmState {
  const [state, setState] = useState<RfmState>({ request: null, reveals: [], final: null, bornMarketId: null });
  const { notifyBorn } = useStore();
  useChannel(requestId ? `rfm:${requestId}` : null, (ev) => {
    if (ev.type === "snapshot") {
      const d = ev.data as RfmState;
      setState(d ? { ...d, reveals: [...(d.reveals ?? [])] } : d);
      return;
    }
    const d = ev.data as
      | { kind: "commit"; count: number }
      | { kind: "phase"; phase: RfmRequest["phase"] }
      | { kind: "reveal"; reveal: RfmReveal }
      | { kind: "final"; final: RfmFinal }
      | { kind: "born"; marketId: string };
    setState((s) => {
      if (!s.request) return s;
      switch (d.kind) {
        case "commit":
          return { ...s, request: { ...s.request, commitCount: d.count } };
        case "phase":
          return { ...s, request: { ...s.request, phase: d.phase } };
        case "reveal":
          return { ...s, reveals: [...s.reveals, d.reveal] };
        case "final":
          return { ...s, request: { ...s.request, phase: "FINALIZED" }, final: d.final };
        case "born":
          return { ...s, bornMarketId: d.marketId };
      }
    });
    if (d.kind === "born") notifyBorn();
  });
  return state;
}

export function useOpenOrders(): { orders: Order[]; reload: () => Promise<void> } {
  const { api, session } = useStore();
  const [orders, setOrders] = useState<Order[]>([]);
  const reload = useCallback(async () => {
    if (session) setOrders(await api.listOpenOrders());
  }, [api, session]);
  useChannel(session ? `user:${session.address}` : null, () => void reload());
  useEffect(() => void reload(), [reload]);
  return { orders, reload };
}
