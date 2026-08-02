using System.Numerics;
using Venue.Domain;

namespace Venue.Ledger;

/// <summary>
/// Off-chain balance mirror. Per PLAN_BACKEND §1 this is a BEST-EFFORT cache for
/// admission + display; the on-chain contract is the solvency guard (settleBatch
/// reverts on any shortfall). `available = chainFree - reserved` is the one number
/// used to admit orders.
///
/// REBUILD RULE (both CR seats): chainFree deltas come ONLY from granular events
/// — Deposited(+), Withdrawn(-), Locked(-), LockReleased(+), LockConsumed(+ to),
/// USDCMoved(signed once), PairMinted FREE funding(-), PairBurned credits(+),
/// Vault Redeemed(+). Summary events (BatchSettled, RfmFill, BondSlashed) are NEVER
/// applied — their granular counterparts already moved the same amounts, so applying
/// them double-counts. Reservations are asset-scoped (USDC vs tokenId).
/// </summary>
public sealed class Ledger
{
    private readonly string _vaultAddress;
    private readonly Func<string, Outcome?> _resolvedOutcome;

    private readonly Dictionary<string, BigInteger> _chainFree = new();
    private readonly Dictionary<(string User, string TokenId), BigInteger> _positions = new();
    private readonly Dictionary<(string User, string Asset), BigInteger> _reserved = new();

    public Ledger(string vaultAddress, Func<string, Outcome?> resolvedOutcome)
    {
        _vaultAddress = Domain.Addresses.Normalize(vaultAddress);
        _resolvedOutcome = resolvedOutcome;
    }

    public BigInteger ChainFree(string user)
        => _chainFree.TryGetValue(Norm(user), out var v) ? v : BigInteger.Zero;

    public BigInteger Position(string user, string tokenId)
        => _positions.TryGetValue((Norm(user), NormId(tokenId)), out var v) ? v : BigInteger.Zero;

    public BigInteger Reserved(string user, string asset)
        => _reserved.TryGetValue((Norm(user), NormId(asset)), out var v) ? v : BigInteger.Zero;

    /// <summary>available = chainFree - reserved for USDC; position - reserved for a token.</summary>
    public BigInteger Available(string user, string asset)
    {
        var baseAmount = asset == Assets.Usdc ? ChainFree(user) : Position(user, asset);
        var r = Reserved(user, asset);
        return baseAmount >= r ? baseAmount - r : BigInteger.Zero;
    }

    public void Reserve(string user, string asset, BigInteger amount)
    {
        if (amount <= 0) return;
        var k = (Norm(user), NormId(asset));
        _reserved.TryGetValue(k, out var cur);
        _reserved[k] = cur + amount;
    }

    public void ReleaseReservation(string user, string asset, BigInteger amount)
    {
        if (amount <= 0) return;
        var k = (Norm(user), NormId(asset));
        if (!_reserved.TryGetValue(k, out var cur)) return;
        var next = cur > amount ? cur - amount : BigInteger.Zero;
        if (next.IsZero) _reserved.Remove(k);
        else _reserved[k] = next;
    }

    /// <summary>Rebuild from a full replay (deploy block onward). Applied in order.</summary>
    public void Rebuild(IEnumerable<VenueEvent> events)
    {
        _chainFree.Clear();
        _positions.Clear();
        _reserved.Clear();
        foreach (var e in events) Apply(e);
    }

    /// <summary>Drop all off-chain reservations (restart); balances remain exact.</summary>
    public void ClearReservations() => _reserved.Clear();

    public void Apply(VenueEvent e)
    {
        switch (e)
        {
            case Deposited d: Add(_chainFree, d.User, d.Amt); break;
            case Withdrawn w: Sub(_chainFree, w.User, w.Amt); break;

            case TokensDeposited d: Add(_positions, (d.User, d.TokenId), d.Amt); break;
            case TokensWithdrawn w: Sub(_positions, (w.User, w.TokenId), w.Amt); break;

            case USDCMoved m: Sub(_chainFree, m.From, m.Amt); Add(_chainFree, m.To, m.Amt); break;
            case TokensMoved m: Sub(_positions, (m.From, m.TokenId), m.Amt); Add(_positions, (m.To, m.TokenId), m.Amt); break;

            case Locked l: Sub(_chainFree, l.User, l.Amt); break;
            case LockReleased r: Add(_chainFree, r.User, r.Amt); break;
            case LockConsumed c: Add(_chainFree, c.To, c.Amt); break; // funder neutral (already freed at Locked)

            case PairMinted pm:
                foreach (var f in pm.Funding)
                    if (f.Kind == FundingKind.Free) Sub(_chainFree, f.Account, f.Amount);
                foreach (var a in pm.YesAlloc) Add(_positions, (a.Account, Assets.TokenId(pm.MarketId, Outcome.Yes)), a.Amount);
                foreach (var a in pm.NoAlloc) Add(_positions, (a.Account, Assets.TokenId(pm.MarketId, Outcome.No)), a.Amount);
                break;

            case PairBurned pb:
                Sub(_positions, (pb.YesFrom, Assets.TokenId(pb.MarketId, Outcome.Yes)), pb.Size);
                Sub(_positions, (pb.NoFrom, Assets.TokenId(pb.MarketId, Outcome.No)), pb.Size);
                Add(_chainFree, pb.YesFrom, pb.YesCredit);
                Add(_chainFree, pb.NoFrom, pb.Size - pb.YesCredit);
                break;

            case Redeemed rd when Norm(rd.Contract) == _vaultAddress:
                // Vault-custodied redeem: user gains USDC and loses their winning token.
                Add(_chainFree, rd.User, rd.Amt);
                var win = _resolvedOutcome(rd.MarketId);
                if (win.HasValue)
                    Sub(_positions, (rd.User, Assets.TokenId(rd.MarketId, win.Value)), rd.Amt);
                break;

            // Summary / lifecycle events: NEVER applied to balances (would double-count).
            case BatchSettled:
            case RfmFill:
            case BondSlashed:
            case MarketReserved:
            case RfmMarketReserved:
            case MarketCreated:
            case MarketResolved:
            case RequestPosted:
            case QuoteCommitted:
            case QuoteRevealed:
            case RequestFinalized:
            case RequestFailed:
            case RequestCancelled:
            case MarketBorn:
                break;

            case Redeemed:
                break; // OutcomeTokens redeem pays wallet-held tokens from the pool: not a Vault balance
        }
    }

    private static void Add(Dictionary<string, BigInteger> d, string k, BigInteger amt)
    {
        var key = Norm(k);
        d.TryGetValue(key, out var cur);
        d[key] = cur + amt;
    }

    private static void Add(Dictionary<(string, string), BigInteger> d, (string, string) k, BigInteger amt)
    {
        var key = (Norm(k.Item1), NormId(k.Item2));
        d.TryGetValue(key, out var cur);
        d[key] = cur + amt;
    }

    private static void Sub(Dictionary<string, BigInteger> d, string k, BigInteger amt)
    {
        var key = Norm(k);
        d.TryGetValue(key, out var cur);
        d[key] = cur >= amt ? cur - amt : BigInteger.Zero;
    }

    private static void Sub(Dictionary<(string, string), BigInteger> d, (string, string) k, BigInteger amt)
    {
        var key = (Norm(k.Item1), NormId(k.Item2));
        d.TryGetValue(key, out var cur);
        d[key] = cur >= amt ? cur - amt : BigInteger.Zero;
    }

    private static string Norm(string user) => Domain.Addresses.Normalize(user);
    private static string NormId(string id) => Infrastructure.Hash.NormalizeBytes32(id);
}
