using System.Numerics;
using Venue.Domain;
using Venue.Infrastructure;

namespace Venue.Chain;

/// <summary>Per-tx metadata used to stamp simulated events with chain positions.</summary>
public sealed class TxCtx
{
    public required ulong BlockNumber { get; init; }
    public required string TxHash { get; init; }
    public ulong LogIndex { get; private set; }
    public ulong NextLogIndex() => LogIndex++;
}

/// <summary>Result of a simulated op: the emitted events, or the revert that aborted the tx.</summary>
public sealed class SimOpResult
{
    public required IReadOnlyList<VenueEvent> Events { get; init; }
    public Settlement.BatchRevertInfo? Revert { get; init; }
    public bool Success => Revert == null;
}

/// <summary>
/// In-memory mirror of the venue contracts (Vault + OutcomeTokens + CTFExchangeLite +
/// RFM). Used ONLY by the simulated gateway for the local demo and end-to-end paths;
/// it applies the exact same validation and emits the exact same events as the
/// Solidity, so the ledger rebuild-from-events is exercised identically. The real chain
/// (Nethereum gateway) is the production source of truth.
/// </summary>
public sealed class SimulatedContract
{
    public static readonly BigInteger Bond = 500_000_000; // RFM_BOND = 500 USDC (6-dec)
    public const int MaxQuotes = 32;
    public const int MaxBatch = 8;

    private readonly string _vault;
    private readonly string _ot;
    private readonly string _exchange;
    private readonly string _rfm;
    private readonly string _operator;

    private readonly Dictionary<string, BigInteger> _usdc = new();
    private readonly Dictionary<string, BigInteger> _wallet = new();
    private readonly Dictionary<string, BigInteger> _locked = new();
    private readonly Dictionary<(string, string), BigInteger> _tokens = new();
    private readonly Dictionary<string, (string User, BigInteger Amount, bool Live)> _locks = new();
    private readonly Dictionary<string, bool> _usedTradeIds = new();
    private readonly Dictionary<string, bool> _usedBatchIds = new();
    private readonly Dictionary<string, (bool Reserved, bool Exists, bool Resolved, Outcome Win)> _markets = new();
    private BigInteger _requestCount;
    private readonly Dictionary<BigInteger, RequestSim> _requests = new();
    private readonly Dictionary<BigInteger, List<string>> _mmList = new();
    private readonly Dictionary<(BigInteger, string), CommitSim> _commits = new();
    private readonly Dictionary<(BigInteger, string), RevealSim> _reveals = new();

    private sealed class RequestSim
    {
        public required string Requester;
        public required string Market;
        public required RfmSide Side;
        public required BigInteger Quantity;
        public required BigInteger MaxPriceTick;
        public required BigInteger MinMatch;
        public required BigInteger CommitDeadline;
        public required BigInteger RevealDeadline;
        public required BigInteger EscrowAmount;
        public required BigInteger MinQuoteSize;
        public required BigInteger CommitCount;
        public bool Finalized, Failed, Cancelled;
    }

    private sealed record CommitSim(bool HasCommitted, BigInteger CommitIndex, string CommitHash);
    private sealed record RevealSim(bool HasRevealed, BigInteger Tick, BigInteger Size, bool InRange, BigInteger LockedAmount);

    public SimulatedContract(string vault, string outcomeTokens, string exchange, string rfm, string operatorAddress)
    {
        _vault = Domain.Addresses.Normalize(vault);
        _ot = Domain.Addresses.Normalize(outcomeTokens);
        _exchange = Domain.Addresses.Normalize(exchange);
        _rfm = Domain.Addresses.Normalize(rfm);
        _operator = Domain.Addresses.Normalize(operatorAddress);
    }

    public string Operator => _operator;

    /// <summary>Test-visible state accessors (mirror the on-chain view functions).</summary>
    public BigInteger UsdcOf(string user) => _usdc.GetValueOrDefault(Domain.Addresses.Normalize(user));
    public BigInteger TokenOf(string user, string tokenId) => Tok(Domain.Addresses.Normalize(user), Hash.NormalizeBytes32(tokenId));
    public BigInteger FreeOf(string user) => Free(Domain.Addresses.Normalize(user));
    public BigInteger WalletOf(string user) => _wallet.GetValueOrDefault(Domain.Addresses.Normalize(user));
    public BigInteger RequestCount => _requestCount;

