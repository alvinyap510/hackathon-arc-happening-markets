// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {IOutcomeTokens} from "./IOutcomeTokens.sol";

/// @title IVault
/// @notice Custody + all physical asset movement (spec section 1).
interface IVault {
    enum FundingKind { LOCK, FREE }

    struct Allocation {
        address account;
        uint256 amount;
    }

    struct Funding {
        FundingKind kind;
        bytes32 ref;
        address account;
        uint256 amount;
    }

    event Deposited(address indexed user, uint256 amt);
    event Withdrawn(address indexed user, uint256 amt);
    event TokensDeposited(address indexed user, uint256 indexed id, uint256 amt);
    event TokensWithdrawn(address indexed user, uint256 indexed id, uint256 amt);
    event USDCMoved(address indexed from, address indexed to, uint256 amt, bytes32 indexed tradeId);
    event TokensMoved(address indexed from, address indexed to, uint256 id, uint256 amt, bytes32 indexed tradeId);
    event Locked(bytes32 indexed ref, address indexed user, uint256 amt);
    event LockReleased(bytes32 indexed ref, address indexed user, uint256 amt);
    event LockConsumed(bytes32 indexed ref, address indexed user, uint256 amt, address indexed to);
    event PairMinted(bytes32 indexed marketId, Allocation[] yesAlloc, Allocation[] noAlloc, Funding[] funding, uint256 size);
    event PairBurned(bytes32 indexed marketId, address indexed yesFrom, address indexed noFrom, uint256 size, uint256 yesCredit);
    event Redeemed(address indexed user, bytes32 indexed marketId, uint256 amt);

    // User surface (permissionless).
    function deposit(uint256 amt) external;
    function withdraw(uint256 amt) external;
    function depositTokens(uint256 id, uint256 amt) external;
    function withdrawTokens(uint256 id, uint256 amt) external;
    function redeem(bytes32 marketId, uint256 amt) external;

    // Authorized primitives (exchange / rfm only).
    function moveUSDC(address from, address to, uint256 amt, bytes32 tradeId) external;
    function moveTokens(address from, address to, uint256 id, uint256 amt, bytes32 tradeId) external;
    function lock(address user, uint256 amt, bytes32 ref) external;
    function releaseLock(bytes32 ref, uint256 amt) external;
    function consumeLock(bytes32 ref, uint256 amt, address to) external;
    function mintPair(bytes32 marketId, Allocation[] calldata yesAlloc, Allocation[] calldata noAlloc, Funding[] calldata funding, uint256 size)
        external;
    function burnPair(bytes32 marketId, address yesFrom, address noFrom, uint256 size, uint256 yesCredit) external;

    // Views.
    function usdcBal(address user) external view returns (uint256);
    function lockedBal(address user) external view returns (uint256);
    function freeBal(address user) external view returns (uint256);
    function tokenBal(address user, uint256 id) external view returns (uint256);

    /// @dev Convenience so callers share the OutcomeTokens token-id derivation.
    function outcomeTokens() external view returns (IOutcomeTokens);
}
