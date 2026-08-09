import { useState } from "react";
import Layout, { type Tab } from "./components/Layout";
import Login from "./components/Login";
import MarketsPage from "./pages/MarketsPage";
import RfmPage from "./pages/RfmPage";
import FaucetPage from "./pages/FaucetPage";
import { StoreProvider, useStore } from "./lib/store";

function Shell() {
  const { session, refreshMarkets } = useStore();
  const [tab, setTab] = useState<Tab>("markets");
  const [selectedMarket, setSelectedMarket] = useState<string | null>(null);

  if (!session) return <Login />;

  const openMarket = (id: string) => {
    setSelectedMarket(id);
    setTab("markets");
    void refreshMarkets();
  };

  return (
    <Layout
      tab={tab}
      onTab={(t) => {
        setTab(t);
        if (t === "markets") {
          setSelectedMarket(null);
          // always refetch on entering the tab: a market born while the user was
          // on another tab must appear without a page reload.
          void refreshMarkets();
        }
      }}
    >
      {tab === "markets" && <MarketsPage selected={selectedMarket} onSelect={setSelectedMarket} />}
      {tab === "rfm" && <RfmPage onOpenMarket={openMarket} />}
      {tab === "faucet" && <FaucetPage />}
    </Layout>
  );
}

export default function App() {
  return (
    <StoreProvider>
      <Shell />
    </StoreProvider>
  );
}
