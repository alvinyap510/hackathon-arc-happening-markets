// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Test} from "forge-std/Test.sol";

import {CTFExchangeLite} from "../src/CTFExchangeLite.sol";
import {Vault} from "../src/Vault.sol";
import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {IVault} from "../src/interfaces/IVault.sol";
import {IOutcomeTokens} from "../src/interfaces/IOutcomeTokens.sol";
import {MockUSDC} from "./mocks/MockUSDC.sol";

contract CTFExchangeLiteTest is Test {
    MockUSDC usdc;
    OutcomeTokens ot;
    Vault vault;
    CTFExchangeLite exch;

    address operator = makeAddr("operator");
    address rfm = makeAddr("rfm");
    address alice = makeAddr("alice");
    address bob = makeAddr("bob");
    address carol = makeAddr("carol");
    address stranger = makeAddr("stranger");

    bytes32 marketId = keccak256("exch-m1");
    uint256 yesId;
    uint256 noId;

    function setUp() public {
        usdc = new MockUSDC();
        ot = new OutcomeTokens(usdc, operator, address(this));
        vault = new Vault(usdc, address(ot), address(this));
        exch = new CTFExchangeLite(address(vault), address(ot), operator);
        ot.setRoles(address(vault), rfm);
        vault.setRoles(address(exch), rfm);
        yesId = ot.tokenId(marketId, IOutcomeTokens.Outcome.YES);
        noId = ot.tokenId(marketId, IOutcomeTokens.Outcome.NO);
    }

    // ---------------------------------------------------------------- helpers

    function _deposit(address user, uint256 amt) internal {
        usdc.mint(user, amt);
        vm.startPrank(user);
        usdc.approve(address(vault), amt);
        vault.deposit(amt);
        vm.stopPrank();
    }

    function _createMarket() internal {
        vm.prank(operator);
        ot.createMarket(marketId, "");
    }

    function _trade(
        bytes32 id,
        CTFExchangeLite.TradeClass cls,
        IOutcomeTokens.Outcome outcome,
        address a,
        address b,
        uint256 tick,
        uint256 size
    ) internal view returns (CTFExchangeLite.Trade memory) {
        return CTFExchangeLite.Trade(id, marketId, cls, outcome, a, b, tick, size);
    }

    function _settle(bytes32 batchId, CTFExchangeLite.Trade[] memory trades) internal {
        vm.prank(operator);
        exch.settleBatch(batchId, trades);
    }

    function _mintBasePositions() internal returns (uint256, uint256) {
        // alice mints YES, bob mints NO: size 50e6 at yesTick 400 -> yes 20e6, no 30e6.
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(
            keccak256("t1"), CTFExchangeLite.TradeClass.MINT, IOutcomeTokens.Outcome.YES, alice, bob, 400, 50e6
        );
        _settle(keccak256("b1"), trades);
        return (20e6, 30e6);
    }

    // -------------------------------------------------------------- operator

    function test_settleBatch_operatorOnly() public {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(keccak256("t1"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 400, 10e6);
        vm.prank(stranger);
        vm.expectRevert(CTFExchangeLite.NotOperator.selector);
        exch.settleBatch(keccak256("b1"), trades);
    }

    // ------------------------------------------------------------------ MINT

    function test_settleBatch_mint() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket();
        (uint256 yesCost, uint256 noCost) = _mintBasePositions();

        assertEq(vault.usdcBal(alice), 100e6 - yesCost);
        assertEq(vault.usdcBal(bob), 100e6 - noCost);
        assertEq(vault.tokenBal(alice, yesId), 50e6);
        assertEq(vault.tokenBal(bob, noId), 50e6);
        assertEq(usdc.balanceOf(address(ot)), 50e6);
        assertTrue(exch.usedTradeIds(keccak256("t1")));
        assertTrue(exch.usedBatchIds(keccak256("b1")));
    }

    // --------------------------------------------------------------- TRANSFER

    function test_settleBatch_transferYes() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket();
        (uint256 yesCost, uint256 noCost) = _mintBasePositions();

        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(keccak256("t2"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 400, 10e6);
        _settle(keccak256("b2"), trades);

        // bob pays floor(10e6*400/1000)=4e6 to alice; 10 YES alice -> bob.
        assertEq(vault.usdcBal(alice), 100e6 - yesCost + 4e6);
        assertEq(vault.usdcBal(bob), 100e6 - noCost - 4e6);
        assertEq(vault.tokenBal(alice, yesId), 40e6);
        assertEq(vault.tokenBal(bob, yesId), 10e6);
    }

    function test_settleBatch_transferNo() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _deposit(carol, 100e6);
        _createMarket();
        _mintBasePositions();

        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        // bob sells NO to carol at NO tick 300 -> carol pays 3e6.
        trades[0] = _trade(keccak256("t2"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.NO, bob, carol, 300, 10e6);
        _settle(keccak256("b2"), trades);
        assertEq(vault.tokenBal(bob, noId), 40e6);
        assertEq(vault.tokenBal(carol, noId), 10e6);
        assertEq(vault.usdcBal(carol), 97e6);
        assertEq(vault.usdcBal(bob), 73e6);
    }

    // ------------------------------------------------------------------ MERGE

    function test_settleBatch_merge() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket();
        (uint256 yesCost, uint256 noCost) = _mintBasePositions();

        // Alice acquires NO from bob (30 NO at tick 300 -> 9e6).
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(keccak256("t2"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.NO, bob, alice, 300, 30e6);
        _settle(keccak256("b2"), trades);

        // Alice merges her own 30 YES + 30 NO pair at yesTick 400: yes leg 12e6, no leg 18e6.
        trades[0] = _trade(keccak256("t3"), CTFExchangeLite.TradeClass.MERGE, IOutcomeTokens.Outcome.YES, alice, alice, 400, 30e6);
        _settle(keccak256("b3"), trades);

        assertEq(vault.tokenBal(alice, yesId), 20e6);
        assertEq(vault.tokenBal(alice, noId), 0);
        assertEq(vault.tokenBal(bob, noId), 20e6);
        assertEq(vault.usdcBal(alice), 100e6 - yesCost - 9e6 + 30e6);
        assertEq(vault.usdcBal(bob), 100e6 - noCost + 9e6);
        assertEq(usdc.balanceOf(address(ot)), 20e6); // 50 minted, 30 merged out
        assertEq(ot.totalSupply(yesId), 20e6);
        assertEq(ot.totalSupply(noId), 20e6);
    }

    // --------------------------------------------------- atomicity + replay

    function test_settleBatch_atomicRollback() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket();
        _mintBasePositions();

        // Batch of two: first valid, second invalid (bob sells 100 NO but holds 50).
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](2);
        trades[0] = _trade(keccak256("t2"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 400, 10e6);
        trades[1] = _trade(keccak256("t3"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.NO, bob, carol, 300, 100e6);
        vm.prank(operator);
        vm.expectRevert(
            abi.encodeWithSelector(CTFExchangeLite.SettleBatchFailed.selector, 1, keccak256("t3"))
        );
        exch.settleBatch(keccak256("b2"), trades);

        // Whole batch rolled back: no partial effects, nothing consumed.
        assertEq(vault.tokenBal(alice, yesId), 50e6);
        assertEq(vault.tokenBal(bob, noId), 50e6);
        assertFalse(exch.usedTradeIds(keccak256("t2")));
        assertFalse(exch.usedTradeIds(keccak256("t3")));
        assertFalse(exch.usedBatchIds(keccak256("b2")));
    }

    function test_settleBatch_tradeReplay() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket();
        _mintBasePositions();

        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(keccak256("t1"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 400, 10e6);
        vm.prank(operator);
        vm.expectRevert(abi.encodeWithSelector(CTFExchangeLite.SettleBatchFailed.selector, 0, keccak256("t1")));
        exch.settleBatch(keccak256("b2"), trades);
    }

    function test_settleBatch_batchReplay() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket();
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(keccak256("t1"), CTFExchangeLite.TradeClass.MINT, IOutcomeTokens.Outcome.YES, alice, bob, 400, 50e6);
        _settle(keccak256("b1"), trades);

        vm.prank(operator);
        vm.expectRevert(abi.encodeWithSelector(CTFExchangeLite.BatchReused.selector, keccak256("b1")));
        exch.settleBatch(keccak256("b1"), trades);
    }

    // ------------------------------------------------------------ validation

    function test_settleBatch_maxBatch() public {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](9);
        for (uint256 i = 0; i < 9; ++i) {
            trades[i] = _trade(bytes32(i), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 400, 1);
        }
        vm.prank(operator);
        vm.expectRevert(abi.encodeWithSelector(CTFExchangeLite.BatchTooLarge.selector, 9));
        exch.settleBatch(keccak256("b1"), trades);
    }

    function test_settleBatch_emptyBatch() public {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](0);
        vm.prank(operator);
        vm.expectRevert(CTFExchangeLite.EmptyBatch.selector);
        exch.settleBatch(keccak256("b1"), trades);
    }

    function test_settleBatch_zeroSize() public {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(keccak256("t1"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 400, 0);
        vm.prank(operator);
        vm.expectRevert(abi.encodeWithSelector(CTFExchangeLite.SettleBatchFailed.selector, 0, keccak256("t1")));
        exch.settleBatch(keccak256("b1"), trades);
    }

    function test_settleBatch_tickOutOfRange() public {
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = _trade(keccak256("t1"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 1001, 10e6);
        vm.prank(operator);
        vm.expectRevert(abi.encodeWithSelector(CTFExchangeLite.TickOutOfRange.selector, 1001));
        exch.settleBatch(keccak256("b1"), trades);
    }

    function test_settleBatch_insufficientMakerToken() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket();
        _mintBasePositions();
        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        // alice holds 50 YES; selling 60 should fail with the failing index.
        trades[0] = _trade(keccak256("t2"), CTFExchangeLite.TradeClass.TRANSFER, IOutcomeTokens.Outcome.YES, alice, bob, 400, 60e6);
        vm.prank(operator);
        vm.expectRevert(abi.encodeWithSelector(CTFExchangeLite.SettleBatchFailed.selector, 0, keccak256("t2")));
        exch.settleBatch(keccak256("b2"), trades);
    }
}