    // ------------------------------------------------------------ MockUSDC (faucet)

    /// <summary>G4 faucet: mint the self-deployed collateral MockUSDC to a user's wallet.
    /// Emits no venue event (the indexer only watches Vault/OT/Exchange/RFM).</summary>
    public SimOpResult MintUsdc(string user, BigInteger amt, TxCtx ctx)
    {
        if (amt <= 0) return Revert(ctx, "ZeroAmount");
        _wallet[Domain.Addresses.Normalize(user)] = _wallet.GetValueOrDefault(Domain.Addresses.Normalize(user)) + amt;
        return Ok(ctx);
    }

    private BigInteger Free(string user) => _usdc.GetValueOrDefault(user) - _locked.GetValueOrDefault(user);
    private BigInteger Tok(string user, string id) => _tokens.GetValueOrDefault((user, id));
    private void AddUsdc(string user, BigInteger amt) => _usdc[user] = _usdc.GetValueOrDefault(user) + amt;
    private void SubUsdc(string user, BigInteger amt) => _usdc[user] = _usdc.GetValueOrDefault(user) - amt;
    private void AddTok(string user, string id, BigInteger amt) => _tokens[(user, id)] = Tok(user, id) + amt;
    private void SubTok(string user, string id, BigInteger amt) => _tokens[(user, id)] = Tok(user, id) - amt;

    private static string MarketIdOf(string rfm, BigInteger requestId) => Hash.KeccakHex(Hash.EncodeAddress(rfm), Hash.EncodeUint256(requestId));

    // Lock refs are opaque keys in the ledger — what matters is that the sim emits the SAME
    // ref string for the lock, its release/consume and any mintPair funding of one flow.
    private static string EscrowRef(BigInteger r) => Hash.KeccakHex(Hash.EncodeUint256(r), EncodeBytes("ESCROW"));
    private static string InstBondRef(BigInteger r) => Hash.KeccakHex(Hash.EncodeUint256(r), EncodeBytes("INSTBOND"));
    private static string MmBondRef(BigInteger r, string mm) => Hash.KeccakHex(Hash.EncodeUint256(r), Hash.EncodeAddress(mm), EncodeBytes("BOND"));
    private static string MmRevealRef(BigInteger r, string mm) => Hash.KeccakHex(Hash.EncodeUint256(r), Hash.EncodeAddress(mm), EncodeBytes("REVEAL"));
    private static byte[] EncodeBytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    // ------------------------------------------------------------ Vault ops

    public SimOpResult Deposit(string user, BigInteger amt, TxCtx ctx)
    {
        if (amt <= 0) return Revert(ctx, "ZeroAmount");
        AddUsdc(user, amt);
        return Ok(ctx, new Deposited(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, user, amt));
    }

    public SimOpResult DepositTokens(string user, string tokenId, BigInteger amt, TxCtx ctx)
    {
        if (amt <= 0) return Revert(ctx, "ZeroAmount");
        AddTok(user, tokenId, amt);
        return Ok(ctx, new TokensDeposited(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, user, tokenId, amt));
    }

    public SimOpResult WithdrawTokens(string user, string tokenId, BigInteger amt, TxCtx ctx)
    {
        if (amt <= 0) return Revert(ctx, "ZeroAmount");
        if (Tok(user, tokenId) < amt) return Revert(ctx, "InsufficientBalance");
        SubTok(user, tokenId, amt);
        return Ok(ctx, new TokensWithdrawn(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, user, tokenId, amt));
    }

    public SimOpResult Withdraw(string user, BigInteger amt, TxCtx ctx)
    {
        if (amt <= 0) return Revert(ctx, "ZeroAmount");
        if (Free(user) < amt) return Revert(ctx, "InsufficientFree");
        SubUsdc(user, amt);
        return Ok(ctx, new Withdrawn(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, user, amt));
    }

