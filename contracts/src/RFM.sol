// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";

import {IOutcomeTokens} from "./interfaces/IOutcomeTokens.sol";
import {IRFM} from "./interfaces/IRFM.sol";
import {IVault} from "./interfaces/IVault.sol";

/// @title RFM
/// @notice Request-for-Market auction phase machine. Institutions post hedging
///         requests; market makers commit sealed quotes with a USDC bond, then
///         reveal; the engine matches best revealed quotes at the deadline and the
///         matched market is born pre-funded and pre-priced. Holds no assets; it
///         commands the Vault through narrow authorized primitives.
/// @dev Spec: PLAN_CONTRACTS.md section 4. All ticks are the price of the REQUESTED
///      outcome; conversion to canonical YES basis happens once in MarketBorn.
contract RFM is ReentrancyGuard, IRFM {
    IVault public immutable vault;
    IOutcomeTokens public immutable outcomeTokens;

    /// @notice 500 USDC (6-dec) symmetric bond: institution at post, MM at commit.
    uint256 public constant RFM_BOND = 500e6;
    uint256 public constant MAX_QUOTES = 32;

    struct Request {
        address requester;
        bytes32 market;
        Side side;
        uint256 quantity;
        uint256 maxPriceTick;
        uint256 minMatch;
        uint256 commitDeadline;
        uint256 revealDeadline;
        uint256 escrowAmount;
        uint256 minQuoteSize;
        uint256 commitCount;
        bool finalized;
        bool failed;
        bool cancelled;
    }

    struct Commit {
        bytes32 commitHash;
        uint256 commitIndex;
        bool hasCommitted;
    }

    struct Reveal {
        uint256 tick;
        uint256 size;
        uint256 lockedAmount;
        bool hasRevealed;
        bool inRange;
    }

    /// @dev Selected fills, grouped to keep stack depth bounded in finalize helpers.
    struct FillSet {
        address[] mm;
        uint256[] tick;
        uint256[] size;
        uint256 len;
    }

    /// @dev Gathered in-range revealed quotes, pre-sort.
    struct QuoteSet {
        address[] mm;
        uint256[] tick;
        uint256[] size;
        uint256[] idx;
        uint256 len;
    }

    mapping(uint256 => Request) public requests;
    mapping(uint256 => address[]) public mmList;
    mapping(uint256 => mapping(address => Commit)) public commits;
    mapping(uint256 => mapping(address => Reveal)) public reveals;
    uint256 public requestCount;

    constructor(address vault_, address outcomeTokens_) {
        vault = IVault(vault_);
        outcomeTokens = IOutcomeTokens(outcomeTokens_);
    }

    // ------------------------------------------------------------------ refs

    function escrowRef(uint256 requestId) internal pure returns (bytes32) {
        return keccak256(abi.encode(requestId, "ESCROW"));
    }

    function instBondRef(uint256 requestId) internal pure returns (bytes32) {
        return keccak256(abi.encode(requestId, "INSTBOND"));
    }

    function mmBondRef(uint256 requestId, address mm) internal pure returns (bytes32) {
        return keccak256(abi.encode(requestId, mm, "BOND"));
    }

    function mmRevealRef(uint256 requestId, address mm) internal pure returns (bytes32) {
        return keccak256(abi.encode(requestId, mm, "REVEAL"));
    }

    // ------------------------------------------------------------------ views

    function marketIdOf(uint256 requestId) public view returns (bytes32) {
        return keccak256(abi.encode(address(this), requestId));
    }

    function quoteHash(uint256 requestId, address mm, uint256 priceTick, uint256 size, uint256 salt)
        external
        view
        returns (bytes32)
    {
        return keccak256(abi.encode(block.chainid, address(this), requestId, mm, priceTick, size, salt));
    }

    /// @dev Derived phase. OPEN/COMMIT share the commit window; the distinction is
    ///      whether a commit exists yet. REVEAL covers the reveal window and the
    ///      post-deadline pre-finalize state (terminalization happens in finalize).
    function phase(uint256 requestId) public view returns (Phase) {
        Request storage r = requests[requestId];
        if (r.cancelled) return Phase.CANCELLED;
        if (r.finalized) return Phase.FINALIZED;
        if (r.failed) return Phase.FAILED;
        if (block.timestamp <= r.commitDeadline) {
            return r.commitCount == 0 ? Phase.OPEN : Phase.COMMIT;
        }
        return Phase.REVEAL;
    }

    function finalizeReady(uint256 requestId) public view returns (bool) {
        Request storage r = requests[requestId];
        return block.timestamp > r.revealDeadline && !r.finalized && !r.failed && !r.cancelled;
    }

    // ------------------------------------------------------------- lifecycle

    function postRequest(
        bytes32 market,
        Side side,
        uint256 quantity,
        uint256 maxPriceTick,
        uint256 minMatch,
        uint256 commitDeadline,
        uint256 revealDeadline
    ) external nonReentrant returns (uint256 requestId) {
        require(block.timestamp < commitDeadline, "deadline in past");
        require(commitDeadline < revealDeadline, "deadline order");
        require(revealDeadline <= block.timestamp + 7 days, "window too long");
        require(quantity > 0, "zero quantity");
        require(minMatch > 0 && minMatch <= quantity, "bad minMatch");
        require(maxPriceTick > 0 && maxPriceTick < 1000, "bad maxPriceTick");

        requestId = ++requestCount;
        bytes32 marketId = marketIdOf(requestId);
        outcomeTokens.reserveMarket(marketId);

        uint256 escrowAmount = (quantity * maxPriceTick) / 1000;
        uint256 minQuoteSize = (minMatch + MAX_QUOTES - 1) / MAX_QUOTES;

        vault.lock(msg.sender, escrowAmount, escrowRef(requestId));
        vault.lock(msg.sender, RFM_BOND, instBondRef(requestId));

        requests[requestId] = Request({
            requester: msg.sender,
            market: market,
            side: side,
            quantity: quantity,
            maxPriceTick: maxPriceTick,
            minMatch: minMatch,
            commitDeadline: commitDeadline,
            revealDeadline: revealDeadline,
            escrowAmount: escrowAmount,
            minQuoteSize: minQuoteSize,
            commitCount: 0,
            finalized: false,
            failed: false,
            cancelled: false
        });

        emit RequestPosted(
            requestId, market, side, quantity, maxPriceTick, minMatch, commitDeadline, revealDeadline, escrowAmount,
            minQuoteSize
        );
        emit MarketReserved(marketId, requestId);
    }

    function commitQuote(uint256 requestId, bytes32 commitHash) external nonReentrant {
        Request storage r = requests[requestId];
        Phase p = phase(requestId);
        require(p == Phase.OPEN || p == Phase.COMMIT, "commit window closed");
        Commit storage c = commits[requestId][msg.sender];
        if (!c.hasCommitted) {
            require(r.commitCount < MAX_QUOTES, "slots full");
            c.commitIndex = r.commitCount;
            r.commitCount += 1;
            mmList[requestId].push(msg.sender);
            vault.lock(msg.sender, RFM_BOND, mmBondRef(requestId, msg.sender));
        }
        c.commitHash = commitHash;
        c.hasCommitted = true;
        emit QuoteCommitted(requestId, msg.sender, c.commitIndex);
    }

    function revealQuote(uint256 requestId, uint256 priceTick, uint256 size, uint256 salt) external nonReentrant {
        Request storage r = requests[requestId];
        require(block.timestamp > r.commitDeadline, "commit window open");
        require(block.timestamp <= r.revealDeadline, "reveal window closed");
        require(!r.finalized && !r.failed && !r.cancelled, "terminal");
        Commit storage c = commits[requestId][msg.sender];
        require(c.hasCommitted, "not committed");
        bytes32 recomputed =
            keccak256(abi.encode(block.chainid, address(this), requestId, msg.sender, priceTick, size, salt));
        require(recomputed == c.commitHash, "hash mismatch");

        Reveal storage rv = reveals[requestId][msg.sender];
        // Undersized reveals are treated as out-of-range (bond slashes) - anti-Sybil.
        bool inRange = priceTick <= r.maxPriceTick && size > 0 && size >= r.minQuoteSize;
        rv.tick = priceTick;
        rv.size = size;
        rv.inRange = inRange;
        rv.hasRevealed = true;
        if (inRange) {
            rv.lockedAmount = size - _mulDivFloor(size, priceTick); // counter-leg of the requested outcome
            vault.lock(msg.sender, rv.lockedAmount, mmRevealRef(requestId, msg.sender));
        }
        emit QuoteRevealed(requestId, msg.sender, priceTick, size, inRange);
    }

    function cancel(uint256 requestId) external nonReentrant {
        Request storage r = requests[requestId];
        require(msg.sender == r.requester, "not requester");
        require(r.commitCount == 0, "commits exist");
        require(!r.finalized && !r.failed && !r.cancelled, "terminal");
        r.cancelled = true;
        vault.releaseLock(escrowRef(requestId), r.escrowAmount);
        vault.releaseLock(instBondRef(requestId), RFM_BOND);
        emit RequestCancelled(requestId);
    }

    function finalize(uint256 requestId) external nonReentrant {
        Request storage r = requests[requestId];
        require(block.timestamp > r.revealDeadline, "reveal window open");
        require(!r.finalized && !r.failed && !r.cancelled, "terminal");

        // 1. Gather in-range revealed quotes; sort by (tick asc, commitIndex asc).
        // 2. Greedy fill to quantity.
        QuoteSet memory q = _gatherQuotes(requestId, r);
        FillSet memory f = _selectFills(r, q);
        uint256 filled = _filledSize(f);

        if (filled >= r.minMatch) {
            r.finalized = true;
            _finalizeMarket(r, requestId, f, filled);
            emit RequestFinalized(requestId);
        } else {
            r.failed = true;
            _failRequest(r, requestId);
            emit RequestFailed(requestId);
        }
    }

    function _gatherQuotes(uint256 requestId, Request storage r) internal view returns (QuoteSet memory q) {
        uint256 n = r.commitCount;
        address[] memory qmm = new address[](n);
        uint256[] memory qtick = new uint256[](n);
        uint256[] memory qsize = new uint256[](n);
        uint256[] memory qidx = new uint256[](n);
        uint256 qlen = 0;
        for (uint256 i = 0; i < n; ++i) {
            address mm = mmList[requestId][i];
            Reveal storage rv = reveals[requestId][mm];
            if (!rv.hasRevealed) continue;
            if (rv.tick > r.maxPriceTick || rv.size == 0 || rv.size < r.minQuoteSize) continue;
            qmm[qlen] = mm;
            qtick[qlen] = rv.tick;
            qsize[qlen] = rv.size;
            qidx[qlen] = commits[requestId][mm].commitIndex;
            ++qlen;
        }
        _insertionSort(qmm, qtick, qsize, qidx, qlen);
        q = QuoteSet({mm: qmm, tick: qtick, size: qsize, idx: qidx, len: qlen});
    }

    function _selectFills(Request storage r, QuoteSet memory q) internal view returns (FillSet memory f) {
        address[] memory fmm = new address[](q.len);
        uint256[] memory ftick = new uint256[](q.len);
        uint256[] memory fsize = new uint256[](q.len);
        uint256 flen = 0;
        uint256 remaining = r.quantity;
        for (uint256 i = 0; i < q.len && remaining > 0; ++i) {
            uint256 take = q.size[i] < remaining ? q.size[i] : remaining;
            fmm[flen] = q.mm[i];
            ftick[flen] = q.tick[i];
            fsize[flen] = take;
            ++flen;
            remaining -= take;
        }
        f = FillSet({mm: fmm, tick: ftick, size: fsize, len: flen});
    }

    function _filledSize(FillSet memory f) internal pure returns (uint256 total) {
        for (uint256 i = 0; i < f.len; ++i) {
            total += f.size[i];
        }
    }

    // ------------------------------------------------------------- finalize

    function _finalizeMarket(Request storage r, uint256 requestId, FillSet memory f, uint256 filled) internal {
        bytes32 marketId = marketIdOf(requestId);
        // createMarket BEFORE the single aggregated split.
        outcomeTokens.createMarket(marketId, abi.encode(r.market, r.side, r.quantity, r.maxPriceTick, r.minMatch));

        uint256 consumedEscrow = _sumConsumedEscrow(f);
        _mintAllocated(r, requestId, marketId, f, filled, consumedEscrow);
        if (r.escrowAmount > consumedEscrow) {
            vault.releaseLock(escrowRef(requestId), r.escrowAmount - consumedEscrow);
        }
        vault.releaseLock(instBondRef(requestId), RFM_BOND);

        _settleWinnerBonds(requestId, f);
        _settleRemaining(r, requestId, f);
        _emitMarketBorn(requestId, marketId, f, filled, r.side);
    }

    function _mintAllocated(Request storage r, uint256 requestId, bytes32 marketId, FillSet memory f, uint256 filled, uint256 consumedEscrow)
        internal
    {
        IVault.Allocation[] memory yesAlloc = new IVault.Allocation[](r.side == Side.YES ? 1 : f.len);
        IVault.Allocation[] memory noAlloc = new IVault.Allocation[](r.side == Side.YES ? f.len : 1);
        if (r.side == Side.YES) {
            yesAlloc[0] = IVault.Allocation(r.requester, filled);
            for (uint256 i = 0; i < f.len; ++i) {
                noAlloc[i] = IVault.Allocation(f.mm[i], f.size[i]);
            }
        } else {
            noAlloc[0] = IVault.Allocation(r.requester, filled);
            for (uint256 i = 0; i < f.len; ++i) {
                yesAlloc[i] = IVault.Allocation(f.mm[i], f.size[i]);
            }
        }

        IVault.Funding[] memory funding = new IVault.Funding[](f.len + (consumedEscrow > 0 ? 1 : 0));
        uint256 fi = 0;
        if (consumedEscrow > 0) {
            funding[fi++] = IVault.Funding(IVault.FundingKind.LOCK, escrowRef(requestId), r.requester, consumedEscrow);
        }
        for (uint256 i = 0; i < f.len; ++i) {
            uint256 mmLeg = f.size[i] - _mulDivFloor(f.size[i], f.tick[i]);
            funding[fi++] = IVault.Funding(IVault.FundingKind.LOCK, mmRevealRef(requestId, f.mm[i]), f.mm[i], mmLeg);
        }
        // mintPair consumes all funding locks internally in one call.
        vault.mintPair(marketId, yesAlloc, noAlloc, funding, filled);
    }

    function _settleWinnerBonds(uint256 requestId, FillSet memory f) internal {
        // Fully-filled winners: reveal lock fully consumed by mintPair. Partially-filled
        // winners: consume exactly the filled counter-leg, release the remainder. A
        // floor-rounding collision can make the remainder exactly zero, which is a
        // legitimate no-op (releaseLock(0) would revert and wedge finalize forever).
        for (uint256 i = 0; i < f.len; ++i) {
            vault.releaseLock(mmBondRef(requestId, f.mm[i]), RFM_BOND);
            Reveal storage rv = reveals[requestId][f.mm[i]];
            if (f.size[i] < rv.size) {
                uint256 filledLeg = f.size[i] - _mulDivFloor(f.size[i], f.tick[i]);
                uint256 remainder = rv.lockedAmount - filledLeg;
                if (remainder > 0) {
                    vault.releaseLock(mmRevealRef(requestId, f.mm[i]), remainder);
                }
            }
            emit RfmFill(requestId, f.mm[i], f.tick[i], f.size[i]);
        }
    }

    function _settleRemaining(Request storage r, uint256 requestId, FillSet memory f) internal {
        // Unselected in-range MMs: reveal + bond released. Non-revealers and
        // out-of-range/undersized: bond consumed to the institution (slash).
        uint256 n = r.commitCount;
        for (uint256 i = 0; i < n; ++i) {
            address mm = mmList[requestId][i];
            Reveal storage rv = reveals[requestId][mm];
            if (rv.hasRevealed && rv.inRange) {
                if (_isWinner(f.mm, f.len, mm)) continue; // handled above
                vault.releaseLock(mmRevealRef(requestId, mm), rv.lockedAmount);
                vault.releaseLock(mmBondRef(requestId, mm), RFM_BOND);
            } else {
                vault.consumeLock(mmBondRef(requestId, mm), RFM_BOND, r.requester);
                emit BondSlashed(requestId, mm, r.requester);
            }
        }
    }

    function _emitMarketBorn(uint256 requestId, bytes32 marketId, FillSet memory f, uint256 filled, Side side)
        internal
    {
        // MarketBorn: ticks converted to canonical YES basis exactly once.
        uint256 marginalTick = f.len > 0 ? _toYesBasis(f.tick[f.len - 1], side) : 0;
        uint256 vwapSum = 0;
        for (uint256 i = 0; i < f.len; ++i) {
            vwapSum += f.size[i] * f.tick[i];
        }
        uint256 vwapTick = f.len > 0 ? _toYesBasis(vwapSum / filled, side) : 0;
        emit MarketBorn(requestId, marketId, marginalTick, vwapTick, filled, side);
    }

    function _failRequest(Request storage r, uint256 requestId) internal {
        vault.releaseLock(escrowRef(requestId), r.escrowAmount);
        vault.releaseLock(instBondRef(requestId), RFM_BOND);
        uint256 n = r.commitCount;
        for (uint256 i = 0; i < n; ++i) {
            address mm = mmList[requestId][i];
            Reveal storage rv = reveals[requestId][mm];
            if (rv.hasRevealed && rv.inRange) {
                vault.releaseLock(mmRevealRef(requestId, mm), rv.lockedAmount);
                vault.releaseLock(mmBondRef(requestId, mm), RFM_BOND);
            } else {
                vault.consumeLock(mmBondRef(requestId, mm), RFM_BOND, r.requester);
                emit BondSlashed(requestId, mm, r.requester);
            }
        }
    }

    // -------------------------------------------------------------- internal

    function _sumConsumedEscrow(FillSet memory f) internal pure returns (uint256 total) {
        for (uint256 i = 0; i < f.len; ++i) {
            total += _mulDivFloor(f.size[i], f.tick[i]);
        }
    }

    function _mulDivFloor(uint256 size, uint256 tick) internal pure returns (uint256) {
        return (size * tick) / 1000;
    }

    function _toYesBasis(uint256 tick, Side side) internal pure returns (uint256) {
        return side == Side.YES ? tick : 1000 - tick;
    }

    function _isWinner(address[] memory winners, uint256 len, address mm) internal pure returns (bool) {
        for (uint256 i = 0; i < len; ++i) {
            if (winners[i] == mm) return true;
        }
        return false;
    }

    function _insertionSort(
        address[] memory mm,
        uint256[] memory tick,
        uint256[] memory size,
        uint256[] memory idx,
        uint256 len
    ) internal pure {
        for (uint256 i = 1; i < len; ++i) {
            address mmv = mm[i];
            uint256 tickv = tick[i];
            uint256 sizev = size[i];
            uint256 idxv = idx[i];
            uint256 j = i;
            while (j > 0 && (tick[j - 1] > tickv || (tick[j - 1] == tickv && idx[j - 1] > idxv))) {
                mm[j] = mm[j - 1];
                tick[j] = tick[j - 1];
                size[j] = size[j - 1];
                idx[j] = idx[j - 1];
                --j;
            }
            mm[j] = mmv;
            tick[j] = tickv;
            size[j] = sizev;
            idx[j] = idxv;
        }
    }
}
