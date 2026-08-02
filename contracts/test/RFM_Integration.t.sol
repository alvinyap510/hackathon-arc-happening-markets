// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Test} from "forge-std/Test.sol";

import {RFM} from "../src/RFM.sol";
import {Vault} from "../src/Vault.sol";
import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {CTFExchangeLite} from "../src/CTFExchangeLite.sol";
import {IRFM} from "../src/interfaces/IRFM.sol";
import {IOutcomeTokens} from "../src/interfaces/IOutcomeTokens.sol";
import {MockUSDC} from "./mocks/MockUSDC.sol";

/// @notice End-to-end lifecycle: RFM auction births a market, the born market
///         trades on all four directions (BUY YES / BUY NO / SELL YES / SELL NO),
///         then resolves and redeems.
contract RFMIntegrationTest is Test {
    MockUSDC usdc;
    OutcomeTokens ot;
    Vault vault;
    CTFExchangeLite exch;
    RFM rfm;

    address operator = makeAddr("operator");
    address institution = makeAddr("institution");
    address mm1 = makeAddr("mm1");
    address mm2 = makeAddr("mm2");
    address t1 = makeAddr("trader1");
    address t2 = makeAddr("trader2");

    bytes32 market = keccak256("arc-hackathon-event");
    uint256 constant BOND = 500e6;

    function setUp() public {
        usdc = new MockUSDC();
        ot = new OutcomeTokens(usdc, operator, address(this));
        vault = new Vault(usdc, address(ot), address(this));
        exch = new CTFExchangeLite(address(vault), address(ot), operator);
        rfm = new RFM(address(vault), address(ot));
        ot.setRoles(address(vault), address(rfm));
        vault.setRoles(address(exch), address(rfm));
    }

    function _deposit(address user, uint256 amt) internal {
        usdc.mint(user, amt);
        vm.startPrank(user);
        usdc.approve(address(vault), amt);
        vault.deposit(amt);
        vm.stopPrank();
    }

    function test_fullLifecycle_auctionToResolve() public {
        // ---------- institution posts a hedge request ----------
        _deposit(institution, 1300e6); // escrow 600e6 + bond 500e6 + margin
        uint256 quantity = 1000e6;
        vm.prank(institution);
        uint256 requestId = rfm.postRequest(
            market, IRFM.Side.YES, quantity, 600, 200e6, block.timestamp + 3600, block.timestamp + 7200
        );
        bytes32 marketId = rfm.marketIdOf(requestId);

        // ---------- two MMs commit sealed quotes ----------
        _fundAndCommit(mm1, requestId, 500, 600e6, 1);
        _fundAndCommit(mm2, requestId, 450, 600e6, 2);

        // ---------- reveal window ----------
        vm.warp(block.timestamp + 3601);
        _reveal(mm1, requestId, 500, 600e6, 1);
        _reveal(mm2, requestId, 450, 600e6, 2);
        vm.warp(block.timestamp + 3601);

        // ---------- deadline-only finalize ----------
        rfm.finalize(requestId);
        assertEq(uint256(rfm.phase(requestId)), uint256(IRFM.Phase.FINALIZED));
        (, bool exists,,,) = ot.markets(marketId);
        assertTrue(exists);

        uint256 yesId = ot.tokenId(marketId, IOutcomeTokens.Outcome.YES);
        uint256 noId = ot.tokenId(marketId, IOutcomeTokens.Outcome.NO);

        // Fill order (tick asc): mm2 (450, idx1) fills 600e6, mm1 (500, idx0) fills 400e6.
        assertEq(vault.tokenBal(institution, yesId), quantity);
        assertEq(vault.tokenBal(mm2, noId), 600e6);
        assertEq(vault.tokenBal(mm1, noId), 400e6);
        assertEq(usdc.balanceOf(address(ot)), quantity); // market born fully funded

        // ---------- trading on the born market: all 4 directions ----------
        _deposit(t1, 500e6);
        _deposit(t2, 500e6);

        // BUY YES: MINT, t1 pays yesCost for size YES.
        _settleMint(keccak256("m1"), marketId, t1, t2, 400, 200e6);
        // t1 bought 200 YES at tick 400 (cost 80e6); t2 bought 200 NO (cost 120e6).
        assertEq(vault.tokenBal(t1, yesId), 200e6);
        assertEq(vault.tokenBal(t2, noId), 200e6);

        // SELL YES: TRANSFER, t1 sells 50 YES to institution at YES tick 400.
        _settleTransfer(keccak256("m2"), marketId, IOutcomeTokens.Outcome.YES, t1, institution, 400, 50e6);
        assertEq(vault.tokenBal(t1, yesId), 150e6);
        assertEq(vault.tokenBal(institution, yesId), quantity + 50e6);

        // SELL NO: TRANSFER, t2 sells 30 NO to institution at NO tick 600.
        _settleTransfer(keccak256("m3"), marketId, IOutcomeTokens.Outcome.NO, t2, institution, 600, 30e6);
        assertEq(vault.tokenBal(t2, noId), 170e6);
        assertEq(vault.tokenBal(institution, noId), 30e6);

        // MERGE: t2 exits 50 YES + 50 NO (bought YES earlier? give t2 YES first via transfer).
        _settleTransfer(keccak256("m4"), marketId, IOutcomeTokens.Outcome.YES, t1, t2, 400, 50e6);
        _settleMerge(keccak256("m5"), marketId, t2, t2, 500, 50e6);
        assertEq(vault.tokenBal(t2, yesId), 0);
        assertEq(vault.tokenBal(t2, noId), 120e6);
        assertEq(vault.usdcBal(t2), 500e6 - 120e6 + 18e6 - 20e6 + 50e6); // -noCost +soldNO -boughtYES +merge

        // ---------- resolve + redeem ----------
        vm.prank(operator);
        ot.resolve(marketId, IOutcomeTokens.Outcome.YES);
        uint256 instYesBefore = vault.tokenBal(institution, yesId);
        vm.prank(institution);
        vault.redeem(marketId, instYesBefore);
        assertEq(vault.tokenBal(institution, yesId), 0);
        assertEq(vault.usdcBal(institution), 1300e6 - 470e6 - 20e6 - 18e6 + 1050e6);
    }

    function _fundAndCommit(address mm, uint256 requestId, uint256 tick, uint256 size, uint256 salt) internal {
        uint256 lock = size - (size * tick) / 1000;
        _deposit(mm, BOND + lock);
        bytes32 h = keccak256(abi.encode(block.chainid, address(rfm), requestId, mm, tick, size, salt));
        vm.prank(mm);
        rfm.commitQuote(requestId, h);
    }

    function _reveal(address mm, uint256 requestId, uint256 tick, uint256 size, uint256 salt) internal {
        vm.prank(mm);
        rfm.revealQuote(requestId, tick, size, salt);
    }

    function _settleMint(bytes32 tradeId, bytes32 marketId, address yes, address no, uint256 yesTick, uint256 size)
        internal
    {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = CTFExchangeLite.Trade(tradeId, marketId, CTFExchangeLite.TradeClass.MINT,
            IOutcomeTokens.Outcome.YES, yes, no, yesTick, size);
        vm.prank(operator);
        exch.settleBatch(keccak256(abi.encode(tradeId, "b")), trades);
    }

    function _settleTransfer(
        bytes32 tradeId,
        bytes32 marketId,
        IOutcomeTokens.Outcome outcome,
        address seller,
        address buyer,
        uint256 tick,
        uint256 size
    ) internal {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = CTFExchangeLite.Trade(tradeId, marketId, CTFExchangeLite.TradeClass.TRANSFER,
            outcome, seller, buyer, tick, size);
        vm.prank(operator);
        exch.settleBatch(keccak256(abi.encode(tradeId, "b")), trades);
    }

    function _settleMerge(bytes32 tradeId, bytes32 marketId, address yes, address no, uint256 yesTick, uint256 size)
        internal
    {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = CTFExchangeLite.Trade(tradeId, marketId, CTFExchangeLite.TradeClass.MERGE,
            IOutcomeTokens.Outcome.YES, yes, no, yesTick, size);
        vm.prank(operator);
        exch.settleBatch(keccak256(abi.encode(tradeId, "b")), trades);
    }
}
