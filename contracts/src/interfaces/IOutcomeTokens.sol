// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

/// @title IOutcomeTokens
/// @notice CTF-lite binary conditional tokens + collateral pool (spec section 3).
interface IOutcomeTokens {
    enum Outcome { YES, NO }

    struct Allocation {
        address account;
        uint256 amount;
    }

    /// @notice RFM-only. Unforgeable-by-race reservation of a derived market id.
    function reserveMarket(bytes32 marketId) external;

    /// @notice RFM (during finalize, before split) or operator. Sets exists = true.
    function createMarket(bytes32 marketId, bytes calldata meta) external;

    /// @notice Vault-only. Pulls `size` USDC into the pool, mints size YES + size NO.
    function split(bytes32 marketId, uint256 size) external;

    /// @notice Vault-only. Burns a YES + NO pair, releases `size` USDC from the pool.
    function merge(bytes32 marketId, uint256 size) external;

    /// @notice Operator-only, one-shot. Sets resolved + winningOutcome.
    function resolve(bytes32 marketId, Outcome outcome) external;

    /// @notice Permissionless post-resolve exit for wallet-held tokens.
    function redeem(bytes32 marketId, uint256 amt) external;

    function tokenId(bytes32 marketId, Outcome outcome) external pure returns (uint256);

    function isResolved(bytes32 marketId) external view returns (bool);

    function winningOutcome(bytes32 marketId) external view returns (Outcome);

    function markets(bytes32 marketId)
        external
        view
        returns (bool reserved, bool exists, bool resolved, Outcome winningOutcome, uint256 collateralPool);

    function physicalUsdc() external view returns (uint256);

    function totalSupply(uint256 id) external view returns (uint256);
}
