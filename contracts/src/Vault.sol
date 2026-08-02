// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import {IERC20} from "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import {SafeERC20} from "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import {IERC165} from "@openzeppelin/contracts/utils/introspection/IERC165.sol";
import {IERC1155} from "@openzeppelin/contracts/token/ERC1155/IERC1155.sol";
import {IERC1155Receiver} from "@openzeppelin/contracts/token/ERC1155/IERC1155Receiver.sol";
import {ReentrancyGuard} from "@openzeppelin/contracts/utils/ReentrancyGuard.sol";

import {IOutcomeTokens} from "./interfaces/IOutcomeTokens.sol";
import {IVault} from "./interfaces/IVault.sol";

/// @title Vault
/// @notice Custody + ALL physical asset movement. Users deposit USDC and venue-custodied
///         outcome tokens; the engine/RFM command it only through narrow conservation-
///         preserving primitives. Permissionless withdraw / redeem can never be frozen.
/// @dev Spec: PLAN_CONTRACTS.md section 1. Money is 6-dec USDC via the ERC-20 face.
///
///      Accounting: `usdcBal[user]` is the TOTAL internal balance (locked included);
///      `lockedBal[user]` is the locked subset; free = usdcBal - lockedBal. Every op
///      either conserves the internal total or moves matching physical assets.
contract Vault is IVault, ReentrancyGuard, IERC1155Receiver {
    using SafeERC20 for IERC20;

    IERC20 public immutable usdc;
    address public immutable outcomeTokens;
    address public immutable deployer;

    /// @dev Set once by the deployer via setRoles; frozen after. Constructor immutables
    ///      cannot cover these two edges because exchange/rfm and vault form a deploy cycle.
    address public exchange;
    address public rfm;

    mapping(address => uint256) public usdcBal;
    mapping(address => uint256) public lockedBal;

    struct Lock {
        address user;
        uint256 amount;
        bool live;
    }

    /// @dev `amount` is the remaining locked amount; partial release/consume decrements it.
    mapping(bytes32 => Lock) public locks;
    mapping(address => mapping(uint256 => uint256)) public tokenBal;

    error Unauthorized();
    error RolesAlreadySet();
    error ZeroAddress();
    error ZeroAmount();
    error RefInUse();
    error InsufficientFree();
    error InsufficientBalance();
    error SumMismatch();
    error NotResolved();

    modifier onlyExchange() {
        if (msg.sender != exchange) revert Unauthorized();
        _;
    }

    modifier onlyRfm() {
        if (msg.sender != rfm) revert Unauthorized();
        _;
    }

    modifier onlyExchangeOrRfm() {
        if (msg.sender != exchange && msg.sender != rfm) revert Unauthorized();
        _;
    }

    constructor(IERC20 usdc_, address outcomeTokens_, address deployer_) {
        usdc = usdc_;
        outcomeTokens = outcomeTokens_;
        deployer = deployer_;
        // Split pulls pool funding from Vault's physical USDC in one approval.
        IERC20(usdc_).forceApprove(outcomeTokens_, type(uint256).max);
    }

    /// @notice One-shot role wiring for the cyclic deploy edges (exchange, rfm).
    function setRoles(address exchange_, address rfm_) external {
        if (msg.sender != deployer) revert Unauthorized();
        if (exchange != address(0) || rfm != address(0)) revert RolesAlreadySet();
        if (exchange_ == address(0) || rfm_ == address(0)) revert ZeroAddress();
        exchange = exchange_;
        rfm = rfm_;
    }

    // ---------------------------------------------------------- user surface

    function deposit(uint256 amt) external nonReentrant {
        if (amt == 0) revert ZeroAmount();
        usdc.safeTransferFrom(msg.sender, address(this), amt);
        usdcBal[msg.sender] += amt;
        emit Deposited(msg.sender, amt);
    }

    function withdraw(uint256 amt) external nonReentrant {
        if (amt == 0) revert ZeroAmount();
        if (freeBal(msg.sender) < amt) revert InsufficientFree();
        usdcBal[msg.sender] -= amt;
        usdc.safeTransfer(msg.sender, amt);
        emit Withdrawn(msg.sender, amt);
    }

    function depositTokens(uint256 id, uint256 amt) external nonReentrant {
        if (amt == 0) revert ZeroAmount();
        IERC1155(outcomeTokens).safeTransferFrom(msg.sender, address(this), id, amt, "");
        tokenBal[msg.sender][id] += amt;
        emit TokensDeposited(msg.sender, id, amt);
    }

    function withdrawTokens(uint256 id, uint256 amt) external nonReentrant {
        if (amt == 0) revert ZeroAmount();
        if (tokenBal[msg.sender][id] < amt) revert InsufficientBalance();
        tokenBal[msg.sender][id] -= amt;
        IERC1155(outcomeTokens).safeTransferFrom(address(this), msg.sender, id, amt, "");
        emit TokensWithdrawn(msg.sender, id, amt);
    }

    /// @notice Post-resolve exit for Vault-custodied tokens, mirroring OutcomeTokens.redeem.
    function redeem(bytes32 marketId, uint256 amt) external nonReentrant {
        if (amt == 0) revert ZeroAmount();
        IOutcomeTokens ot = IOutcomeTokens(outcomeTokens);
        if (!ot.isResolved(marketId)) revert NotResolved();
        uint256 win = ot.tokenId(marketId, ot.winningOutcome(marketId));
        if (tokenBal[msg.sender][win] < amt) revert InsufficientBalance();
        tokenBal[msg.sender][win] -= amt;
        ot.redeem(marketId, amt); // burns Vault's physical winning tokens; pool pays Vault amt USDC
        usdcBal[msg.sender] += amt;
        emit Redeemed(msg.sender, marketId, amt);
    }

    // ------------------------------------------------------- authorized moves

    function moveUSDC(address from, address to, uint256 amt, bytes32 tradeId) external nonReentrant onlyExchange {
        if (amt == 0) revert ZeroAmount();
        if (freeBal(from) < amt) revert InsufficientFree();
        usdcBal[from] -= amt;
        usdcBal[to] += amt;
        emit USDCMoved(from, to, amt, tradeId);
    }

    function moveTokens(address from, address to, uint256 id, uint256 amt, bytes32 tradeId)
        external
        nonReentrant
        onlyExchange
    {
        if (amt == 0) revert ZeroAmount();
        if (tokenBal[from][id] < amt) revert InsufficientBalance();
        tokenBal[from][id] -= amt;
        tokenBal[to][id] += amt;
        emit TokensMoved(from, to, id, amt, tradeId);
    }

    // --------------------------------------------------------- RFM locks

    function lock(address user, uint256 amt, bytes32 ref) external nonReentrant onlyRfm {
        if (amt == 0) revert ZeroAmount();
        if (locks[ref].live) revert RefInUse();
        if (freeBal(user) < amt) revert InsufficientFree();
        lockedBal[user] += amt;
        locks[ref] = Lock({user: user, amount: amt, live: true});
        emit Locked(ref, user, amt);
    }

    function releaseLock(bytes32 ref, uint256 amt) external nonReentrant onlyRfm {
        if (amt == 0) revert ZeroAmount();
        Lock storage lk = locks[ref];
        if (!lk.live) revert RefInUse();
        if (lk.amount < amt) revert InsufficientBalance();
        lk.amount -= amt;
        if (lk.amount == 0) lk.live = false;
        lockedBal[lk.user] -= amt;
        emit LockReleased(ref, lk.user, amt);
    }

    /// @notice Slash/pay only: locked -> internal credit of `to`. Refunds are releaseLock;
    ///         pool funding flows through mintPair.funding[] instead.
    function consumeLock(bytes32 ref, uint256 amt, address to) external nonReentrant onlyRfm {
        if (amt == 0) revert ZeroAmount();
        Lock storage lk = locks[ref];
        if (!lk.live) revert RefInUse();
        if (lk.amount < amt) revert InsufficientBalance();
        lk.amount -= amt;
        if (lk.amount == 0) lk.live = false;
        lockedBal[lk.user] -= amt;
        usdcBal[lk.user] -= amt;
        usdcBal[to] += amt;
        emit LockConsumed(ref, lk.user, amt, to);
    }

    // ------------------------------------------------------ pair primitives

    /// @notice Mints a YES + NO pair into the pool. `yesAlloc`/`noAlloc` each sum to `size`;
    ///         `funding` (LOCK refs or FREE balances) sums to `size` and is consumed
    ///         internally in this one call.
    function mintPair(
        bytes32 marketId,
        Allocation[] calldata yesAlloc,
        Allocation[] calldata noAlloc,
        Funding[] calldata funding,
        uint256 size
    ) external nonReentrant onlyExchangeOrRfm {
        if (size == 0) revert ZeroAmount();
        _checkAllocations(yesAlloc, noAlloc, funding, size);
        for (uint256 i = 0; i < funding.length; ++i) {
            Funding calldata f = funding[i];
            if (f.kind == FundingKind.LOCK) {
                Lock storage lk = locks[f.ref];
                if (!lk.live || lk.user != f.account) revert Unauthorized();
                if (lk.amount < f.amount) revert InsufficientBalance();
                lk.amount -= f.amount;
                if (lk.amount == 0) lk.live = false;
                lockedBal[lk.user] -= f.amount;
                usdcBal[lk.user] -= f.amount;
            } else {
                if (freeBal(f.account) < f.amount) revert InsufficientFree();
                usdcBal[f.account] -= f.amount;
            }
        }
        IOutcomeTokens(outcomeTokens).split(marketId, size); // moves size USDC into the pool
        uint256 yesId = IOutcomeTokens(outcomeTokens).tokenId(marketId, IOutcomeTokens.Outcome.YES);
        uint256 noId = IOutcomeTokens(outcomeTokens).tokenId(marketId, IOutcomeTokens.Outcome.NO);
        for (uint256 i = 0; i < yesAlloc.length; ++i) {
            tokenBal[yesAlloc[i].account][yesId] += yesAlloc[i].amount;
        }
        for (uint256 i = 0; i < noAlloc.length; ++i) {
            tokenBal[noAlloc[i].account][noId] += noAlloc[i].amount;
        }
        emit PairMinted(marketId, yesAlloc, noAlloc, funding, size);
    }

    function burnPair(bytes32 marketId, address yesFrom, address noFrom, uint256 size, uint256 yesCredit)
        external
        nonReentrant
        onlyExchange
    {
        if (size == 0) revert ZeroAmount();
        if (yesCredit > size) revert SumMismatch();
        uint256 yesId = IOutcomeTokens(outcomeTokens).tokenId(marketId, IOutcomeTokens.Outcome.YES);
        uint256 noId = IOutcomeTokens(outcomeTokens).tokenId(marketId, IOutcomeTokens.Outcome.NO);
        if (tokenBal[yesFrom][yesId] < size || tokenBal[noFrom][noId] < size) revert InsufficientBalance();
        tokenBal[yesFrom][yesId] -= size;
        tokenBal[noFrom][noId] -= size;
        IOutcomeTokens(outcomeTokens).merge(marketId, size); // returns size USDC to Vault
        usdcBal[yesFrom] += yesCredit;
        usdcBal[noFrom] += size - yesCredit;
        emit PairBurned(marketId, yesFrom, noFrom, size, yesCredit);
    }

    // ------------------------------------------------------------------ views

    function freeBal(address user) public view returns (uint256) {
        return usdcBal[user] - lockedBal[user];
    }

    // ----------------------------------------------------- ERC-1155 receiver

    function onERC1155Received(address, address, uint256, uint256, bytes calldata) external pure returns (bytes4) {
        return IERC1155Receiver.onERC1155Received.selector;
    }

    function onERC1155BatchReceived(address, address, uint256[] calldata, uint256[] calldata, bytes calldata)
        external
        pure
        returns (bytes4)
    {
        return IERC1155Receiver.onERC1155BatchReceived.selector;
    }

    function supportsInterface(bytes4 interfaceId) external pure returns (bool) {
        return interfaceId == type(IERC1155Receiver).interfaceId || interfaceId == type(IERC165).interfaceId;
    }

    // -------------------------------------------------------------- internal

    function _checkAllocations(Allocation[] calldata yesAlloc, Allocation[] calldata noAlloc, Funding[] calldata funding, uint256 size)
        internal
        pure
    {
        uint256 yesSum;
        uint256 noSum;
        uint256 fundSum;
        for (uint256 i = 0; i < yesAlloc.length; ++i) {
            yesSum += yesAlloc[i].amount;
        }
        for (uint256 i = 0; i < noAlloc.length; ++i) {
            noSum += noAlloc[i].amount;
        }
        for (uint256 i = 0; i < funding.length; ++i) {
            fundSum += funding[i].amount;
        }
        if (yesSum != size || noSum != size || fundSum != size) revert SumMismatch();
    }
}
