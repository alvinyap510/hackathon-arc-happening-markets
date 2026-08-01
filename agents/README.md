# agents/ — market-maker agent (Agentic-track entry)

An autonomous market maker with a real on-chain identity. Registers via ERC-8004; answers each
Request-for-Market as an ERC-8183 job (accept → commit → reveal → completion gated on a valid
reveal); quotes both sides of the public book after the market is born. A customer of the venue,
never part of it.

Status: scaffolding.
