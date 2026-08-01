// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Test} from "forge-std/Test.sol";
import {VmSafe} from "forge-std/Vm.sol";

import {RFM} from "../src/RFM.sol";
import {Vault} from "../src/Vault.sol";
import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {CTFExchangeLite} from "../src/CTFExchangeLite.sol";
import {IRFM} from "../src/interfaces/IRFM.sol";
import {IOutcomeTokens} from "../src/interfaces/IOutcomeTokens.sol";
import {MockUSDC} from "./mocks/MockUSDC.sol";

contract RFMTest is Test {
    MockUSDC usdc;
    OutcomeTokens ot;
    Vault vault;
    CTFExchangeLite exch;
    RFM rfm;

    address operator = makeAddr("operator");
    address institution = makeAddr("institution");
    address mm1 = makeAddr("mm1");
    address mm2 = makeAddr("mm2");
    address mm3 = makeAddr("mm3");
    address mm4 = makeAddr("mm4");
    address stranger = makeAddr("stranger");

    bytes32 market = keccak256("event-description");
    uint256 constant QUANTITY = 1000e6;
    uint256 constant MAX_TICK = 600;
    uint256 constant MIN_MATCH = 200e6;
    uint256 constant BOND = 500e6;
    uint256 commitDeadline;
    uint256 revealDeadline;

    function setUp() public {
        usdc = new MockUSDC();
        ot = new OutcomeTokens(usdc, operator, address(this));
        vault = new Vault(usdc, address(ot), address(this));
        exch = new CTFExchangeLite(address(vault), address(ot), operator);
        rfm = new RFM(address(vault), address(ot));
        ot.setRoles(address(vault), address(rfm));
        vault.setRoles(address(exch), address(rfm));
        commitDeadline = block.timestamp + 3600;
        revealDeadline = block.timestamp + 7200;
    }

    // ---------------------------------------------------------------- helpers

    function _deposit(address user, uint256 amt) internal {
        usdc.mint(user, amt);
        vm.startPrank(user);
        usdc.approve(address(vault), amt);
        vault.deposit(amt);
        vm.stopPrank();
    }

    function _post(address requester, IRFM.Side side) internal returns (uint256 requestId) {
        vm.prank(requester);
        requestId = rfm.postRequest(market, side, QUANTITY, MAX_TICK, MIN_MATCH, commitDeadline, revealDeadline);
    }

    function _commit(address mm, uint256 requestId, uint256 tick, uint256 size, uint256 salt) internal {
        // Computed inline: an external quoteHash() call would consume any pending expectRevert.
        bytes32 h = keccak256(abi.encode(block.chainid, address(rfm), requestId, mm, tick, size, salt));
        vm.prank(mm);
        rfm.commitQuote(requestId, h);
    }

    function _reveal(address mm, uint256 requestId, uint256 tick, uint256 size, uint256 salt) internal {
        vm.prank(mm);
        rfm.revealQuote(requestId, tick, size, salt);
    }

    function _warpToReveal() internal {
        vm.warp(commitDeadline + 1);
    }

    function _warpPastReveal() internal {
        vm.warp(revealDeadline + 1);
    }

    function _fundMM(address mm, uint256 revealLock) internal {
        _deposit(mm, BOND + revealLock);
    }

    // ------------------------------------------------------------------ post

    function test_postRequest_validatesAll() public {
        _deposit(institution, 1200e6);
        vm.prank(institution);
        vm.expectRevert("deadline in past");
        rfm.postRequest(market, IRFM.Side.YES, QUANTITY, MAX_TICK, MIN_MATCH, block.timestamp, block.timestamp + 100);

        vm.prank(institution);
        vm.expectRevert("deadline order");
        rfm.postRequest(market, IRFM.Side.YES, QUANTITY, MAX_TICK, MIN_MATCH, block.timestamp + 100, block.timestamp + 50);

        vm.prank(institution);
        vm.expectRevert("window too long");
        rfm.postRequest(
            market, IRFM.Side.YES, QUANTITY, MAX_TICK, MIN_MATCH, block.timestamp + 100, block.timestamp + 8 days
        );

        vm.prank(institution);
        vm.expectRevert("zero quantity");
        rfm.postRequest(market, IRFM.Side.YES, 0, MAX_TICK, MIN_MATCH, commitDeadline, revealDeadline);

        vm.prank(institution);
        vm.expectRevert("bad minMatch");
        rfm.postRequest(market, IRFM.Side.YES, QUANTITY, MAX_TICK, 0, commitDeadline, revealDeadline);

        vm.prank(institution);
        vm.expectRevert("bad minMatch");
        rfm.postRequest(market, IRFM.Side.YES, QUANTITY, MAX_TICK, QUANTITY + 1, commitDeadline, revealDeadline);

        vm.prank(institution);
        vm.expectRevert("bad maxPriceTick");
        rfm.postRequest(market, IRFM.Side.YES, QUANTITY, 0, MIN_MATCH, commitDeadline, revealDeadline);

        vm.prank(institution);
        vm.expectRevert("bad maxPriceTick");
        rfm.postRequest(market, IRFM.Side.YES, QUANTITY, 1000, MIN_MATCH, commitDeadline, revealDeadline);
    }

    function test_postRequest_insufficientFree() public {
        _deposit(institution, 100e6); // escrow 600e6 + bond 500e6 required
        vm.expectRevert(Vault.InsufficientFree.selector);
        _post(institution, IRFM.Side.YES);
    }

    function test_postRequest_effects() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        assertEq(requestId, 1);
        (bool reserved,,,,) = ot.markets(rfm.marketIdOf(requestId));
        assertTrue(reserved);
        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.OPEN));
        assertEq(vault.lockedBal(institution), 600e6 + BOND);
        assertEq(vault.freeBal(institution), 1200e6 - 600e6 - BOND);
        (,,,,,,,, uint256 escrowAmount, uint256 minQuoteSize,,,,) = rfm.requests(requestId);
        assertEq(escrowAmount, 600e6);
        assertEq(minQuoteSize, (MIN_MATCH + 31) / 32);
    }

    // ---------------------------------------------------------------- phases

    function test_phaseTransitions() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6);
        _commit(mm1, requestId, 500, QUANTITY, 1);
        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.COMMIT));

        _warpToReveal();
        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.REVEAL));
        _reveal(mm1, requestId, 500, QUANTITY, 1);
        _warpPastReveal();
        assertTrue(rfm.finalizeReady(requestId));
        rfm.finalize(requestId);
        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.FINALIZED));
    }

    // ---------------------------------------------------------------- cancel

    function test_cancel_refundsAndTerminal() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        vm.prank(institution);
        rfm.cancel(requestId);
        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.CANCELLED));
        assertEq(vault.freeBal(institution), 1200e6); // escrow + bond fully released
        assertEq(vault.lockedBal(institution), 0);
    }

    function test_cancel_onlyRequester() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        vm.prank(stranger);
        vm.expectRevert("not requester");
        rfm.cancel(requestId);
    }

    function test_cancel_blocksAfterCommit() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6);
        _commit(mm1, requestId, 500, QUANTITY, 1);
        vm.prank(institution);
        vm.expectRevert("commits exist");
        rfm.cancel(requestId);
    }

    // ---------------------------------------------------------------- commit

    function test_commitQuote_locksBondAndRecommitReuses() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6);
        _commit(mm1, requestId, 500, QUANTITY, 1);
        assertEq(vault.lockedBal(mm1), BOND);

        // Recommit overwrites the hash and reuses the same bond.
        _commit(mm1, requestId, 550, QUANTITY, 2);
        assertEq(vault.lockedBal(mm1), BOND);
        (bytes32 h, uint256 idx,) = rfm.commits(requestId, mm1);
        assertEq(h, rfm.quoteHash(requestId, mm1, 550, QUANTITY, 2));
        assertEq(idx, 0);
    }

    function test_commitQuote_windowClosed() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6);
        _warpToReveal();
        vm.expectRevert("commit window closed");
        _commit(mm1, requestId, 500, QUANTITY, 1);
    }

    function test_commitQuote_maxSlots() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        for (uint256 i = 0; i < 32; ++i) {
            address mm = address(uint160(0x1000 + i));
            _deposit(mm, BOND);
            _commit(mm, requestId, 500, 100e6, i);
        }
        address mm33 = address(uint160(0x9999));
        _deposit(mm33, BOND);
        vm.expectRevert("slots full");
        _commit(mm33, requestId, 500, 100e6, 99);
    }

    // ---------------------------------------------------------------- reveal

    function test_revealQuote_locksCounterLeg() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 300e6); // bond 500 + reveal lock 150 + 150 spare
        _commit(mm1, requestId, 500, 300e6, 1);
        _warpToReveal();
        _reveal(mm1, requestId, 500, 300e6, 1);
        (,, uint256 locked,,) = rfm.reveals(requestId, mm1);
        assertEq(locked, 300e6 - 150e6); // counter-leg of the requested outcome
        assertEq(vault.lockedBal(mm1), BOND + 150e6);
    }

    function test_revealQuote_hashMismatchReverts() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6);
        _commit(mm1, requestId, 500, 300e6, 1);
        _warpToReveal();
        vm.expectRevert("hash mismatch");
        _reveal(mm1, requestId, 500, 300e6, 2); // wrong salt
    }

    function test_revealQuote_notCommitted() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _warpToReveal();
        vm.expectRevert("not committed");
        _reveal(mm1, requestId, 500, 300e6, 1);
    }

    function test_revealQuote_outOfRangeAccepted() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6);
        _commit(mm1, requestId, 700, 300e6, 1); // 700 > max 600
        _warpToReveal();
        _reveal(mm1, requestId, 700, 300e6, 1);
        (,, uint256 locked, bool revealed, bool inRange) = rfm.reveals(requestId, mm1);
        assertTrue(revealed);
        assertFalse(inRange);
        assertEq(locked, 0); // no funding lock for out-of-range
        assertEq(vault.lockedBal(mm1), BOND); // slash deferred to finalize
    }

    // --------------------------------------------------------------- finalize

    function test_finalize_singleWinnerFullFill() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6); // reveal lock = 1000 - 500 = 500
        _commit(mm1, requestId, 500, QUANTITY, 1);
        _warpToReveal();
        _reveal(mm1, requestId, 500, QUANTITY, 1);
        _warpPastReveal();
        rfm.finalize(requestId);

        bytes32 marketId = rfm.marketIdOf(requestId);
        (, bool exists,,,) = ot.markets(marketId);
        assertTrue(exists);
        uint256 yesId = ot.tokenId(marketId, IOutcomeTokens.Outcome.YES);
        uint256 noId = ot.tokenId(marketId, IOutcomeTokens.Outcome.NO);
        assertEq(vault.tokenBal(institution, yesId), QUANTITY);
        assertEq(vault.tokenBal(mm1, noId), QUANTITY);
        assertEq(vault.usdcBal(institution), 1200e6 - 500e6); // escrow consumed 500e6, bond + remainder released
        assertEq(vault.usdcBal(mm1), 500e6); // 1000 deposited, 500 reveal lock consumed
        assertEq(usdc.balanceOf(address(ot)), QUANTITY); // pool fully funded
        assertEq(vault.lockedBal(institution), 0);
        assertEq(vault.lockedBal(mm1), 0);
    }

    function test_finalize_multiMMPartialFill() public {
        // The multi-winner partial-fill path: 4 MMs, all in-range, greedy fill,
        // one aggregated mintPair, complete terminal accounting.
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);

        // Commit order: mm1, mm2, mm3, mm4. Ticks: 500, 600, 550, 500.
        _fundMM(mm1, 150e6);
        _fundMM(mm2, 120e6);
        _fundMM(mm3, 135e6);
        _fundMM(mm4, 150e6);
        _commit(mm1, requestId, 500, 300e6, 1);
        _commit(mm2, requestId, 600, 300e6, 2);
        _commit(mm3, requestId, 550, 300e6, 3);
        _commit(mm4, requestId, 500, 300e6, 4);

        _warpToReveal();
        _reveal(mm1, requestId, 500, 300e6, 1);
        _reveal(mm2, requestId, 600, 300e6, 2);
        _reveal(mm3, requestId, 550, 300e6, 3);
        _reveal(mm4, requestId, 500, 300e6, 4);
        _warpPastReveal();
        rfm.finalize(requestId);

        bytes32 marketId = rfm.marketIdOf(requestId);
        uint256 yesId = ot.tokenId(marketId, IOutcomeTokens.Outcome.YES);
        uint256 noId = ot.tokenId(marketId, IOutcomeTokens.Outcome.NO);

        // Fill order (tick asc, commitIndex asc): mm1(500,0)=300, mm4(500,3)=300,
        // mm3(550,2)=300, mm2(600,1)=100 (partial). consumedEscrow = 525e6.
        assertEq(vault.tokenBal(institution, yesId), QUANTITY);
        assertEq(vault.tokenBal(mm1, noId), 300e6);
        assertEq(vault.tokenBal(mm4, noId), 300e6);
        assertEq(vault.tokenBal(mm3, noId), 300e6);
        assertEq(vault.tokenBal(mm2, noId), 100e6);

        // Escrow: 600e6 locked, 525e6 consumed, 75e6 released.
        assertEq(vault.usdcBal(institution), 1200e6 - 525e6);
        // mm2 partial winner: locked 120e6, consumed exactly 40e6 (filled counter-leg),
        // remainder 80e6 released. Deposited 620e6.
        assertEq(vault.usdcBal(mm2), 620e6 - 40e6);
        // Fully filled winners: reveal lock fully consumed.
        assertEq(vault.usdcBal(mm1), 650e6 - 150e6);
        assertEq(vault.usdcBal(mm4), 650e6 - 150e6);
        assertEq(vault.usdcBal(mm3), 635e6 - 135e6);

        // No stranded locks, no slashes (all in-range).
        assertEq(vault.lockedBal(institution), 0);
        assertEq(vault.lockedBal(mm1) + vault.lockedBal(mm2) + vault.lockedBal(mm3) + vault.lockedBal(mm4), 0);
        assertEq(usdc.balanceOf(address(ot)), QUANTITY);
    }

    function test_finalize_failedReleasesAndSlashes() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        // mm1 reveals in-range but too small to reach minMatch; mm2 commits and never reveals.
        _fundMM(mm1, 50e6);
        _fundMM(mm2, 0);
        _commit(mm1, requestId, 500, 100e6, 1);
        _commit(mm2, requestId, 500, 500e6, 2);
        _warpToReveal();
        _reveal(mm1, requestId, 500, 100e6, 1); // 100e6 < minMatch 200e6 -> filled 0
        _warpPastReveal();
        rfm.finalize(requestId);

        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.FAILED));
        // Institution fully refunded.
        // mm1 in-range reveal released fully.
        assertEq(vault.usdcBal(mm1), 550e6);
        // mm2 never revealed: bond slashed to the institution.
        assertEq(vault.usdcBal(mm2), 0);
        assertEq(vault.usdcBal(institution), 1200e6 + BOND);
        assertEq(vault.lockedBal(institution), 0);
        assertEq(vault.lockedBal(mm1), 0);
        assertEq(vault.lockedBal(mm2), 0);
    }

    function test_finalize_slashesOutOfRange() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 0);
        _commit(mm1, requestId, 700, 300e6, 1); // out of range
        _fundMM(mm2, 500e6);
        _commit(mm2, requestId, 500, QUANTITY, 2);
        _warpToReveal();
        _reveal(mm1, requestId, 700, 300e6, 1); // recorded, inRange false
        _reveal(mm2, requestId, 500, QUANTITY, 2);
        _warpPastReveal();
        rfm.finalize(requestId);

        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.FINALIZED));
        // mm1's bond consumed to the institution; mm1's usdcBal drained by the bond lock.
        assertEq(vault.usdcBal(mm1), 0);
        assertEq(vault.usdcBal(institution), 1200e6 - 500e6 + BOND); // -escrow consumed +slashed bond
        assertEq(vault.lockedBal(mm1), 0);
        assertEq(vault.lockedBal(institution), 0);
    }

    function test_finalize_revertsBeforeDeadline() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        vm.expectRevert("reveal window open");
        rfm.finalize(requestId);
    }

    function test_finalize_doubleFinalizeReverts() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        _fundMM(mm1, 500e6);
        _commit(mm1, requestId, 500, QUANTITY, 1);
        _warpToReveal();
        _reveal(mm1, requestId, 500, QUANTITY, 1);
        _warpPastReveal();
        rfm.finalize(requestId);
        vm.expectRevert("terminal");
        rfm.finalize(requestId);
    }

    function test_finalize_tieBreakCommitOrder() public {
        _deposit(institution, 800e6); // escrow 180e6 + bond 500e6
        // Small request so the quantity is exhausted at the tied price level.
        vm.prank(institution);
        uint256 requestId =
            rfm.postRequest(market, IRFM.Side.YES, 300e6, MAX_TICK, 100e6, commitDeadline, revealDeadline);
        _fundMM(mm1, 150e6);
        _fundMM(mm2, 150e6);
        _commit(mm1, requestId, 500, 300e6, 1); // first commit wins the tie
        _commit(mm2, requestId, 500, 300e6, 2);
        _warpToReveal();
        _reveal(mm1, requestId, 500, 300e6, 1);
        _reveal(mm2, requestId, 500, 300e6, 2);
        _warpPastReveal();
        rfm.finalize(requestId);

        bytes32 marketId = rfm.marketIdOf(requestId);
        uint256 noId = ot.tokenId(marketId, IOutcomeTokens.Outcome.NO);
        assertEq(vault.tokenBal(mm1, noId), 300e6); // filled
        assertEq(vault.tokenBal(mm2, noId), 0); // unselected
        assertEq(vault.usdcBal(mm2), 650e6); // bond + reveal lock released
    }

    function test_finalize_undersizedRevealSlashes() public {
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        // minQuoteSize = ceil(200e6/32) = 6.25e6; 5e6 is undersized.
        _fundMM(mm1, 0);
        _fundMM(mm2, 500e6);
        _commit(mm1, requestId, 500, 5e6, 1);
        _commit(mm2, requestId, 500, QUANTITY, 2);
        _warpToReveal();
        _reveal(mm1, requestId, 500, 5e6, 1); // undersized -> inRange false -> slash
        _reveal(mm2, requestId, 500, QUANTITY, 2);
        _warpPastReveal();
        rfm.finalize(requestId);
        assertEq(vault.usdcBal(mm1), 0); // bond consumed
        assertEq(vault.usdcBal(institution), 1200e6 - 500e6 + BOND);
    }

    function test_finalize_noSideConversion() public {
        // Institution buys NO. RFM ticks are the price of the requested (NO) outcome.
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.NO);
        _fundMM(mm1, 700e6); // reveal lock = 1000 - floor(1000*300/1000) = 700e6
        _commit(mm1, requestId, 300, QUANTITY, 1);
        _warpToReveal();
        _reveal(mm1, requestId, 300, QUANTITY, 1);
        _warpPastReveal();
        vm.recordLogs();
        rfm.finalize(requestId);

        bytes32 marketId = rfm.marketIdOf(requestId);
        uint256 yesId = ot.tokenId(marketId, IOutcomeTokens.Outcome.YES);
        uint256 noId = ot.tokenId(marketId, IOutcomeTokens.Outcome.NO);
        assertEq(vault.tokenBal(institution, noId), QUANTITY); // institution buys NO
        assertEq(vault.tokenBal(mm1, yesId), QUANTITY); // MM takes the complement (YES)

        VmSafe.Log[] memory logs = vm.getRecordedLogs();
        for (uint256 i = 0; i < logs.length; ++i) {
            if (logs[i].topics[0] == keccak256("MarketBorn(uint256,bytes32,uint256,uint256,uint256,uint8)")) {
                (uint256 marginal, uint256 vwap,,) = abi.decode(logs[i].data, (uint256, uint256, uint256, uint256));
                // 300 NO tick -> yes basis 700; vwap = 1000 - 300 = 700.
                assertEq(marginal, 700);
                assertEq(vwap, 700);
                return;
            }
        }
        fail("MarketBorn not emitted");
    }

    function test_finalize_32Quotes() public {
        // Build-gate gas scenario: full slot occupation, all in-range, finalize must succeed.
        _deposit(institution, 1200e6);
        uint256 requestId = _post(institution, IRFM.Side.YES);
        uint256 perMM = 50e6;
        for (uint256 i = 0; i < 32; ++i) {
            address mm = address(uint160(0x1000 + i));
            uint256 lock = perMM - (perMM * 500) / 1000;
            _deposit(mm, BOND + lock);
            _commit(mm, requestId, 500, perMM, i);
        }
        _warpToReveal();
        for (uint256 i = 0; i < 32; ++i) {
            address mm = address(uint160(0x1000 + i));
            _reveal(mm, requestId, 500, perMM, i);
        }
        _warpPastReveal();
        rfm.finalize(requestId);
        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.FINALIZED));
        bytes32 marketId = rfm.marketIdOf(requestId);
        uint256 yesId = ot.tokenId(marketId, IOutcomeTokens.Outcome.YES);
        assertEq(vault.tokenBal(institution, yesId), QUANTITY);
    }
}