    public SimOpResult Resolve(string marketId, Outcome outcome, TxCtx ctx)
    {
        var m = _markets.GetValueOrDefault(Hash.NormalizeBytes32(marketId));
        if (!m.Exists) return Revert(ctx, "NotExists");
        if (m.Resolved) return Revert(ctx, "AlreadyResolved");
        _markets[Hash.NormalizeBytes32(marketId)] = (m.Reserved, m.Exists, true, outcome);
        return Ok(ctx, new MarketResolved(_ot, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, marketId, outcome));
    }

    public SimOpResult Redeem(string user, string marketId, BigInteger amt, TxCtx ctx)
    {
        var m = _markets.GetValueOrDefault(Hash.NormalizeBytes32(marketId));
        if (!m.Exists || !m.Resolved) return Revert(ctx, "Unauthorized");
        if (amt <= 0) return Revert(ctx, "ZeroAmount");
        var winId = Assets.TokenId(marketId, m.Win);
        if (Tok(user, winId) < amt) return Revert(ctx, "InsufficientBalance");
        SubTok(user, winId, amt);
        AddUsdc(user, amt);
        // Vault.redeem also burns Vault's physical tokens via ot.redeem -> pool pays the Vault.
        return Ok(ctx,
            new Redeemed(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, user, marketId, amt),
            new Redeemed(_ot, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, _vault, marketId, amt));
    }

    // ------------------------------------------------------------ settleBatch

    /// <summary>
    /// settleBatch is WHOLE-BATCH ATOMIC exactly like the Solidity: every balance and
    /// usedTradeId mutation is applied to a snapshot, and ANY trade failure rolls the ENTIRE
    /// batch back (no state change, no events) with a SettleBatchFailed(index, tradeId) revert.
    /// </summary>
    public SimOpResult SettleBatch(string batchId, IReadOnlyList<SettlementTrade> trades, TxCtx ctx)
    {
        var batchKey = Hash.NormalizeBytes32(batchId);
        if (_usedBatchIds.TryGetValue(batchKey, out var used) && used) return Revert(ctx, new Settlement.BatchRevertInfo(null, batchId, "BatchReused", ""));
        if (trades.Count == 0) return Revert(ctx, new Settlement.BatchRevertInfo(null, batchId, "EmptyBatch", ""));
        if (trades.Count > MaxBatch) return Revert(ctx, new Settlement.BatchRevertInfo(null, batchId, "BatchTooLarge", trades.Count.ToString()));

        var snapshot = SnapshotState();
        var events = new List<VenueEvent>();
        for (var i = 0; i < trades.Count; i++)
        {
            var t = trades[i];
            var tKey = Hash.NormalizeBytes32(t.TradeId);
            if (_usedTradeIds.TryGetValue(tKey, out var tused) && tused) return Fail(snapshot, ctx, i, t);
            if (t.Size <= 0) return Fail(snapshot, ctx, i, t);
            if (t.OutcomeTick > 1000) return Fail(snapshot, ctx, i, t);

            switch (t.Class)
            {
                case TradeClass.Transfer:
                {
                    var seller = t.PartyA;
                    var buyer = t.PartyB;
                    var cost = Prices.LegCost(t.Size, t.OutcomeTick);
                    var id = Assets.TokenId(t.MarketId, t.Outcome ?? Outcome.Yes);
                    if (Free(buyer) < cost || Tok(seller, id) < t.Size)
                        return Fail(snapshot, ctx, i, t);
                    SubUsdc(buyer, cost); AddUsdc(seller, cost);
                    SubTok(seller, id, t.Size); AddTok(buyer, id, t.Size);
                    _usedTradeIds[tKey] = true;
                    events.Add(new USDCMoved(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, buyer, seller, cost, t.TradeId));
                    events.Add(new TokensMoved(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, seller, buyer, id, t.Size, t.TradeId));
                    break;
                }
                case TradeClass.Mint:
                {
                    var yesCost = Prices.LegCost(t.Size, t.OutcomeTick);
                    var noCost = t.Size - yesCost;
                    if (Free(t.PartyA) < yesCost || Free(t.PartyB) < noCost)
                        return Fail(snapshot, ctx, i, t);
                    SubUsdc(t.PartyA, yesCost); SubUsdc(t.PartyB, noCost);
                    var yesId = Assets.TokenId(t.MarketId, Outcome.Yes);
                    var noId = Assets.TokenId(t.MarketId, Outcome.No);
                    AddTok(t.PartyA, yesId, t.Size);
                    AddTok(t.PartyB, noId, t.Size);
                    _usedTradeIds[tKey] = true;
                    events.Add(new PairMinted(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, t.MarketId,
                        new[] { new Allocation(t.PartyA, t.Size) },
                        new[] { new Allocation(t.PartyB, t.Size) },
                        new[]
                        {
                            new Funding(FundingKind.Free, "", t.PartyA, yesCost),
                            new Funding(FundingKind.Free, "", t.PartyB, noCost),
                        },
                        t.Size));
                    break;
                }
                case TradeClass.Merge:
                {
                    var yesId = Assets.TokenId(t.MarketId, Outcome.Yes);
                    var noId = Assets.TokenId(t.MarketId, Outcome.No);
                    if (Tok(t.PartyA, yesId) < t.Size || Tok(t.PartyB, noId) < t.Size)
                        return Fail(snapshot, ctx, i, t);
                    SubTok(t.PartyA, yesId, t.Size); SubTok(t.PartyB, noId, t.Size);
                    var yesCredit = Prices.LegCost(t.Size, t.OutcomeTick);
                    AddUsdc(t.PartyA, yesCredit); AddUsdc(t.PartyB, t.Size - yesCredit);
                    _usedTradeIds[tKey] = true;
                    events.Add(new PairBurned(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, t.MarketId, t.PartyA, t.PartyB, t.Size, yesCredit));
                    break;
                }
            }
        }

        _usedBatchIds[batchKey] = true;
        events.Add(new BatchSettled(_exchange, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, batchId, trades.Select(t => t.TradeId).ToArray()));
        return new SimOpResult { Events = events };
    }

