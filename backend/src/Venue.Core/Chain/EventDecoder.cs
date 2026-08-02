using System.Numerics;
using Nethereum.ABI.FunctionEncoding.Attributes;
using Nethereum.Contracts;
using Nethereum.JsonRpc.Client;
using Nethereum.RPC.Eth.DTOs;
using Venue.Domain;

namespace Venue.Chain;

/// <summary>
/// Decodes raw FilterLogs into domain VenueEvents by routing on (contract address,
/// topic0). Topic0s are derived from the DTOs via Nethereum's EventABI so the
/// signatures can never drift from the C# side. Unknown events return null — the
/// indexer still advances its cursor over them.
/// </summary>
public sealed class EventDecoder
{
    private readonly ChainConfig _cfg;
    private readonly Dictionary<string, Dictionary<string, Func<FilterLog, VenueEvent?>>> _byContract = new();

    public EventDecoder(ChainConfig cfg)
    {
        _cfg = cfg;
        Build();
    }

    public VenueEvent? Decode(FilterLog log)
    {
        var address = Domain.Addresses.Normalize(log.Address);
        if (!_byContract.TryGetValue(address, out var table)) return null;
        var topic0 = log.Topics is { Length: > 0 } ? NormalizeTopic(log.Topics[0]?.ToString()) : null;
        if (topic0 == null || !table.TryGetValue(topic0, out var fn)) return null;
        return fn(log);
    }

    public IReadOnlyList<VenueEvent> DecodeAll(IEnumerable<FilterLog> logs)
    {
        var result = new List<VenueEvent>();
        foreach (var log in logs)
        {
            var e = Decode(log);
            if (e != null) result.Add(e);
        }
        return result;
    }

    private void Build()
    {
        var vault = Table(_cfg.NormalizedVault);
        var ot = Table(_cfg.NormalizedOutcomeTokens);
        var ex = Table(_cfg.NormalizedExchange);
        var rfm = Table(_cfg.NormalizedRfm);

        Add<DepositedEventDto>(vault, (log, d) => new Deposited(Vault, Blk(log), Idx(log), Tx(log), A(d.User), d.Amt));
        Add<WithdrawnEventDto>(vault, (log, d) => new Withdrawn(Vault, Blk(log), Idx(log), Tx(log), A(d.User), d.Amt));
        Add<TokensDepositedEventDto>(vault, (log, d) => new TokensDeposited(Vault, Blk(log), Idx(log), Tx(log), A(d.User), RawIdHex(log), d.Amt));
        Add<TokensWithdrawnEventDto>(vault, (log, d) => new TokensWithdrawn(Vault, Blk(log), Idx(log), Tx(log), A(d.User), RawIdHex(log), d.Amt));
        Add<UsdcMovedEventDto>(vault, (log, d) => new USDCMoved(Vault, Blk(log), Idx(log), Tx(log), A(d.From), A(d.To), d.Amt, B32(d.TradeId)));
        Add<TokensMovedEventDto>(vault, (log, d) => new TokensMoved(Vault, Blk(log), Idx(log), Tx(log), A(d.From), A(d.To), RawIdHex(log), d.Amt, B32(d.TradeId)));
        Add<LockedEventDto>(vault, (log, d) => new Locked(Vault, Blk(log), Idx(log), Tx(log), B32(d.Ref), A(d.User), d.Amt));
        Add<LockReleasedEventDto>(vault, (log, d) => new LockReleased(Vault, Blk(log), Idx(log), Tx(log), B32(d.Ref), A(d.User), d.Amt));
        Add<LockConsumedEventDto>(vault, (log, d) => new LockConsumed(Vault, Blk(log), Idx(log), Tx(log), B32(d.Ref), A(d.User), d.Amt, A(d.To)));
        Add<PairMintedEventDto>(vault, (log, d) => new PairMinted(Vault, Blk(log), Idx(log), Tx(log), B32(d.MarketId),
            d.YesAlloc.Select(x => new Allocation(A(x.Account), x.Amount)).ToArray(),
            d.NoAlloc.Select(x => new Allocation(A(x.Account), x.Amount)).ToArray(),
            d.Funding.Select(x => new Funding((FundingKind)x.Kind, B32(x.Ref), A(x.Account), x.Amount)).ToArray(),
            d.Size));
        Add<PairBurnedEventDto>(vault, (log, d) => new PairBurned(Vault, Blk(log), Idx(log), Tx(log), B32(d.MarketId), A(d.YesFrom), A(d.NoFrom), d.Size, d.YesCredit));
        Add<RedeemedEventDto>(vault, (log, d) => new Redeemed(Vault, Blk(log), Idx(log), Tx(log), A(d.User), B32(d.MarketId), d.Amt));

        Add<OtMarketReservedEventDto>(ot, (log, d) => new MarketReserved(OT, Blk(log), Idx(log), Tx(log), B32(d.MarketId)));
        Add<MarketCreatedEventDto>(ot, (log, d) => new MarketCreated(OT, Blk(log), Idx(log), Tx(log), B32(d.MarketId), d.Meta));
        Add<MarketResolvedEventDto>(ot, (log, d) => new MarketResolved(OT, Blk(log), Idx(log), Tx(log), B32(d.MarketId), (Outcome)d.Outcome));
        Add<RedeemedEventDto>(ot, (log, d) => new Redeemed(OT, Blk(log), Idx(log), Tx(log), A(d.User), B32(d.MarketId), d.Amt));

        Add<BatchSettledEventDto>(ex, (log, d) => new BatchSettled(EX, Blk(log), Idx(log), Tx(log), B32(d.BatchId), d.TradeIds.Select(B32).ToArray()));

        Add<RequestPostedEventDto>(rfm, (log, d) => new RequestPosted(RFM, Blk(log), Idx(log), Tx(log), d.RequestId, B32(d.Market), (RfmSide)d.Side, d.Quantity, d.MaxPriceTick, d.MinMatch, d.CommitDeadline, d.RevealDeadline, d.EscrowAmount, d.MinQuoteSize));
        Add<QuoteCommittedEventDto>(rfm, (log, d) => new QuoteCommitted(RFM, Blk(log), Idx(log), Tx(log), d.RequestId, A(d.Mm), d.CommitIndex));
        Add<QuoteRevealedEventDto>(rfm, (log, d) => new QuoteRevealed(RFM, Blk(log), Idx(log), Tx(log), d.RequestId, A(d.Mm), d.Tick, d.Size, d.InRange));
        Add<RfmFillEventDto>(rfm, (log, d) => new RfmFill(RFM, Blk(log), Idx(log), Tx(log), d.RequestId, A(d.Mm), d.Tick, d.Size));
        Add<RequestFinalizedEventDto>(rfm, (log, d) => new RequestFinalized(RFM, Blk(log), Idx(log), Tx(log), d.RequestId));
        Add<RequestFailedEventDto>(rfm, (log, d) => new RequestFailed(RFM, Blk(log), Idx(log), Tx(log), d.RequestId));
        Add<RequestCancelledEventDto>(rfm, (log, d) => new RequestCancelled(RFM, Blk(log), Idx(log), Tx(log), d.RequestId));
        Add<BondSlashedEventDto>(rfm, (log, d) => new BondSlashed(RFM, Blk(log), Idx(log), Tx(log), d.RequestId, A(d.Mm), A(d.To)));
        Add<RfmMarketReservedEventDto>(rfm, (log, d) => new RfmMarketReserved(RFM, Blk(log), Idx(log), Tx(log), B32(d.MarketId), d.RequestId));
        Add<MarketBornEventDto>(rfm, (log, d) => new MarketBorn(RFM, Blk(log), Idx(log), Tx(log), d.RequestId, B32(d.MarketId), d.MarginalYesTick, d.VwapYesTick, d.FilledQuantity, (RfmSide)d.Side));
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Nethereum's Sha3Signature is lowercase hex WITHOUT the 0x prefix, while
    /// a log topic0 carries it - normalize both to the bare lowercase hex so the table
    /// lookup can never miss a real-chain event (found by the E2E harness).</summary>
    private static string NormalizeTopic(string? t)
    {
        if (string.IsNullOrEmpty(t)) return "";
        return t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..].ToLowerInvariant() : t.ToLowerInvariant();
    }

