# contracts/ - RFM on Arc (Solidity, Foundry)

Four contracts. Custody is isolated from settlement so a broken exchange can never freeze exit.

| Contract | Role |
|---|---|
| `Vault` | Custody + all physical asset movement. Permissionless `withdraw`/`redeem` - nothing can freeze exit |
| `CTFExchangeLite` | Settlement router: `settleBatch` over TRANSFER / MINT / MERGE. Holds no funds |
| `OutcomeTokens` | CTF-lite ERC-1155 + collateral pool. `split`/`merge` callable only by Vault |
| `RFM` | The auction: request → sealed commit-reveal quotes + bonds → deadline match → market born |

Spec: `../../PLAN_CONTRACTS.md` (FINAL, two-round audited).

## Build & test

```bash
forge install        # deps (OpenZeppelin v5 + forge-std) - lib/ is gitignored
forge build
forge test           # 89 tests: units per contract + money-conservation + phase-aware supply invariants + multi-MM partial-fill RFM path
```

## Deploy (Arc testnet)

```bash
PRIVATE_KEY=... OPERATOR_ADDRESS=... forge script script/Deploy.s.sol --rpc-url https://rpc.testnet.arc.network --broadcast
# optional USDC_ADDRESS override; defaults to the canonical Arc ERC-20 face 0x3600...0000
```

Secrets live in env, never in git. The four contracts are wired via one-shot `setRoles`
(deployer-gated, frozen after deploy) because the vault/exchange/rfm/outcomeTokens edges
form a deploy-time cycle that constructor immutables cannot cover.