    private SimOpResult Fail((Dictionary<string, BigInteger> usdc, Dictionary<(string, string), BigInteger> tokens, Dictionary<string, bool> used) snapshot, TxCtx ctx, int index, SettlementTrade trade)
    {
        RestoreState(snapshot);
        return new SimOpResult
        {
            Events = Array.Empty<VenueEvent>(),
            Revert = new Settlement.BatchRevertInfo(index, trade.TradeId, "SettleBatchFailed", ""),
        };
    }

    private (Dictionary<string, BigInteger>, Dictionary<(string, string), BigInteger>, Dictionary<string, bool>) SnapshotState()
        => (new Dictionary<string, BigInteger>(_usdc), new Dictionary<(string, string), BigInteger>(_tokens), new Dictionary<string, bool>(_usedTradeIds));

    private void RestoreState((Dictionary<string, BigInteger>, Dictionary<(string, string), BigInteger>, Dictionary<string, bool>) snapshot)
    {
        _usdc.Clear();
        foreach (var kv in snapshot.Item1) _usdc[kv.Key] = kv.Value;
        _tokens.Clear();
        foreach (var kv in snapshot.Item2) _tokens[kv.Key] = kv.Value;
        _usedTradeIds.Clear();
        foreach (var kv in snapshot.Item3) _usedTradeIds[kv.Key] = kv.Value;
    }

    // -------------------------------------------------------------- RFM ops

