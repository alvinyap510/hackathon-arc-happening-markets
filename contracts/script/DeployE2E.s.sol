// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {Script, console2} from "forge-std/Script.sol";
import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";

import {MockUSDC} from "../src/mocks/MockUSDC.sol";
import {OutcomeTokens} from "../src/OutcomeTokens.sol";
import {Vault} from "../src/Vault.sol";
import {CTFExchangeLite} from "../src/CTFExchangeLite.sol";
import {RFM} from "../src/RFM.sol";

/// @title DeployE2E
/// @notice E2E harness deployment on a local Anvil chain: deploys a freely-mintable
///         MockUSDC (the COLLATERAL), then the four venue contracts pointed at it,
///         wires the one-shot roles, and writes the addresses to a JSON file the
///         backend and driver consume.
/// @dev env: PRIVATE_KEY (deployer), OPERATOR_ADDRESS, optional E2E_OUT_FILE
///      (default ./addresses.json). The deploy job copies it to the shared volume.
contract DeployE2E is Script {
    function run() external returns (address, address, address, address, address) {
        uint256 pk = vm.envUint("PRIVATE_KEY");
        address deployer = vm.addr(pk);
        address operator = vm.envAddress("OPERATOR_ADDRESS");
        string memory outFile = vm.envOr("E2E_OUT_FILE", string("addresses.json"));

        vm.startBroadcast(pk);

        MockUSDC usdc = new MockUSDC();
        OutcomeTokens ot = new OutcomeTokens(IERC20(address(usdc)), operator, deployer);
        Vault vault = new Vault(IERC20(address(usdc)), address(ot), deployer);
        CTFExchangeLite exch = new CTFExchangeLite(address(vault), address(ot), operator);
        RFM rfm = new RFM(address(vault), address(ot));

        ot.setRoles(address(vault), address(rfm));
        vault.setRoles(address(exch), address(rfm));

        vm.stopBroadcast();

        string memory json = string.concat(
            "{",
            '"chainId":"31337",',
            '"usdc":"', vm.toString(address(usdc)), '",',
            '"outcomeTokens":"', vm.toString(address(ot)), '",',
            '"vault":"', vm.toString(address(vault)), '",',
            '"exchange":"', vm.toString(address(exch)), '",',
            '"rfm":"', vm.toString(address(rfm)), '",',
            '"operator":"', vm.toString(operator), '"',
            "}"
        );
        vm.writeJson(json, outFile);

        console2.log("usdc:", address(usdc));
        console2.log("OutcomeTokens:", address(ot));
        console2.log("Vault:", address(vault));
        console2.log("CTFExchangeLite:", address(exch));
        console2.log("RFM:", address(rfm));

        return (address(usdc), address(ot), address(vault), address(exch), address(rfm));
    }
}
