// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {ERC1155} from "@openzeppelin/contracts/token/ERC1155/ERC1155.sol";
import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";

import {IOutcomeTokens} from "./interfaces/IOutcomeTokens.sol";

/// @title OutcomeTokens
/// @notice CTF-lite binary conditional tokens + collateral pool. TokenId =
///         keccak(marketId, outcome), outcome in {YES, NO}. split/merge are
///         Vault-only; resolve is operator-only one-shot; redeem is permissionless.
/// @dev Spec: PLAN_CONTRACTS.md section 3. Money is 6-dec USDC, never native.
contract OutcomeTokens is ERC1155, ReentrancyGuard, IOutcomeTokens {
    using SafeERC20 for IERC20;

    IERC20 public immutable usdc;
    address public immutable operator;
    address public immutable deployer;

    address public vault;
    address public rfm;

    /// @dev Two-flag market state: `reserved` (RFM, unforgeable-by-race) vs `exists`.
    mapping(bytes32 => Market) public markets;

    mapping(uint256 => uint256) private _totalSupply;

    struct Market {
        bool reserved;
        bool exists;
        bool resolved;
        Outcome winningOutcome;
        uint256 collateralPool;
    }

    event MarketReserved(bytes32 indexed marketId);
    event MarketCreated(bytes32 indexed marketId, bytes meta);
    event MarketResolved(bytes32 indexed marketId, Outcome outcome);
    event Redeemed(address indexed user, bytes32 indexed marketId, uint256 amt);

    error Unauthorized();
    error AlreadyReserved();
    error NotReserved();
    error AlreadyExists();
    error ReservedId();
    error NotExists();
    error AlreadyResolved();
    error ZeroAmount();
    error ZeroAddress();
    error RolesAlreadySet();

    modifier onlyVault() {
        if (msg.sender != vault) revert Unauthorized();
        _;
    }

    constructor(IERC20 usdc_, address operator_, address deployer_) ERC1155("") {
        usdc = usdc_;
        operator = operator_;
        deployer = deployer_;
    }

    /// @notice One-shot role wiring. The two cyclic role edges (vault, rfm) cannot be
    ///         constructor immutables, so they are frozen here by the deployer once.
    function setRoles(address vault_, address rfm_) external {
        if (msg.sender != deployer) revert Unauthorized();
        if (vault != address(0) || rfm != address(0)) revert RolesAlreadySet();
        if (vault_ == address(0) || rfm_ == address(0)) revert ZeroAddress();
        vault = vault_;
        rfm = rfm_;
    }

    function tokenId(bytes32 marketId, Outcome outcome) public pure returns (uint256) {
        return uint256(keccak256(abi.encode(marketId, outcome)));
    }

    // ------------------------------------------------------------------ markets

    function reserveMarket(bytes32 marketId) external {
        if (msg.sender != rfm) revert Unauthorized();
        Market storage m = markets[marketId];
        if (m.reserved || m.exists) revert AlreadyReserved();
        m.reserved = true;
        emit MarketReserved(marketId);
    }

    function createMarket(bytes32 marketId, bytes calldata meta) external {
        Market storage m = markets[marketId];
        if (msg.sender == rfm) {
            if (!m.reserved) revert NotReserved();
            if (m.exists) revert AlreadyExists();
        } else {
            if (msg.sender != operator) revert Unauthorized();
            // Operator may only create genuinely fresh ids, never one reserved by RFM.
            if (m.exists) revert AlreadyExists();
            if (m.reserved) revert ReservedId();
        }
        m.exists = true;
        emit MarketCreated(marketId, meta);
    }

    // ------------------------------------------------------------------- pool ops

    /// @notice Vault-only. Pulls `size` USDC into the pool, mints size YES + size NO to Vault.
    function split(bytes32 marketId, uint256 size) external nonReentrant onlyVault {
        if (!markets[marketId].exists) revert NotExists();
        if (size == 0) revert ZeroAmount();
        usdc.safeTransferFrom(msg.sender, address(this), size);
        markets[marketId].collateralPool += size;
        _mint(msg.sender, tokenId(marketId, Outcome.YES), size, "");
        _mint(msg.sender, tokenId(marketId, Outcome.NO), size, "");
    }

    /// @notice Vault-only. Burns a YES + NO pair, releases `size` USDC from the pool.
    function merge(bytes32 marketId, uint256 size) external nonReentrant onlyVault {
        if (!markets[marketId].exists) revert NotExists();
        if (size == 0) revert ZeroAmount();
        _burn(msg.sender, tokenId(marketId, Outcome.YES), size);
        _burn(msg.sender, tokenId(marketId, Outcome.NO), size);
        markets[marketId].collateralPool -= size;
        usdc.safeTransfer(msg.sender, size);
    }

    /// @notice Operator-only, one-shot resolution. Winning token redeems 1:1, losing to 0.
    function resolve(bytes32 marketId, Outcome outcome) external {
        if (msg.sender != operator) revert Unauthorized();
        Market storage m = markets[marketId];
        if (!m.exists) revert NotExists();
        if (m.resolved) revert AlreadyResolved();
        m.resolved = true;
        m.winningOutcome = outcome;
        emit MarketResolved(marketId, outcome);
    }

    /// @notice Permissionless post-resolve exit for wallet-held tokens.
    function redeem(bytes32 marketId, uint256 amt) external nonReentrant {
        Market storage m = markets[marketId];
        if (!m.exists || !m.resolved) revert Unauthorized();
        if (amt == 0) revert ZeroAmount();
        _burn(msg.sender, tokenId(marketId, m.winningOutcome), amt);
        m.collateralPool -= amt;
        usdc.safeTransfer(msg.sender, amt);
        emit Redeemed(msg.sender, marketId, amt);
    }

    // --------------------------------------------------------------------- views

    function isResolved(bytes32 marketId) external view returns (bool) {
        return markets[marketId].resolved;
    }

    function winningOutcome(bytes32 marketId) external view returns (Outcome) {
        return markets[marketId].winningOutcome;
    }

    function physicalUsdc() external view returns (uint256) {
        return usdc.balanceOf(address(this));
    }

    /// @notice Outstanding supply of a token id, tracked in `_update`.
    function totalSupply(uint256 id) public view returns (uint256) {
        return _totalSupply[id];
    }

    function _update(address from, address to, uint256[] memory ids, uint256[] memory values) internal virtual override {
        if (from == address(0)) {
            for (uint256 i = 0; i < ids.length; ++i) {
                _totalSupply[ids[i]] += values[i];
            }
        } else if (to == address(0)) {
            for (uint256 i = 0; i < ids.length; ++i) {
                _totalSupply[ids[i]] -= values[i];
            }
        }
        super._update(from, to, ids, values);
    }
}