    public SimOpResult PostRequest(string requester, string market, RfmSide side, BigInteger quantity, BigInteger maxPriceTick, BigInteger minMatch, BigInteger commitDeadline, BigInteger revealDeadline, TxCtx ctx)
    {
        var now = Now;
        if (now >= commitDeadline) return Revert(ctx, "deadline in past");
        if (commitDeadline >= revealDeadline) return Revert(ctx, "deadline order");
        if (revealDeadline > now + 7 * 86400) return Revert(ctx, "window too long");
        if (quantity <= 0) return Revert(ctx, "zero quantity");
        if (minMatch <= 0 || minMatch > quantity) return Revert(ctx, "bad minMatch");
        if (maxPriceTick <= 0 || maxPriceTick >= 1000) return Revert(ctx, "bad maxPriceTick");

        _requestCount += 1;
        var requestId = _requestCount;
        var marketId = MarketIdOf(_rfm, requestId);
        _markets[marketId] = (Reserved: true, Exists: false, Resolved: false, Outcome.Yes);
        var escrowAmount = quantity * maxPriceTick / 1000;
        var minQuoteSize = (minMatch + MaxQuotes - 1) / MaxQuotes;

        // vault.lock escrow + bond
        if (Free(requester) < escrowAmount + Bond) return Revert(ctx, "InsufficientFree");
        _locked[requester] = _locked.GetValueOrDefault(requester) + escrowAmount + Bond;
        _locks[EscrowRef(requestId)] = (requester, escrowAmount, true);
        _locks[InstBondRef(requestId)] = (requester, Bond, true);

        _requests[requestId] = new RequestSim
        {
            Requester = requester, Market = market, Side = side, Quantity = quantity, MaxPriceTick = maxPriceTick,
            MinMatch = minMatch, CommitDeadline = commitDeadline, RevealDeadline = revealDeadline,
            EscrowAmount = escrowAmount, MinQuoteSize = minQuoteSize, CommitCount = 0,
        };

        var events = new List<VenueEvent>
        {
            new Locked(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, EscrowRef(requestId), requester, escrowAmount),
            new Locked(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, InstBondRef(requestId), requester, Bond),
            new MarketReserved(_ot, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, marketId),
            new RequestPosted(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, market, side, quantity, maxPriceTick, minMatch, commitDeadline, revealDeadline, escrowAmount, minQuoteSize),
            new RfmMarketReserved(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, marketId, requestId),
        };
        return new SimOpResult { Events = events };
    }

    public SimOpResult CommitQuote(string mm, BigInteger requestId, string commitHash, TxCtx ctx)
    {
        if (!_requests.TryGetValue(requestId, out var r)) return Revert(ctx, "no request");
        if (Phase(r, Now) is not (RfmPhase.Open or RfmPhase.Commit)) return Revert(ctx, "commit window closed");
        if (!_commits.ContainsKey((requestId, mm)) && r.CommitCount >= MaxQuotes) return Revert(ctx, "slots full");
        if (!_commits.TryGetValue((requestId, mm), out var c))
        {
            if (Free(mm) < Bond) return Revert(ctx, "InsufficientFree");
            var idx = r.CommitCount;
            r.CommitCount += 1;
            _mmList.TryAdd(requestId, new List<string>());
            _mmList[requestId].Add(mm);
            _locked[mm] = _locked.GetValueOrDefault(mm) + Bond;
            _locks[MmBondRef(requestId, mm)] = (mm, Bond, true);
            c = new CommitSim(true, idx, commitHash);
            _commits[(requestId, mm)] = c;
            return Ok(ctx, new Locked(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmBondRef(requestId, mm), mm, Bond),
                new QuoteCommitted(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, mm, idx));
        }
        _commits[(requestId, mm)] = c with { CommitHash = commitHash };
        return Ok(ctx, new QuoteCommitted(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, mm, c.CommitIndex));
    }

    public SimOpResult RevealQuote(string mm, BigInteger requestId, BigInteger priceTick, BigInteger size, BigInteger salt, TxCtx ctx)
    {
        if (!_requests.TryGetValue(requestId, out var r)) return Revert(ctx, "no request");
        if (Now <= r.CommitDeadline) return Revert(ctx, "commit window open");
        if (Now > r.RevealDeadline) return Revert(ctx, "reveal window closed");
        if (r.Finalized || r.Failed || r.Cancelled) return Revert(ctx, "terminal");
        if (!_commits.TryGetValue((requestId, mm), out var c)) return Revert(ctx, "not committed");
        var recomputed = Hash.QuoteHash(ChainId, _rfm, requestId, mm, priceTick, size, salt);
        if (!string.Equals(recomputed, Hash.NormalizeBytes32(c.CommitHash), StringComparison.OrdinalIgnoreCase)) return Revert(ctx, "hash mismatch");

        var inRange = priceTick <= r.MaxPriceTick && size > 0 && size >= r.MinQuoteSize;
        var lockedAmount = inRange ? size - size * priceTick / 1000 : BigInteger.Zero;
        if (inRange && Free(mm) < lockedAmount) return Revert(ctx, "InsufficientFree");
        if (inRange)
        {
            _locked[mm] = _locked.GetValueOrDefault(mm) + lockedAmount;
            _locks[MmRevealRef(requestId, mm)] = (mm, lockedAmount, true);
            _reveals[(requestId, mm)] = new RevealSim(true, priceTick, size, true, lockedAmount);
            return Ok(ctx,
                new Locked(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmRevealRef(requestId, mm), mm, lockedAmount),
                new QuoteRevealed(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, mm, priceTick, size, true));
        }
        _reveals[(requestId, mm)] = new RevealSim(true, priceTick, size, false, 0);
        return Ok(ctx, new QuoteRevealed(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, mm, priceTick, size, false));
    }

