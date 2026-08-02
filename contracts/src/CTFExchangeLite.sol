// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";

import {IOutcomeTokens} from "./interfaces/IOutcomeTokens.sol";
import {IVault} from "./interfaces/IVault.sol";

/// @title CTFExchangeLite
/// @notice Settlement router over the Vault. Holds no funds and stores only batch
///         state (usedTradeIds / usedBatchIds). `settleBatch` is operator-only,
///         whole-batch atomic: any invalid trade reverts the ENTIRE batch with a
///         custom error carrying the failing index + tradeId.
/// @dev Spec: PLAN_CONTRACTS.md section 2.
contract CTFExchangeLite is ReentrancyGuard {
    IVault public immutable vault;
    IOutcomeTokens public immutable outcomeTokens;
    address public immutable operator;

    uint256 public constant MAX_BATCH = 8;

    enum TradeClass { TRANSFER, MINT, MERGE }

    struct Trade {
        bytes32 tradeId;
        bytes32 marketId;
        TradeClass class;
        IOutcomeTokens.Outcome outcome; // TRANSFER only
        address partyA; // TRANSFER: seller; MINT/MERGE: yes party
        address partyB; // TRANSFER: buyer; MINT/MERGE: no party
        uint256 outcomeTick; // TRANSFER: outcome price; MINT/MERGE: yes tick
        uint256 size;
    }

    mapping(bytes32 => bool) public usedTradeIds;
    mapping(bytes32 => bool) public usedBatchIds;

    event BatchSettled(bytes32 indexed batchId, bytes32[] tradeIds);

    error NotOperator();
    error BatchReused(bytes32 batchId);
    error BatchTooLarge(uint256 len);
    error EmptyBatch();
    error SettleBatchFailed(uint256 index, bytes32 tradeId);

    constructor(address vault_, address outcomeTokens_, address operator_) {
        vault = IVault(vault_);
        outcomeTokens = IOutcomeTokens(outcomeTokens_);
        operator = operator_;
    }

    function settleBatch(bytes32 batchId, Trade[] calldata trades) external nonReentrant {
        if (msg.sender != operator) revert NotOperator();
        if (usedBatchIds[batchId]) revert BatchReused(batchId);
        if (trades.length == 0) revert EmptyBatch();
        if (trades.length > MAX_BATCH) revert BatchTooLarge(trades.length);

        bytes32[] memory settled = new bytes32[](trades.length);
        for (uint256 i = 0; i < trades.length; ++i) {
            Trade calldata t = trades[i];
            if (usedTradeIds[t.tradeId]) revert SettleBatchFailed(i, t.tradeId);
            if (t.size == 0) revert SettleBatchFailed(i, t.tradeId);

            if (t.class == TradeClass.TRANSFER) {
                _settleTransfer(t, i);
            } else if (t.class == TradeClass.MINT) {
                _settleMint(t, i);
            } else {
                _settleMerge(t, i);
            }
            usedTradeIds[t.tradeId] = true;
            settled[i] = t.tradeId;
        }

        usedBatchIds[batchId] = true;
        emit BatchSettled(batchId, settled);
    }

    // ------------------------------------------------------------------ trades

    function _settleTransfer(Trade calldata t, uint256 i) internal {
        if (t.outcomeTick > 1000) revert SettleBatchFailed(i, t.tradeId);
        address seller = t.partyA;
        address buyer = t.partyB;
        uint256 cost = (t.size * t.outcomeTick) / 1000;
        uint256 id = outcomeTokens.tokenId(t.marketId, t.outcome);
        if (vault.tokenBal(seller, id) < t.size) revert SettleBatchFailed(i, t.tradeId);
        // A tick-0 or micro-size transfer rounds the USDC leg to zero: the tokens
        // still move, the zero-cost USDC leg is a legitimate no-op (moveUSDC would
        // revert on ZeroAmount).
        if (cost > 0) {
            if (vault.freeBal(buyer) < cost) revert SettleBatchFailed(i, t.tradeId);
            vault.moveUSDC(buyer, seller, cost, t.tradeId);
        }
        vault.moveTokens(seller, buyer, id, t.size, t.tradeId);
    }

    function _settleMint(Trade calldata t, uint256 i) internal {
        if (t.outcomeTick > 1000) revert SettleBatchFailed(i, t.tradeId);
        address yesParty = t.partyA;
        address noParty = t.partyB;
        uint256 yesCost = (t.size * t.outcomeTick) / 1000;
        uint256 noCost = t.size - yesCost;
        if (vault.freeBal(yesParty) < yesCost) revert SettleBatchFailed(i, t.tradeId);
        if (vault.freeBal(noParty) < noCost) revert SettleBatchFailed(i, t.tradeId);

        IVault.Allocation[] memory yesAlloc = new IVault.Allocation[](1);
        yesAlloc[0] = IVault.Allocation(yesParty, t.size);
        IVault.Allocation[] memory noAlloc = new IVault.Allocation[](1);
        noAlloc[0] = IVault.Allocation(noParty, t.size);
        IVault.Funding[] memory funding = new IVault.Funding[](2);
        funding[0] = IVault.Funding(IVault.FundingKind.FREE, bytes32(0), yesParty, yesCost);
        funding[1] = IVault.Funding(IVault.FundingKind.FREE, bytes32(0), noParty, noCost);
        vault.mintPair(t.marketId, yesAlloc, noAlloc, funding, t.size);
    }

    function _settleMerge(Trade calldata t, uint256 i) internal {
        if (t.outcomeTick > 1000) revert SettleBatchFailed(i, t.tradeId);
        address yesParty = t.partyA;
        address noParty = t.partyB;
        uint256 yesCost = (t.size * t.outcomeTick) / 1000;
        uint256 yesId = outcomeTokens.tokenId(t.marketId, IOutcomeTokens.Outcome.YES);
        uint256 noId = outcomeTokens.tokenId(t.marketId, IOutcomeTokens.Outcome.NO);
        if (vault.tokenBal(yesParty, yesId) < t.size) revert SettleBatchFailed(i, t.tradeId);
        if (vault.tokenBal(noParty, noId) < t.size) revert SettleBatchFailed(i, t.tradeId);
        vault.burnPair(t.marketId, yesParty, noParty, t.size, yesCost);
    }
}
