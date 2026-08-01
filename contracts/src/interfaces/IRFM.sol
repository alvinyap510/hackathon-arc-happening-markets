// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {IOutcomeTokens} from "./IOutcomeTokens.sol";
import {IVault} from "./IVault.sol";

/// @title IRFM
/// @notice Auction phase machine surface (spec section 4).
interface IRFM {
    enum Side { YES, NO }

    enum Phase { OPEN, COMMIT, REVEAL, FINALIZED, FAILED, CANCELLED }

    event RequestPosted(
        uint256 indexed requestId,
        bytes32 indexed market,
        Side side,
        uint256 quantity,
        uint256 maxPriceTick,
        uint256 minMatch,
        uint256 commitDeadline,
        uint256 revealDeadline,
        uint256 escrowAmount,
        uint256 minQuoteSize
    );
    event QuoteCommitted(uint256 indexed requestId, address indexed mm, uint256 commitIndex);
    event QuoteRevealed(uint256 indexed requestId, address indexed mm, uint256 tick, uint256 size, bool inRange);
    event RfmFill(uint256 indexed requestId, address indexed mm, uint256 tick, uint256 size);
    event RequestFinalized(uint256 indexed requestId);
    event RequestFailed(uint256 indexed requestId);
    event RequestCancelled(uint256 indexed requestId);
    event BondSlashed(uint256 indexed requestId, address indexed mm, address indexed to);
    event MarketReserved(bytes32 indexed marketId, uint256 indexed requestId);
    event MarketBorn(
        uint256 indexed requestId,
        bytes32 indexed marketId,
        uint256 marginalYesTick,
        uint256 vwapYesTick,
        uint256 filledQuantity,
        Side side
    );

    function postRequest(
        bytes32 market,
        Side side,
        uint256 quantity,
        uint256 maxPriceTick,
        uint256 minMatch,
        uint256 commitDeadline,
        uint256 revealDeadline
    ) external returns (uint256 requestId);

    function commitQuote(uint256 requestId, bytes32 commitHash) external;
    function revealQuote(uint256 requestId, uint256 priceTick, uint256 size, uint256 salt) external;
    function finalize(uint256 requestId) external;
    function cancel(uint256 requestId) external;

    function phase(uint256 requestId) external view returns (Phase);
    function finalizeReady(uint256 requestId) external view returns (bool);
    function marketIdOf(uint256 requestId) external view returns (bytes32);
    function quoteHash(uint256 requestId, address mm, uint256 priceTick, uint256 size, uint256 salt)
        external
        view
        returns (bytes32);

    function vault() external view returns (IVault);
    function outcomeTokens() external view returns (IOutcomeTokens);
}