    private string Vault => _cfg.NormalizedVault;
    private string OT => _cfg.NormalizedOutcomeTokens;
    private string EX => _cfg.NormalizedExchange;
    private string RFM => _cfg.NormalizedRfm;

    private string A(string address) => Domain.Addresses.Normalize(address);
    private static string B32(byte[] b) => Infrastructure.Hash.BytesToHex(b);
    private static ulong Blk(FilterLog l) => (ulong)l.BlockNumber.Value;
    private static ulong Idx(FilterLog l) => (ulong)l.LogIndex.Value;
    private static string Tx(FilterLog l) => l.TransactionHash ?? "";

    /// <summary>
    /// The ERC-1155 token id as the FIRST 32 bytes of the log data, read verbatim.
    /// Nethereum/.NET's uint256 BigInteger decode is wrong for ids whose top bit is set
    /// (e.g. keccak-derived ids) - it produces a >256-bit value with a scrambled byte
    /// order that the ledger then keys positions by. Found by the E2E harness: an
    /// over-long id wedged the indexer poll loop. Reading the raw word sidesteps it.
    /// </summary>
    private static string RawIdHex(FilterLog l)
    {
        var data = l.Data ?? "";
        if (data.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) data = data[2..];
        if (data.Length < 64) throw new ArgumentException("token-id event has no 32-byte data word");
        return Infrastructure.Hash.NormalizeBytes32("0x" + data[..64]);
    }

    private Dictionary<string, Func<FilterLog, VenueEvent?>> Table(string address)
    {
        if (!_byContract.TryGetValue(address, out var table))
        {
            table = new Dictionary<string, Func<FilterLog, VenueEvent?>>();
            _byContract[address] = table;
        }
        return table;
    }

    private void Add<T>(Dictionary<string, Func<FilterLog, VenueEvent?>> table, Func<FilterLog, T, VenueEvent> map)
        where T : IEventDTO, new()
    {
        // Event<T> members are static in Nethereum 6.x: no client needed for local decode.
        var topic0 = Event<T>.GetEventABI().Sha3Signature;
        table[topic0] = log =>
        {
            var decoded = Event<T>.DecodeEvent(log);
            return decoded == null ? null : map(log, decoded.Event);
        };
    }
}
