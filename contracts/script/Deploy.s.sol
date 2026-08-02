// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Script, console2} from "forge-std/Script.sol";
import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";

import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {Vault} from "../src/Vault.sol";
import {CTFExchangeLite} from "../src/CTFExchangeLite.sol";
import {RFM} from "../src/RFM.sol";

/// @title Deploy
/// @notice Arc testnet deployment. Reads secrets from env (never committed).
///         env: PRIVATE_KEY, OPERATOR_ADDRESS, optionally USDC_ADDRESS.
/// @dev USDC defaults to the canonical Arc ERC-20 face (6-dec view of the native
///      18-dec USDC balance); override via USDC_ADDRESS for other chains.
contract Deploy is Script {
    /// @dev Canonical Arc testnet USDC ERC-20 face (STACK.md token registry).
    address internal constant ARC_USDC = 0x3600000000000000000000000000000000000000;

    function run() external returns (address, address, address, address) {
        uint256 pk = vm.envUint("PRIVATE_KEY");
        address deployer = vm.addr(pk);
        address operator = vm.envAddress("OPERATOR_ADDRESS");
        IERC20 usdc = IERC20(vm.envOr("USDC_ADDRESS", ARC_USDC));

        vm.startBroadcast(pk);

        OutcomeTokens ot = new OutcomeTokens(usdc, operator, deployer);
        Vault vault = new Vault(usdc, address(ot), deployer);
        CTFExchangeLite exch = new CTFExchangeLite(address(vault), address(ot), operator);
        RFM rfm = new RFM(address(vault), address(ot));

        // One-shot role wiring: the cyclic deploy edges are frozen here and can
        // never be changed again (no persistent owner authority).
        ot.setRoles(address(vault), address(rfm));
        vault.setRoles(address(exch), address(rfm));

        vm.stopBroadcast();

        console2.log("OutcomeTokens:", address(ot));
        console2.log("Vault:", address(vault));
        console2.log("CTFExchangeLite:", address(exch));
        console2.log("RFM:", address(rfm));
        console2.log("operator:", operator);
        console2.log("usdc:", address(usdc));

        return (address(ot), address(vault), address(exch), address(rfm));
    }
}