    public SimOpResult CancelRequest(string requester, BigInteger requestId, TxCtx ctx)
    {
        if (!_requests.TryGetValue(requestId, out var r)) return Revert(ctx, "no request");
        if (r.Requester != requester) return Revert(ctx, "not requester");
        if (r.CommitCount != 0) return Revert(ctx, "commits exist");
        if (r.Finalized || r.Failed || r.Cancelled) return Revert(ctx, "terminal");
        r.Cancelled = true;
        ReleaseLockInternal(EscrowRef(requestId), r.EscrowAmount);
        ReleaseLockInternal(InstBondRef(requestId), Bond);
        return Ok(ctx,
            new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, EscrowRef(requestId), requester, r.EscrowAmount),
            new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, InstBondRef(requestId), requester, Bond),
            new RequestCancelled(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId));
    }

    public SimOpResult Finalize(BigInteger requestId, TxCtx ctx)
    {
        if (!_requests.TryGetValue(requestId, out var r)) return Revert(ctx, "no request");
        if (Now <= r.RevealDeadline) return Revert(ctx, "reveal window open");
        if (r.Finalized || r.Failed || r.Cancelled) return Revert(ctx, "terminal");
        var marketId = MarketIdOf(_rfm, requestId);

        // gather in-range revealed quotes, sort by (tick asc, commitIndex asc), greedy fill
        var mmList = _mmList.GetValueOrDefault(requestId) ?? new List<string>();
        var quotes = new List<(string Mm, BigInteger Tick, BigInteger Size, BigInteger CommitIndex)>();
        for (var i = 0; i < mmList.Count; i++)
        {
            var mm = mmList[i];
            if (!_reveals.TryGetValue((requestId, mm), out var rv) || !rv.HasRevealed) continue;
            if (rv.Tick > r.MaxPriceTick || rv.Size == 0 || rv.Size < r.MinQuoteSize) continue;
            quotes.Add((mm, rv.Tick, rv.Size, _commits[(requestId, mm)].CommitIndex));
        }
        quotes.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.CommitIndex.CompareTo(b.CommitIndex));

        var fills = new List<(string Mm, BigInteger Tick, BigInteger Size)>();
        var remaining = r.Quantity;
        foreach (var q in quotes)
        {
            if (remaining <= 0) break;
            var take = q.Size < remaining ? q.Size : remaining;
            fills.Add((q.Mm, q.Tick, take));
            remaining -= take;
        }
        var filled = fills.Aggregate(BigInteger.Zero, (a, f) => a + f.Size);

        var events = new List<VenueEvent>();
        if (filled < r.MinMatch)
        {
            r.Failed = true;
            ReleaseLockInternal(EscrowRef(requestId), r.EscrowAmount);
            ReleaseLockInternal(InstBondRef(requestId), Bond);
            events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, EscrowRef(requestId), r.Requester, r.EscrowAmount));
            events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, InstBondRef(requestId), r.Requester, Bond));
            foreach (var mm in mmList)
            {
                if (_reveals.TryGetValue((requestId, mm), out var rv) && rv.HasRevealed && rv.InRange)
                {
                    ReleaseLockInternal(MmRevealRef(requestId, mm), rv.LockedAmount);
                    ReleaseLockInternal(MmBondRef(requestId, mm), Bond);
                    events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmRevealRef(requestId, mm), mm, rv.LockedAmount));
                    events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmBondRef(requestId, mm), mm, Bond));
                }
                else
                {
                    ConsumeLockInternal(MmBondRef(requestId, mm), Bond, r.Requester);
                    events.Add(new LockConsumed(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmBondRef(requestId, mm), mm, Bond, r.Requester));
                    events.Add(new BondSlashed(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, mm, r.Requester));
                }
            }
            events.Add(new RequestFailed(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId));
            return new SimOpResult { Events = events };
        }

        // FINALIZED path
        r.Finalized = true;
        _markets[marketId] = (Reserved: true, Exists: true, Resolved: false, Outcome.Yes);
        events.Add(new MarketCreated(_ot, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, marketId, Array.Empty<byte>()));

        // mintPair: LOCK funding from escrow (pay-as-bid) + each winner's reveal lock.
        var consumedEscrow = fills.Aggregate(BigInteger.Zero, (a, f) => a + f.Size * f.Tick / 1000);
        var funding = new List<Funding>();
        if (consumedEscrow > 0)
            funding.Add(new Funding(FundingKind.Lock, EscrowRef(requestId), r.Requester, consumedEscrow));
        foreach (var f in fills)
        {
            var mmLeg = f.Size - f.Size * f.Tick / 1000;
            funding.Add(new Funding(FundingKind.Lock, MmRevealRef(requestId, f.Mm), f.Mm, mmLeg));
        }
        var yesAlloc = r.Side == RfmSide.Yes
            ? new[] { new Allocation(r.Requester, filled) }
            : fills.Select(f => new Allocation(f.Mm, f.Size)).ToArray();
        var noAlloc = r.Side == RfmSide.Yes
            ? fills.Select(f => new Allocation(f.Mm, f.Size)).ToArray()
            : new[] { new Allocation(r.Requester, filled) };

    // consume LOCK funding internally, credit allocations
    // Pool funding moves locked USDC INTO the collateral pool — debit the funder, credit NOBODY.
    foreach (var f in funding)
        ConsumeLockToPool(f.Ref, f.Amount);
        var yesId = Assets.TokenId(marketId, Outcome.Yes);
        var noId = Assets.TokenId(marketId, Outcome.No);
        foreach (var a in yesAlloc) AddTok(a.Account, yesId, a.Amount);
        foreach (var a in noAlloc) AddTok(a.Account, noId, a.Amount);
        events.Add(new PairMinted(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, marketId, yesAlloc, noAlloc, funding.ToArray(), filled));

        if (r.EscrowAmount > consumedEscrow)
        {
            ReleaseLockInternal(EscrowRef(requestId), r.EscrowAmount - consumedEscrow);
            events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, EscrowRef(requestId), r.Requester, r.EscrowAmount - consumedEscrow));
        }
        ReleaseLockInternal(InstBondRef(requestId), Bond);
        events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, InstBondRef(requestId), r.Requester, Bond));

        var winners = fills.Select(f => f.Mm).ToHashSet();
        foreach (var f in fills)
        {
            ReleaseLockInternal(MmBondRef(requestId, f.Mm), Bond);
            events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmBondRef(requestId, f.Mm), f.Mm, Bond));
            if (_reveals.TryGetValue((requestId, f.Mm), out var rv) && f.Size < rv.Size)
            {
                var filledLeg = f.Size - f.Size * f.Tick / 1000;
                var remainder = rv.LockedAmount - filledLeg;
                if (remainder > 0)
                {
                    ReleaseLockInternal(MmRevealRef(requestId, f.Mm), remainder);
                    events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmRevealRef(requestId, f.Mm), f.Mm, remainder));
                }
            }
            events.Add(new RfmFill(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, f.Mm, f.Tick, f.Size));
        }

        foreach (var mm in mmList)
        {
            if (_reveals.TryGetValue((requestId, mm), out var rv) && rv.HasRevealed && rv.InRange)
            {
                if (winners.Contains(mm)) continue;
                ReleaseLockInternal(MmRevealRef(requestId, mm), rv.LockedAmount);
                ReleaseLockInternal(MmBondRef(requestId, mm), Bond);
                events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmRevealRef(requestId, mm), mm, rv.LockedAmount));
                events.Add(new LockReleased(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmBondRef(requestId, mm), mm, Bond));
            }
            else
            {
                ConsumeLockInternal(MmBondRef(requestId, mm), Bond, r.Requester);
                events.Add(new LockConsumed(_vault, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, MmBondRef(requestId, mm), mm, Bond, r.Requester));
                events.Add(new BondSlashed(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, mm, r.Requester));
            }
        }

        var marginalTick = fills.Count > 0 ? ToYesBasis(fills[^1].Tick, r.Side) : 0;
        var vwapSum = fills.Aggregate(BigInteger.Zero, (a, f) => a + f.Size * f.Tick);
        var vwapTick = fills.Count > 0 ? ToYesBasis(vwapSum / filled, r.Side) : 0;
        events.Add(new RequestFinalized(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId));
        events.Add(new MarketBorn(_rfm, ctx.BlockNumber, ctx.NextLogIndex(), ctx.TxHash, requestId, marketId, marginalTick, vwapTick, filled, r.Side));
        return new SimOpResult { Events = events };
    }

    // ------------------------------------------------------------ internal

    private void ReleaseLockInternal(string refKey, BigInteger amt)
    {
        if (amt <= 0) return;
        if (!_locks.TryGetValue(refKey, out var lk) || !lk.Live) return;
        var next = lk.Amount - amt;
        _locked[lk.User] = _locked.GetValueOrDefault(lk.User) - amt;
        _locks[refKey] = next <= 0 ? (lk.User, BigInteger.Zero, false) : (lk.User, next, true);
    }

    /// <summary>
    /// Slash/pay: locked -&gt; internal credit of `to`. Used ONLY for bond slashing
    /// (non-revealer / out-of-range MM bonds to the institution) and refund-free payments.
    /// </summary>
    private void ConsumeLockInternal(string refKey, BigInteger amt, string to)
    {
        if (amt <= 0) return;
        if (!_locks.TryGetValue(refKey, out var lk) || !lk.Live) return;
        var next = lk.Amount - amt;
        _locked[lk.User] = _locked.GetValueOrDefault(lk.User) - amt;
        SubUsdc(lk.User, amt);
        AddUsdc(to, amt);
        _locks[refKey] = next <= 0 ? (lk.User, BigInteger.Zero, false) : (lk.User, next, true);
    }

    /// <summary>
    /// Lock-to-POOL consumption (mirrors Vault.mintPair funding[] LOCK): the locked amount is
    /// debited from the funder's internal balance into the collateral pool — NO recipient is
    /// credited. This is how RFM pool funding is paid; the funder must NOT keep the USDC and
    /// also receive outcome tokens redeemable for the same USDC.
    /// </summary>
    private void ConsumeLockToPool(string refKey, BigInteger amt)
    {
        if (amt <= 0) return;
        if (!_locks.TryGetValue(refKey, out var lk) || !lk.Live) return;
        var next = lk.Amount - amt;
        _locked[lk.User] = _locked.GetValueOrDefault(lk.User) - amt;
        SubUsdc(lk.User, amt);
        _locks[refKey] = next <= 0 ? (lk.User, BigInteger.Zero, false) : (lk.User, next, true);
    }

    private static long ToYesBasis(BigInteger tick, RfmSide side) => side == RfmSide.Yes ? (long)tick : 1000 - (long)tick;

    private static RfmPhase Phase(RequestSim r, BigInteger now)
    {
        if (r.Cancelled) return RfmPhase.Cancelled;
        if (r.Finalized) return RfmPhase.Finalized;
        if (r.Failed) return RfmPhase.Failed;
        if (now <= r.CommitDeadline) return r.CommitCount == 0 ? RfmPhase.Open : RfmPhase.Commit;
        return RfmPhase.Reveal;
    }

    /// <summary>Test hook: override the simulated chain clock (unix seconds).</summary>
    public BigInteger? NowOverride { get; set; }

    private BigInteger Now => NowOverride ?? (DateTimeOffset.UtcNow.ToUnixTimeSeconds() < 0 ? BigInteger.Zero : new BigInteger(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private BigInteger ChainId => 5042002;

    private static SimOpResult Ok(TxCtx ctx, params VenueEvent[] events)
        => new() { Events = events };

    private static SimOpResult Revert(TxCtx ctx, string errorName)
        => new() { Events = Array.Empty<VenueEvent>(), Revert = new Settlement.BatchRevertInfo(null, null, errorName, "") };

    private static SimOpResult Revert(TxCtx ctx, Settlement.BatchRevertInfo revert)
        => new() { Events = Array.Empty<VenueEvent>(), Revert = revert };
}
