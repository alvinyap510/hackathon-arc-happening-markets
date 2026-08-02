// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Test} from "forge-std/Test.sol";

import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {IOutcomeTokens} from "../src/interfaces/IOutcomeTokens.sol";
import {MockUSDC} from "./mocks/MockUSDC.sol";

contract OutcomeTokensTest is Test {

    MockUSDC usdc;
    OutcomeTokens ot;

    address vault = makeAddr("vault");
    address rfm = makeAddr("rfm");
    address operator = makeAddr("operator");
    address trader = makeAddr("trader");
    address stranger = makeAddr("stranger");

    bytes32 marketId = keccak256("m1");
    bytes32 marketId2 = keccak256("m2");
    uint256 yesId;
    uint256 noId;

    function setUp() public {
        usdc = new MockUSDC();
        ot = new OutcomeTokens(usdc, operator, address(this));
        ot.setRoles(vault, rfm);
        yesId = ot.tokenId(marketId, IOutcomeTokens.Outcome.YES);
        noId = ot.tokenId(marketId, IOutcomeTokens.Outcome.NO);
    }

    // ---------------------------------------------------------------- helpers

    function _operatorCreate(bytes32 mid) internal {
        vm.prank(operator);
        ot.createMarket(mid, "");
    }

    function _fundPool(bytes32 mid, uint256 size) internal {
        usdc.mint(vault, size);
        vm.prank(vault);
        usdc.approve(address(ot), size);
        vm.prank(vault);
        ot.split(mid, size);
    }

    function _resolve(bytes32 mid, IOutcomeTokens.Outcome winner) internal {
        vm.prank(operator);
        ot.resolve(mid, winner);
    }

    // ---------------------------------------------------------------- reserve

    function test_reserveMarket_onlyRfm() public {
        vm.prank(rfm);
        ot.reserveMarket(marketId);
        (bool reserved, bool exists,,,) = ot.markets(marketId);
        assertTrue(reserved);
        assertFalse(exists);
    }

    function test_reserveMarket_revertsForNonRfm() public {
        vm.expectRevert(OutcomeTokens.Unauthorized.selector);
        vm.prank(stranger);
        ot.reserveMarket(marketId);
    }

    function test_reserveMarket_revertsWhenDoubleReserved() public {
        vm.prank(rfm);
        ot.reserveMarket(marketId);
        vm.expectRevert(OutcomeTokens.AlreadyReserved.selector);
        vm.prank(rfm);
        ot.reserveMarket(marketId);
    }

    function test_reserveMarket_revertsWhenExists() public {
        _operatorCreate(marketId);
        vm.expectRevert(OutcomeTokens.AlreadyReserved.selector);
        vm.prank(rfm);
        ot.reserveMarket(marketId);
    }

    // --------------------------------------------------------------- create

    function test_createMarket_rfmRequiresReserved() public {
        vm.expectRevert(OutcomeTokens.NotReserved.selector);
        vm.prank(rfm);
        ot.createMarket(marketId, "");
    }

    function test_createMarket_rfmAfterReserve() public {
        vm.prank(rfm);
        ot.reserveMarket(marketId);
        vm.prank(rfm);
        ot.createMarket(marketId, "");
        (, bool exists,,,) = ot.markets(marketId);
        assertTrue(exists);
    }

    function test_createMarket_rfmRevertsWhenDouble() public {
        vm.prank(rfm);
        ot.reserveMarket(marketId);
        vm.prank(rfm);
        ot.createMarket(marketId, "");
        vm.expectRevert(OutcomeTokens.AlreadyExists.selector);
        vm.prank(rfm);
        ot.createMarket(marketId, "");
    }

    function test_createMarket_operatorFreshId() public {
        _operatorCreate(marketId2);
        (, bool exists,,,) = ot.markets(marketId2);
        assertTrue(exists);
    }

    function test_createMarket_operatorCannotTouchReservedId() public {
        vm.prank(rfm);
        ot.reserveMarket(marketId);
        vm.expectRevert(OutcomeTokens.ReservedId.selector);
        vm.prank(operator);
        ot.createMarket(marketId, "");
    }

    function test_createMarket_revertsForStranger() public {
        vm.expectRevert(OutcomeTokens.Unauthorized.selector);
        ot.createMarket(marketId, "");
    }

    // ----------------------------------------------------------------- split

    function test_split_onlyVault() public {
        _operatorCreate(marketId);
        usdc.mint(vault, 100e6);
        vm.expectRevert(OutcomeTokens.Unauthorized.selector);
        vm.prank(stranger);
        ot.split(marketId, 100e6);
    }

    function test_split_mintsPairAndPools() public {
        _operatorCreate(marketId);
        _fundPool(marketId, 250e6);
        assertEq(usdc.balanceOf(address(ot)), 250e6);
        (,,,, uint256 pool) = ot.markets(marketId);
        assertEq(pool, 250e6);
        assertEq(ot.balanceOf(vault, yesId), 250e6);
        assertEq(ot.balanceOf(vault, noId), 250e6);
        assertEq(ot.totalSupply(yesId), 250e6);
        assertEq(ot.totalSupply(noId), 250e6);
    }

    function test_split_requiresExists() public {
        vm.expectRevert(OutcomeTokens.NotExists.selector);
        vm.prank(vault);
        ot.split(marketId, 100e6);
    }

    function test_split_revertsZero() public {
        _operatorCreate(marketId);
        vm.expectRevert(OutcomeTokens.ZeroAmount.selector);
        vm.prank(vault);
        ot.split(marketId, 0);
    }

    // ----------------------------------------------------------------- merge

    function test_merge_burnsPairAndReleasesPool() public {
        _operatorCreate(marketId);
        _fundPool(marketId, 250e6);
        vm.prank(vault);
        ot.merge(marketId, 90e6);
        (,,,, uint256 pool) = ot.markets(marketId);
        assertEq(pool, 160e6);
        assertEq(ot.balanceOf(vault, yesId), 160e6);
        assertEq(ot.balanceOf(vault, noId), 160e6);
        assertEq(usdc.balanceOf(vault), 90e6);
    }

    function test_merge_onlyVault() public {
        _operatorCreate(marketId);
        _fundPool(marketId, 250e6);
        vm.expectRevert(OutcomeTokens.Unauthorized.selector);
        ot.merge(marketId, 10e6);
    }

    // ---------------------------------------------------------------- resolve

    function test_resolve_operatorOnlyOneShot() public {
        _operatorCreate(marketId);
        vm.prank(stranger);
        vm.expectRevert(OutcomeTokens.Unauthorized.selector);
        ot.resolve(marketId, IOutcomeTokens.Outcome.YES);

        _resolve(marketId, IOutcomeTokens.Outcome.NO);
        assertTrue(ot.isResolved(marketId));
        assertEq(uint256(ot.winningOutcome(marketId)), uint256(IOutcomeTokens.Outcome.NO));

        vm.prank(operator);
        vm.expectRevert(OutcomeTokens.AlreadyResolved.selector);
        ot.resolve(marketId, IOutcomeTokens.Outcome.YES);
    }

    function test_resolve_requiresExists() public {
        vm.prank(operator);
        vm.expectRevert(OutcomeTokens.NotExists.selector);
        ot.resolve(marketId, IOutcomeTokens.Outcome.YES);
    }

    // ----------------------------------------------------------------- redeem

    function test_redeem_winningOneToOne() public {
        _operatorCreate(marketId);
        _fundPool(marketId, 250e6);
        // Move tokens to a trader via mock transfer, then redeem wallet-held.
        vm.prank(vault);
        ot.safeTransferFrom(vault, trader, yesId, 40e6, "");
        _resolve(marketId, IOutcomeTokens.Outcome.YES);

        vm.prank(trader);
        ot.redeem(marketId, 40e6);
        assertEq(usdc.balanceOf(trader), 40e6);
        assertEq(ot.balanceOf(trader, yesId), 0);
        assertEq(ot.balanceOf(trader, noId), 0); // trader never held NO; losing tokens are worthless
        (,,,, uint256 pool) = ot.markets(marketId);
        assertEq(pool, 210e6);
    }

    function test_redeem_losingReverts() public {
        _operatorCreate(marketId);
        _fundPool(marketId, 250e6);
        vm.prank(vault);
        ot.safeTransferFrom(vault, trader, noId, 40e6, "");
        _resolve(marketId, IOutcomeTokens.Outcome.YES);
        vm.expectRevert(); // no winning balance to burn
        vm.prank(trader);
        ot.redeem(marketId, 40e6);
    }

    function test_redeem_permissionlessBeforeResolveReverts() public {
        _operatorCreate(marketId);
        _fundPool(marketId, 100e6);
        vm.prank(vault);
        ot.safeTransferFrom(vault, trader, yesId, 10e6, "");
        vm.expectRevert(OutcomeTokens.Unauthorized.selector);
        vm.prank(trader);
        ot.redeem(marketId, 10e6);
    }

    // ------------------------------------------------------- supply invariant

    function test_phaseAwareSupplyInvariant() public {
        // Pre-resolve: outstanding supply per outcome == pool collateral.
        _operatorCreate(marketId);
        _fundPool(marketId, 100e6);
        _fundPool(marketId, 50e6);
        (,,,, uint256 pool) = ot.markets(marketId);
        assertEq(ot.totalSupply(yesId), pool);
        assertEq(ot.totalSupply(noId), pool);

        // Post-resolve: remaining winning supply == remaining pool.
        vm.prank(vault);
        ot.safeTransferFrom(vault, trader, yesId, 30e6, "");
        _resolve(marketId, IOutcomeTokens.Outcome.YES);
        vm.prank(trader);
        ot.redeem(marketId, 30e6);
        (,,,, pool) = ot.markets(marketId);
        assertEq(ot.totalSupply(yesId), pool);
        assertEq(usdc.balanceOf(address(ot)), pool);
    }

    function test_tokenIdsAreDerivedFixedWidth() public view {
        bytes32 m1 = keccak256("a");
        bytes32 m2 = keccak256("b");
        assertTrue(ot.tokenId(m1, IOutcomeTokens.Outcome.YES) != ot.tokenId(m1, IOutcomeTokens.Outcome.NO));
        assertTrue(ot.tokenId(m1, IOutcomeTokens.Outcome.YES) != ot.tokenId(m2, IOutcomeTokens.Outcome.YES));
        // abi.encode is fixed-width: no accidental collision between (m1,YES) and (m2,YES).
        assertEq(
            uint256(keccak256(abi.encode(m1, IOutcomeTokens.Outcome.YES))),
            ot.tokenId(m1, IOutcomeTokens.Outcome.YES)
        );
    }

    function test_setRolesIsOneShot() public {
        vm.expectRevert(OutcomeTokens.RolesAlreadySet.selector);
        ot.setRoles(address(0xBAD), address(0xFEE));
    }

    function test_setRoles_onlyDeployer() public {
        vm.prank(stranger);
        vm.expectRevert(OutcomeTokens.Unauthorized.selector);
        ot.setRoles(address(0xBAD), address(0xFEE));
    }

    function test_setRoles_rejectsZeroAddress() public {
        OutcomeTokens fresh = new OutcomeTokens(usdc, operator, address(this));
        vm.expectRevert(OutcomeTokens.ZeroAddress.selector);
        fresh.setRoles(address(0), makeAddr("rfm"));
        vm.expectRevert(OutcomeTokens.ZeroAddress.selector);
        fresh.setRoles(makeAddr("vault"), address(0));
    }
}
