#!/bin/sh
# E2E deploy job: wait for anvil, ensure forge deps (lib/ is gitignored), run
# DeployE2E.s.sol on the real local chain, then write the backend env file.
set -e

echo "[e2e] waiting for anvil at $E2E_RPC ..."
until cast block-number --rpc-url "$E2E_RPC" >/dev/null 2>&1; do sleep 1; done
echo "[e2e] anvil up (block $(cast block-number --rpc-url "$E2E_RPC"))"

cd /app/contracts

# lib/ is gitignored, so first run installs it (needs network at compose-up time).
if [ ! -d lib/openzeppelin-contracts ]; then
  echo "[e2e] installing openzeppelin-contracts ..."
  forge install "openzeppelin/openzeppelin-contracts@v5.0.2" --no-git >/dev/null
fi
if [ ! -d lib/forge-std ]; then
  echo "[e2e] installing forge-std ..."
  forge install "foundry-rs/forge-std@v1.9.4" --no-git >/dev/null
fi

echo "[e2e] deploying contracts ..."
export PRIVATE_KEY="$DEPLOYER_KEY"
export OPERATOR_ADDRESS
export E2E_OUT_FILE="/app/contracts/addresses.json"
rm -f "$E2E_OUT_FILE"
forge script script/DeployE2E.s.sol --rpc-url "$E2E_RPC" --broadcast --private-key "$DEPLOYER_KEY"

echo "[e2e] copying addresses to shared volume ..."
cp "$E2E_OUT_FILE" "$E2E_OUT"

echo "[e2e] writing backend env ..."
/app/e2e/write-backend-env.sh

echo "[e2e] deploy complete"
