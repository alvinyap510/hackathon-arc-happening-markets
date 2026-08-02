// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Test} from "forge-std/Test.sol";

import {RFM} from "../src/RFM.sol";
import {Vault} from "../src/Vault.sol";
import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {CTFExchangeLite} from "../src/CTFExchangeLite.sol";
import {IVault} from "../src/interfaces/IVault.sol";
import {IRFM} from "../src/interfaces/IRFM.sol";
import {IOutcomeTokens} from "../src/interfaces/IOutcomeTokens.sol";
import {MockUSDC} from "./mocks/MockUSDC.sol";

/// @notice Money-conservation invariant harness. Handlers drive deposit / withdraw,
///         full RFM rounds (post -> commit -> reveal -> finalize -> resolve -> redeem)
///         and a settleBatch MINT. Invariants assert, after every call:
///          1. Vault USDC == sum of internal usdcBal; pool == sum of per-market pools;
///             per-token physical 1155 >= sum of internal tokenBal.
///          2. Phase-aware supply: per market, pre-resolve supply(YES) == supply(NO) ==
///             collateralPool; post-resolve remaining winning supply == remaining pool.
contract RFMStateHandler is Test {
    MockUSDC usdc;
    OutcomeTokens ot;
    Vault vault;
    CTFExchangeLite exch;
    RFM rfm;

    address operator = makeAddr("operator");
    address institution = makeAddr("institution");
    address[4] mms = [makeAddr("mm0"), makeAddr("mm1"), makeAddr("mm2"), makeAddr("mm3")];
    address[] internal allUsers;

    bytes32[] internal markets;

    uint256 internal constant BOND = 500e6;
    uint256 internal nonce;

    constructor(address usdc_, address ot_, address vault_, address exch_, address rfm_) {
        usdc = MockUSDC(usdc_);
        ot = OutcomeTokens(ot_);
        vault = Vault(vault_);
        exch = CTFExchangeLite(exch_);
        rfm = RFM(rfm_);
        allUsers.push(institution);
        for (uint256 i = 0; i < 4; ++i) {
            allUsers.push(mms[i]);
        }
    }

    function users() external view returns (address[] memory) {
        return allUsers;
    }

    function marketsCount() external view returns (uint256) {
        return markets.length;
    }

    function marketAt(uint256 i) external view returns (bytes32) {
        return markets[i];
    }

    function deposit(uint256 u, uint256 amt) external {
        address user = allUsers[u % allUsers.length];
        amt = bound(amt, 1e6, 200e6);
        usdc.mint(user, amt);
        vm.startPrank(user);
        usdc.approve(address(vault), amt);
        vault.deposit(amt);
        vm.stopPrank();
    }

    function withdraw(uint256 u, uint256 amt) external {
        address user = allUsers[u % allUsers.length];
        amt = bound(amt, 0, vault.freeBal(user));
        if (amt == 0) return;
        vm.prank(user);
        vault.withdraw(amt);
    }

    function settleMint(uint256 u1, uint256 u2, uint256 seedTick, uint256 seedSize) external {
        address a = allUsers[u1 % allUsers.length];
        address b = allUsers[u2 % allUsers.length];
        if (markets.length == 0) return;
        bytes32 marketId = markets[markets.length - 1];
        (, bool mExists,,,) = ot.markets(marketId);
        if (!mExists) return;
        uint256 size = bound(seedSize, 1e6, 100e6);
        uint256 tick = bound(seedTick, 1, 999);
        uint256 yesCost = (size * tick) / 1000;
        uint256 noCost = size - yesCost;
        if (vault.freeBal(a) < yesCost || vault.freeBal(b) < noCost) return;
        ++nonce;

        CTFExchangeLite.Trade[] memory trades = new CTFExchangeLite.Trade[](1);
        trades[0] = CTFExchangeLite.Trade(
            keccak256(abi.encode(nonce, "t")),
            marketId,
            CTFExchangeLite.TradeClass.MINT,
            IOutcomeTokens.Outcome.YES,
            a,
            b,
            tick,
            size
        );
        vm.prank(operator);
        exch.settleBatch(keccak256(abi.encode(nonce, "b")), trades);
    }

    /// @notice Runs a complete auction round (post -> commits -> reveal -> finalize
    ///         -> resolve -> redeem). Deterministic given the seed; always valid.
    function runFullRfmRound(uint256 seed) external {
        seed = seed % 1_000_000_000; // keep derived products overflow-safe
        // Fund institution (escrow + bond + slack).
        uint256 instNeed = 700e6;
        if (vault.freeBal(institution) < instNeed) {
            usdc.mint(institution, instNeed);
            vm.startPrank(institution);
            usdc.approve(address(vault), instNeed);
            vault.deposit(instNeed);
            vm.stopPrank();
        }

        uint256 quantity = 200e6;
        uint256 maxTick = 600;
        uint256 minMatch = 80e6;
        IRFM.Side side = seed % 2 == 0 ? IRFM.Side.YES : IRFM.Side.NO;
        vm.prank(institution);
        uint256 requestId = rfm.postRequest(
            keccak256(abi.encode(seed, block.timestamp)), side, quantity, maxTick, minMatch,
            block.timestamp + 1000, block.timestamp + 2000
        );

        uint256 numMMs = 2 + (seed % 3); // 2..4
        uint256[] memory ticks = new uint256[](numMMs);
        uint256[] memory sizes = new uint256[](numMMs);
        for (uint256 i = 0; i < numMMs; ++i) {
            address mm = mms[(seed + i) % 4];
            uint256 tick = 300 + ((seed + i * 50) % 300); // 300..599 (in range)
            uint256 size = 60e6 + ((seed * (i + 1)) % 40e6); // 60..100e6
            ticks[i] = tick;
            sizes[i] = size;
            uint256 lock = size - (size * tick) / 1000;
            uint256 need = BOND + lock;
            if (vault.freeBal(mm) < need) {
                usdc.mint(mm, need);
                vm.startPrank(mm);
                usdc.approve(address(vault), need);
                vault.deposit(need);
                vm.stopPrank();
            }
            bytes32 h = keccak256(abi.encode(block.chainid, address(rfm), requestId, mm, tick, size, i));
            vm.prank(mm);
            rfm.commitQuote(requestId, h);
        }

        vm.warp(block.timestamp + 1001); // commit window closed, reveal open
        for (uint256 i = 0; i < numMMs; ++i) {
            address mm = mms[(seed + i) % 4];
            vm.prank(mm);
            rfm.revealQuote(requestId, ticks[i], sizes[i], i);
        }
        vm.warp(block.timestamp + 1001); // past reveal deadline
        rfm.finalize(requestId);

        bytes32 marketId = rfm.marketIdOf(requestId);
        // Resolve in about a third of rounds, leaving the rest pre-resolve so the
        // phase-aware supply invariant is exercised in both states.
        if (seed % 3 == 0) {
            vm.prank(operator);
            ot.resolve(marketId, side == IRFM.Side.YES ? IOutcomeTokens.Outcome.YES : IOutcomeTokens.Outcome.NO);

            IOutcomeTokens.Outcome win = side == IRFM.Side.YES ? IOutcomeTokens.Outcome.YES : IOutcomeTokens.Outcome.NO;
            uint256 winId = ot.tokenId(marketId, win);
            uint256 bal = vault.tokenBal(institution, winId);
            if (bal > 0) {
                vm.prank(institution);
                vault.redeem(marketId, bal);
            }
        }

        markets.push(marketId);
    }
}

