namespace Venue.Infrastructure;

using System.Globalization;
using System.Numerics;
using Nethereum.Util;

/// <summary>
/// Deterministic keccak256 helpers matching the Solidity encoding the contracts use:
/// tokenId = uint256(keccak256(abi.encode(marketId, outcome))), RFM quoteHash =
/// keccak256(abi.encode(chainid, rfm, requestId, mm, tick, size, salt)), and the
/// off-chain tradeId = keccak(marketId || makerOrderId || takerOrderId || fillSeq).
/// </summary>
public static class Hash
{
    /// <summary>keccak256 of the concatenated raw byte payload.</summary>
    public static string KeccakHex(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var buf = new byte[total];
        var off = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, buf, off, p.Length);
            off += p.Length;
        }
        var hash = Sha3Keccack.Current.CalculateHash(buf);
        return BytesToHex(hash);
    }

    /// <summary>keccak256 of an ASCII/UTF8 string (e.g. event signature — not used by the ledger).</summary>
    public static string KeccakHex(string utf8)
    {
        var hash = Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes(utf8));
        return BytesToHex(hash);
    }

    /// <summary>Token id for a market/outcome: keccak256(abi.encode(marketId, outcome)).</summary>
    public static string TokenId(string marketId, Domain.Outcome outcome)
    {
        var m = HexToBytes(marketId);
        return KeccakHex(m, EncodeUint256((uint)outcome));
    }

    /// <summary>RFM quote hash: keccak256(abi.encode(chainId, rfm, requestId, mm, priceTick, size, salt)).</summary>
    public static string QuoteHash(BigInteger chainId, string rfmAddress, BigInteger requestId, string mmAddress, BigInteger priceTick, BigInteger size, BigInteger salt)
    {
        return KeccakHex(
            EncodeUint256(chainId),
            EncodeAddress(rfmAddress),
            EncodeUint256(requestId),
            EncodeAddress(mmAddress),
            EncodeUint256(priceTick),
            EncodeUint256(size),
            EncodeUint256(salt));
    }

    /// <summary>tradeId = keccak256(marketId || makerOrderId || takerOrderId || fillSeq) — deterministic per fill.</summary>
    public static string TradeId(string marketId, string makerOrderId, string takerOrderId, BigInteger fillSeq)
    {
        return KeccakHex(
            HexToBytes(marketId),
            HexToBytes(makerOrderId),
            HexToBytes(takerOrderId),
            EncodeUint256(fillSeq));
    }

    /// <summary>batchId — unique per submission attempt (attempt counter prevents reuse on repair).</summary>
    public static string BatchId(string operatorAddress, BigInteger attempt)
    {
        return KeccakHex(EncodeAddress(operatorAddress), EncodeUint256(attempt), EncodeUint256(UnixNow()));
    }

    private static BigInteger UnixNow()
    {
        var dt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return dt < 0 ? BigInteger.Zero : new BigInteger(dt);
    }

    /// <summary>Solidity abi.encode(uint256) — 32-byte big-endian.</summary>
    public static byte[] EncodeUint256(BigInteger value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "no negative ABI uint256");
        var bytes = value.ToByteArray(); // little-endian, two's complement
        // strip leading zero sign byte
        if (bytes.Length > 1 && bytes[^1] == 0 && (bytes[^2] & 0x80) == 0) Array.Resize(ref bytes, bytes.Length - 1);
        var outb = new byte[32];
        for (var i = 0; i < bytes.Length && i < 32; i++) outb[31 - i] = bytes[i];
        return outb;
    }

    /// <summary>Solidity abi.encode(address) — 20 bytes left-padded into 32 bytes.</summary>
    public static byte[] EncodeAddress(string address)
    {
        var a = Domain.Addresses.Normalize(address);
        var body = HexToBytes(a);
        if (body.Length != 20) throw new ArgumentException($"bad address length {body.Length} for {a}");
        var outb = new byte[32];
        Buffer.BlockCopy(body, 0, outb, 12, 20);
        return outb;
    }

    /// <summary>Hex string ("0x…" or bare) to bytes.</summary>
    public static byte[] HexToBytes(string hex)
    {
        var h = hex.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.Length % 2 != 0) h = "0" + h;
        var outb = new byte[h.Length / 2];
        for (var i = 0; i < outb.Length; i++)
            outb[i] = byte.Parse(h.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return outb;
    }

    /// <summary>Bytes to a lowercase 0x-prefixed hex string (fixed 64 chars for 32-byte values).</summary>
    public static string BytesToHex(byte[] bytes)
    {
        return "0x" + Convert.ToHexStringLower(bytes);
    }

    /// <summary>Normalize a bytes32 hex string to canonical 0x + 64 lowercase hex.</summary>
    public static string NormalizeBytes32(string value)
    {
        var v = value.Trim();
        if (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) v = v[2..];
        if (v.Length > 64) throw new ArgumentException($"bytes32 too long: {value}");
        return "0x" + v.PadLeft(64, '0').ToLowerInvariant();
    }
}
