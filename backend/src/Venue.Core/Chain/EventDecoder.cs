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
        var topic0 = log.Topics is { Length: > 0 } ? log.Topics[0]?.ToString() : null;
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
        Add<TokensDepositedEventDto>(vault, (log, d) => new TokensDeposited(Vault, Blk(log), Idx(log), Tx(log), A(d.User), IdHex(d.Id), d.Amt));
        Add<TokensWithdrawnEventDto>(vault, (log, d) => new TokensWithdrawn(Vault, Blk(log), Idx(log), Tx(log), A(d.User), IdHex(d.Id), d.Amt));
        Add<UsdcMovedEventDto>(vault, (log, d) => new USDCMoved(Vault, Blk(log), Idx(log), Tx(log), A(d.From), A(d.To), d.Amt, B32(d.TradeId)));
        Add<TokensMovedEventDto>(vault, (log, d) => new TokensMoved(Vault, Blk(log), Idx(log), Tx(log), A(d.From), A(d.To), IdHex(d.Id), d.Amt, B32(d.TradeId)));
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

    private string Vault => _cfg.NormalizedVault;
    private string OT => _cfg.NormalizedOutcomeTokens;
    private string EX => _cfg.NormalizedExchange;
    private string RFM => _cfg.NormalizedRfm;

    private string A(string address) => Domain.Addresses.Normalize(address);
    private static string B32(byte[] b) => Infrastructure.Hash.BytesToHex(b);
    private static string IdHex(BigInteger id) => Infrastructure.Hash.NormalizeBytes32("0x" + id.ToString("x64"));
    private static ulong Blk(FilterLog l) => (ulong)l.BlockNumber.Value;
    private static ulong Idx(FilterLog l) => (ulong)l.LogIndex.Value;
    private static string Tx(FilterLog l) => l.TransactionHash ?? "";

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