contract InvariantsTest is Test {
    MockUSDC usdc;
    OutcomeTokens ot;
    Vault vault;
    CTFExchangeLite exch;
    RFM rfm;
    RFMStateHandler handler;

    function setUp() public {
        usdc = new MockUSDC();
        ot = new OutcomeTokens(usdc, makeAddr("operator"), address(this));
        vault = new Vault(usdc, address(ot), address(this));
        exch = new CTFExchangeLite(address(vault), address(ot), makeAddr("operator"));
        rfm = new RFM(address(vault), address(ot));
        ot.setRoles(address(vault), address(rfm));
        vault.setRoles(address(exch), address(rfm));

        handler = new RFMStateHandler(address(usdc), address(ot), address(vault), address(exch), address(rfm));
        targetContract(address(handler));
    }

    function invariant_physicalAssetsGTEInternal() public view {
        // Vault's physical USDC exactly matches the sum of internal balances.
        uint256 sum = 0;
        address[] memory users = handler.users();
        for (uint256 i = 0; i < users.length; ++i) {
            sum += vault.usdcBal(users[i]);
        }
        assertEq(usdc.balanceOf(address(vault)), sum);

        // Pool physical USDC equals the sum of per-market collateral pools.
        uint256 poolSum = 0;
        for (uint256 i = 0; i < handler.marketsCount(); ++i) {
            bytes32 m = handler.marketAt(i);
            (,,,, uint256 pool) = ot.markets(m);
            poolSum += pool;
        }
        assertEq(usdc.balanceOf(address(ot)), poolSum);

        // Venue-custodied 1155s: physical >= internal tokenBal per (market, outcome).
        for (uint256 i = 0; i < handler.marketsCount(); ++i) {
            bytes32 m = handler.marketAt(i);
            uint256 yesId = ot.tokenId(m, IOutcomeTokens.Outcome.YES);
            uint256 noId = ot.tokenId(m, IOutcomeTokens.Outcome.NO);
            uint256 internalYes;
            uint256 internalNo;
            for (uint256 j = 0; j < users.length; ++j) {
                internalYes += vault.tokenBal(users[j], yesId);
                internalNo += vault.tokenBal(users[j], noId);
            }
            assertGe(ot.balanceOf(address(vault), yesId), internalYes);
            assertGe(ot.balanceOf(address(vault), noId), internalNo);
        }
    }

    function invariant_phaseAwareSupply() public view {
        for (uint256 i = 0; i < handler.marketsCount(); ++i) {
            bytes32 m = handler.marketAt(i);
            (, bool exists, bool resolved, IOutcomeTokens.Outcome winning, uint256 pool) = ot.markets(m);
            if (!exists) continue;
            uint256 yesId = ot.tokenId(m, IOutcomeTokens.Outcome.YES);
            uint256 noId = ot.tokenId(m, IOutcomeTokens.Outcome.NO);
            if (!resolved) {
                // Pre-resolve: each outcome's outstanding supply == pool collateral.
                assertEq(ot.totalSupply(yesId), pool);
                assertEq(ot.totalSupply(noId), pool);
            } else {
                // Post-resolve: remaining winning supply == remaining pool.
                uint256 winningSupply =
                    winning == IOutcomeTokens.Outcome.YES ? ot.totalSupply(yesId) : ot.totalSupply(noId);
                assertEq(winningSupply, pool);
            }
        }
    }
}
