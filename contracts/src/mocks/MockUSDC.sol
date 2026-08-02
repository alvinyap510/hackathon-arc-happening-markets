// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {ERC20} from "@openzeppelin/contracts/token/ERC20/ERC20.sol";

/// @title MockUSDC
/// @notice 6-decimals, freely-mintable ERC-20 standing in for the Arc USDC ERC-20 face
///         (0x3600...0000). The venue ALWAYS trades this self-deployed mock as
///         COLLATERAL - on both the local Anvil E2E chain and Arc testnet - because the
///         real Arc system USDC is Circle-issued and not mintable to demo size.
///         GAS is a separate concern (ETH on Anvil; real native USDC on Arc, tier-2).
/// @dev Permissionless mint so the E2E driver can fund throwaway accounts.
contract MockUSDC is ERC20 {
    uint8 private immutable _decimals;

    constructor() ERC20("Mock USDC", "USDC") {
        _decimals = 6;
    }

    function decimals() public view override returns (uint8) {
        return _decimals;
    }

    function mint(address to, uint256 amt) external {
        _mint(to, amt);
    }
}
