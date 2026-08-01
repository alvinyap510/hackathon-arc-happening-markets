// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {ERC20} from "@openzeppelin/contracts/token/ERC20/ERC20.sol";

/// @title MockUSDC
/// @notice 6-decimals ERC-20 standing in for the Arc USDC ERC-20 face (0x3600...0000)
///         in tests. Production deploys use the canonical Arc system contract.
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
