using System.Numerics;
using Venue.Chain;
using Venue.Domain;

namespace Venue.Rfm;

/// <summary>
/// RFM coordinator: mirrors request state from events (phase derived exactly as the
/// contract derives it) and cranks finalize() once a request's reveal window has
/// passed (demo preset: commit 2 min / reveal 1 min). Zero authority — it only
/// exercises the permissionless finalize path for liveness. Also drives the API's
/// per-request timelines and commit/reveal counts.
/// </summary>
public sealed class RfmCoordinator
{
    private readonly Dictionary<BigInteger, RfmRequestMirror> _requests = new();
    private readonly Dictionary<string, BigInteger> _requestByMarket = new();
    private readonly IChainGateway _gateway;

    public RfmCoordinator(IChainGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>Apply a lifecycle event to the mirror (called under the core gate).</summary>
    public void Apply(VenueEvent e)
    {
        switch (e)
        {
            case RequestPosted p:
                _requests[p.RequestId] = new RfmRequestMirror
                {
                    RequestId = p.RequestId,
                    Market = p.Market,
                    Side = p.Side,
                    Quantity = p.Quantity,
                    MaxPriceTick = p.MaxPriceTick,
                    MinMatch = p.MinMatch,
                    CommitDeadline = p.CommitDeadline,
                    RevealDeadline = p.RevealDeadline,
                    EscrowAmount = p.EscrowAmount,
                    MinQuoteSize = p.MinQuoteSize,
                    PostedTxHash = p.TxHash,
                };
                break;
            case RfmMarketReserved r:
                _requestByMarket[r.MarketId] = r.RequestId;
                break;
            case QuoteCommitted q:
                if (_requests.TryGetValue(q.RequestId, out var rc) && q.CommitIndex + 1 > rc.CommitCount)
                    rc.CommitCount = q.CommitIndex + 1;
                break;
            case QuoteRevealed q:
                if (_requests.TryGetValue(q.RequestId, out var rq))
                    rq.Reveals.Add(new RevealView(q.Mm, q.Tick, q.Size, q.InRange, q.TxHash));
                break;
            case RfmFill f:
                if (_requests.TryGetValue(f.RequestId, out var rf))
                    rf.Fills.Add((f.Mm, f.Tick, f.Size));
                break;
            case RequestFinalized f:
                if (_requests.TryGetValue(f.RequestId, out var rf2)) rf2.Finalized = true;
                break;
            case RequestFailed f:
                if (_requests.TryGetValue(f.RequestId, out var rf3)) rf3.Failed = true;
                break;
            case RequestCancelled c:
                if (_requests.TryGetValue(c.RequestId, out var rc2)) rc2.Cancelled = true;
                break;
            case MarketBorn b:
                if (_requests.TryGetValue(b.RequestId, out var rb))
                {
                    rb.MarketId = b.MarketId;
                    rb.BornMarginalYesTick = (long)b.MarginalYesTick;
                    rb.BornVwapYesTick = (long)b.VwapYesTick;
                    rb.BornFilledQuantity = b.FilledQuantity;
                    rb.BornTxHash = b.TxHash;
                }
                _requestByMarket[b.MarketId] = b.RequestId;
                break;
        }
    }

    public IReadOnlyList<RfmRequestMirror> Requests => _requests.Values.OrderBy(r => r.RequestId).ToList();

    /// <summary>Restart: drop all mirrors (replayed from events).</summary>
    public void Clear()
    {
        _requests.Clear();
        _requestByMarket.Clear();
    }

    public RfmRequestMirror? Get(BigInteger requestId)
        => _requests.TryGetValue(requestId, out var r) ? r : null;

    public BigInteger? RequestForMarket(string marketId)
        => _requestByMarket.TryGetValue(Infrastructure.Hash.NormalizeBytes32(marketId), out var id) ? id : null;

    /// <summary>Requests whose reveal window has passed and which are not terminal.</summary>
    public IReadOnlyList<BigInteger> ReadyToFinalize(BigInteger nowUnixSec)
        => _requests.Values.Where(r => r.FinalizeReadyAt(nowUnixSec)).Select(r => r.RequestId).ToList();

    /// <summary>Crank finalize for every ready request (liveness: nobody else may call it).</summary>
    public async Task CrankAsync(IEnumerable<BigInteger> requestIds, CancellationToken ct)
    {
        foreach (var requestId in requestIds)
        {
            try
            {
                await _gateway.SubmitFinalizeAsync(requestId, ct);
            }
            catch (Exception ex)
            {
                // A concurrent finalize (e.g. by the MM agent) is fine — the contract is
                // idempotent per request; we log and move on.
                System.Console.WriteLine($"rfm: finalize {requestId} submit failed: {ex.Message}");
            }
        }
    }
}
