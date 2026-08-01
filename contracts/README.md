# contracts/ — RFM on Arc (Solidity, Foundry)

Four contracts. Custody is isolated from settlement so a broken exchange can never freeze exit.

| Contract | Role |
|---|---|
| `Vault` | Custody + all physical asset movement. Permissionless `withdraw`/`redeem` — nothing can freeze exit |
| `CTFExchangeLite` | Settlement router: `settleBatch` over TRANSFER / MINT / MERGE. Holds no funds |
| `OutcomeTokens` | CTF-lite ERC-1155 + collateral pool. `split`/`merge` callable only by Vault |
| `RFM` | The auction: request → sealed commit-reveal quotes + bonds → deadline match → market born |

Status: scaffolding. Spec: `../../PLAN_CONTRACTS.md` (FINAL, two-round audited).
