// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Test} from "forge-std/Test.sol";
import {IERC1155Receiver} from "@openzeppelin/contracts/token/ERC1155/IERC1155Receiver.sol";

import {Vault} from "../src/Vault.sol";
import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {IVault} from "../src/interfaces/IVault.sol";
import {IOutcomeTokens} from "../src/interfaces/IOutcomeTokens.sol";
import {MockUSDC} from "./mocks/MockUSDC.sol";

contract VaultTest is Test {
    MockUSDC usdc;
    OutcomeTokens ot;
    Vault vault;

    address exchange = makeAddr("exchange");
    address rfm = makeAddr("rfm");
    address operator = makeAddr("operator");
    address alice = makeAddr("alice");
    address bob = makeAddr("bob");
    address stranger = makeAddr("stranger");

    bytes32 marketId = keccak256("vault-m1");
    uint256 yesId;
    uint256 noId;

    function setUp() public {
        usdc = new MockUSDC();
        ot = new OutcomeTokens(usdc, operator, address(this));
        vault = new Vault(usdc, address(ot), address(this));
        ot.setRoles(address(vault), rfm);
        vault.setRoles(exchange, rfm);
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

    function _lock(address user, uint256 amt, bytes32 ref) internal {
        vm.prank(rfm);
        vault.lock(user, amt, ref);
    }

    function _createMarket(bytes32 mid) internal {
        vm.prank(operator);
        ot.createMarket(mid, "");
    }

    function _mintPair(
        bytes32 mid,
        IVault.Allocation[] memory yesAlloc,
        IVault.Allocation[] memory noAlloc,
        IVault.Funding[] memory funding,
        uint256 size
    ) internal {
        vm.prank(exchange);
        vault.mintPair(mid, yesAlloc, noAlloc, funding, size);
    }

    // ------------------------------------------------------ deposit/withdraw

    function test_depositWithdraw() public {
        _deposit(alice, 100e6);
        assertEq(vault.usdcBal(alice), 100e6);
        assertEq(usdc.balanceOf(address(vault)), 100e6);
        vm.prank(alice);
        vault.withdraw(40e6);
        assertEq(vault.usdcBal(alice), 60e6);
        assertEq(usdc.balanceOf(alice), 40e6);
    }

    function test_withdrawBoundedByFreeBalance() public {
        _deposit(alice, 100e6);
        _lock(alice, 60e6, keccak256("ref1"));
        vm.prank(alice);
        vault.withdraw(40e6); // free = 40
        vm.expectRevert(Vault.InsufficientFree.selector);
        vm.prank(alice);
        vault.withdraw(1e6);
        assertEq(vault.lockedBal(alice), 60e6);
    }

    function test_withdrawStillWorksWhileLocksOutstanding() public {
        _deposit(alice, 100e6);
        _lock(alice, 60e6, keccak256("ref1"));
        vm.prank(alice);
        vault.withdraw(40e6);
        assertEq(usdc.balanceOf(alice), 40e6); // nothing froze exit
    }

    function test_withdrawZeroReverts() public {
        vm.expectRevert(Vault.ZeroAmount.selector);
        vm.prank(alice);
        vault.withdraw(0);
    }

    // --------------------------------------------------------------- moves

    function test_moveUSDC_exchangeOnly() public {
        _deposit(alice, 100e6);
        vm.expectRevert(Vault.Unauthorized.selector);
        vm.prank(stranger);
        vault.moveUSDC(alice, bob, 10e6, keccak256("t1"));

        vm.prank(exchange);
        vault.moveUSDC(alice, bob, 10e6, keccak256("t1"));
        assertEq(vault.usdcBal(alice), 90e6);
        assertEq(vault.usdcBal(bob), 10e6);
    }

    function test_moveUSDC_respectsFree() public {
        _deposit(alice, 100e6);
        _lock(alice, 60e6, keccak256("ref1"));
        vm.expectRevert(Vault.InsufficientFree.selector);
        vm.prank(exchange);
        vault.moveUSDC(alice, bob, 60e6, keccak256("t1")); // 60 > free 40
    }

    function test_moveTokens_exchangeOnly() public {
        _deposit(alice, 100e6);
        _lock(alice, 100e6, keccak256("fund"));
        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 100e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(bob, 100e6);
        IVault.Funding[] memory fund = new IVault.Funding[](1);
        fund[0] = IVault.Funding(IVault.FundingKind.LOCK, keccak256("fund"), alice, 100e6);
        _createMarket(marketId);
        _mintPair(marketId, yes, no, fund, 100e6);

        vm.expectRevert(Vault.Unauthorized.selector);
        vm.prank(stranger);
        vault.moveTokens(alice, bob, yesId, 10e6, keccak256("t1"));

        vm.prank(exchange);
        vault.moveTokens(alice, bob, yesId, 10e6, keccak256("t1"));
        assertEq(vault.tokenBal(alice, yesId), 90e6);
        assertEq(vault.tokenBal(bob, yesId), 10e6);
    }

    // -------------------------------------------------------------- locks

    function test_lockReleasePartial() public {
        _deposit(alice, 100e6);
        bytes32 ref = keccak256("esc");
        _lock(alice, 40e6, ref);
        assertEq(vault.lockedBal(alice), 40e6);
        assertEq(vault.freeBal(alice), 60e6);

        vm.prank(rfm);
        vault.releaseLock(ref, 10e6);
        assertEq(vault.lockedBal(alice), 30e6);
        assertEq(vault.freeBal(alice), 70e6);

        vm.prank(rfm);
        vault.releaseLock(ref, 30e6); // drains it
        assertEq(vault.lockedBal(alice), 0);
        (,, bool live) = vault.locks(ref);
        assertFalse(live);
    }

    function test_consumeLockCreditsTarget() public {
        _deposit(alice, 100e6);
        bytes32 ref = keccak256("bond");
        _lock(alice, 50e6, ref);
        vm.prank(rfm);
        vault.consumeLock(ref, 50e6, bob);
        assertEq(vault.lockedBal(alice), 0);
        assertEq(vault.usdcBal(alice), 50e6);
        assertEq(vault.usdcBal(bob), 50e6);
        assertEq(vault.freeBal(bob), 50e6);
        (,, bool live) = vault.locks(ref);
        assertFalse(live);
    }

    function test_lockRevertsRefInUse() public {
        _deposit(alice, 100e6);
        bytes32 ref = keccak256("ref1");
        _lock(alice, 10e6, ref);
        vm.expectRevert(Vault.RefInUse.selector);
        _lock(alice, 10e6, ref);
    }

    function test_lockRevertsInsufficientFree() public {
        _deposit(alice, 100e6);
        vm.expectRevert(Vault.InsufficientFree.selector);
        _lock(alice, 101e6, keccak256("ref1"));
    }

    function test_lockRevertsReleaseExcess() public {
        _deposit(alice, 100e6);
        bytes32 ref = keccak256("ref1");
        _lock(alice, 10e6, ref);
        vm.expectRevert(Vault.InsufficientBalance.selector);
        vm.prank(rfm);
        vault.releaseLock(ref, 11e6);
    }

    // -------------------------------------------------------------- mintPair

    function test_mintPair_lockFunding() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket(marketId);

        bytes32 fundRef = keccak256("fund");
        _lock(alice, 100e6, fundRef);

        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 100e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(bob, 100e6);
        IVault.Funding[] memory fund = new IVault.Funding[](1);
        fund[0] = IVault.Funding(IVault.FundingKind.LOCK, fundRef, alice, 100e6);

        _mintPair(marketId, yes, no, fund, 100e6);

        // Lock consumed; allocations credited; pool funded; physical conserved.
        (,, bool live) = vault.locks(fundRef);
        assertFalse(live);
        assertEq(vault.lockedBal(alice), 0);
        assertEq(vault.usdcBal(alice), 0);
        assertEq(vault.tokenBal(alice, yesId), 100e6);
        assertEq(vault.tokenBal(bob, noId), 100e6);
        assertEq(usdc.balanceOf(address(ot)), 100e6);
        assertEq(usdc.balanceOf(address(vault)), 100e6); // bob's 100 still custodied
        assertEq(ot.totalSupply(yesId), 100e6);
    }

    function test_mintPair_freeFunding() public {
        _deposit(alice, 100e6);
        _deposit(bob, 100e6);
        _createMarket(marketId);

        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 80e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(bob, 80e6);
        IVault.Funding[] memory fund = new IVault.Funding[](2);
        fund[0] = IVault.Funding(IVault.FundingKind.FREE, bytes32(0), alice, 40e6);
        fund[1] = IVault.Funding(IVault.FundingKind.FREE, bytes32(0), bob, 40e6);

        _mintPair(marketId, yes, no, fund, 80e6);
        assertEq(vault.usdcBal(alice), 60e6);
        assertEq(vault.usdcBal(bob), 60e6);
        assertEq(vault.tokenBal(alice, yesId), 80e6);
        assertEq(vault.tokenBal(bob, noId), 80e6);
    }

    function test_mintPair_sumsEnforced() public {
        _deposit(alice, 100e6);
        _createMarket(marketId);
        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 50e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(alice, 50e6);
        IVault.Funding[] memory fund = new IVault.Funding[](1);
        fund[0] = IVault.Funding(IVault.FundingKind.FREE, bytes32(0), alice, 100e6);
        vm.expectRevert(Vault.SumMismatch.selector); // yes side sums to 50 != size 100
        _mintPair(marketId, yes, no, fund, 100e6);
    }

    function test_mintPair_onlyExchangeOrRfm() public {
        _deposit(alice, 100e6);
        _createMarket(marketId);
        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 100e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(alice, 100e6);
        IVault.Funding[] memory fund = new IVault.Funding[](1);
        fund[0] = IVault.Funding(IVault.FundingKind.FREE, bytes32(0), alice, 100e6);
        vm.prank(stranger);
        vm.expectRevert(Vault.Unauthorized.selector);
        vault.mintPair(marketId, yes, no, fund, 100e6);
    }

    // -------------------------------------------------------------- burnPair

    function test_burnPair_creditsSplit() public {
        _deposit(alice, 100e6);
        _createMarket(marketId);
        bytes32 fundRef = keccak256("fund");
        _lock(alice, 100e6, fundRef);
        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 100e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(bob, 100e6);
        IVault.Funding[] memory fund = new IVault.Funding[](1);
        fund[0] = IVault.Funding(IVault.FundingKind.LOCK, fundRef, alice, 100e6);
        _mintPair(marketId, yes, no, fund, 100e6);

        vm.prank(exchange);
        vault.burnPair(marketId, alice, bob, 100e6, 40e6);
        assertEq(vault.tokenBal(alice, yesId), 0);
        assertEq(vault.tokenBal(bob, noId), 0);
        assertEq(vault.usdcBal(alice), 40e6);
        assertEq(vault.usdcBal(bob), 60e6);
        assertEq(usdc.balanceOf(address(vault)), 100e6); // pool returned to Vault
        assertEq(usdc.balanceOf(address(ot)), 0);
    }

    function test_burnPair_onlyExchange() public {
        _deposit(alice, 100e6);
        _createMarket(marketId);
        vm.expectRevert(Vault.Unauthorized.selector);
        vm.prank(stranger);
        vault.burnPair(marketId, alice, bob, 10e6, 0);
    }

    // ---------------------------------------------------------------- redeem

    function test_redeem_vaultCustodied() public {
        _deposit(alice, 100e6);
        _createMarket(marketId);
        bytes32 fundRef = keccak256("fund");
        _lock(alice, 100e6, fundRef);
        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 60e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(bob, 60e6);
        IVault.Funding[] memory fund = new IVault.Funding[](1);
        fund[0] = IVault.Funding(IVault.FundingKind.LOCK, fundRef, alice, 60e6);
        _mintPair(marketId, yes, no, fund, 60e6);

        vm.prank(operator);
        ot.resolve(marketId, IOutcomeTokens.Outcome.YES);

        vm.prank(alice);
        vault.redeem(marketId, 40e6);
        assertEq(vault.tokenBal(alice, yesId), 20e6);
        assertEq(vault.usdcBal(alice), 80e6); // 40 remaining after funding + 40 redeemed
        assertEq(usdc.balanceOf(address(vault)), 80e6); // bob's 40 custodied + 40 redeemed out of pool
    }

    function test_redeem_beforeResolveReverts() public {
        _deposit(alice, 100e6);
        _createMarket(marketId);
        vm.expectRevert(Vault.NotResolved.selector);
        vm.prank(alice);
        vault.redeem(marketId, 1e6);
    }

    // ------------------------------------------------------------- token custody

    function test_depositWithdrawTokens() public {
        // Create tokens via a mintPair (Vault receives physical 1155s), then withdraw
        // and re-deposit wallet-held tokens through the custody surface.
        _deposit(alice, 100e6);
        _createMarket(marketId);
        bytes32 fundRef = keccak256("fund");
        _lock(alice, 100e6, fundRef);
        IVault.Allocation[] memory yes = new IVault.Allocation[](1);
        yes[0] = IVault.Allocation(alice, 100e6);
        IVault.Allocation[] memory no = new IVault.Allocation[](1);
        no[0] = IVault.Allocation(bob, 100e6);
        IVault.Funding[] memory fund = new IVault.Funding[](1);
        fund[0] = IVault.Funding(IVault.FundingKind.LOCK, fundRef, alice, 100e6);
        _mintPair(marketId, yes, no, fund, 100e6);

        vm.prank(alice);
        vault.withdrawTokens(yesId, 30e6);
        assertEq(vault.tokenBal(alice, yesId), 70e6);
        assertEq(ot.balanceOf(alice, yesId), 30e6);

        vm.prank(alice);
        ot.setApprovalForAll(address(vault), true);
        vm.prank(alice);
        vault.depositTokens(yesId, 30e6);
        assertEq(vault.tokenBal(alice, yesId), 100e6);
        assertEq(ot.balanceOf(address(vault), yesId), 100e6);
    }

    // ------------------------------------------------------------- receiver

    function test_supportsReceiverInterface() public {
        assertTrue(vault.supportsInterface(type(IERC1155Receiver).interfaceId));
    }

    // ---------------------------------------------------------------- roles

    function test_setRolesOneShot() public {
        vm.expectRevert(Vault.RolesAlreadySet.selector);
        vault.setRoles(address(0xBAD), address(0xFEE));
    }

    function test_setRolesOnlyDeployer() public {
        vm.prank(stranger);
        vm.expectRevert(Vault.Unauthorized.selector);
        vault.setRoles(address(0xBAD), address(0xFEE));
    }
}
