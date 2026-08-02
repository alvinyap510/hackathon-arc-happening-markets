using System.Numerics;
using Venue.Chain;
using Venue.Domain;
using Nethereum.Hex.HexTypes;
using Nethereum.RPC.Eth.DTOs;
using Xunit;

namespace Venue.Core.Tests;

/// <summary>
/// EventDecoder regression tests against raw log data. Found by the E2E harness: a
/// TokensMoved event whose ERC-1155 token id has its top bit set made Nethereum's
/// uint256 BigInteger decode produce a >256-bit scrambled value, and the indexer
/// wedged on it (NormalizeBytes32 "too long"). The token id must be read verbatim
/// from the first data word, never via the BigInteger.
/// </summary>
public class EventDecoderTests
{
    private static EventDecoder NewDecoder() => new(TestData.Cfg);

    private static FilterLog TokensMovedLog(string rawId)
    {
        var sig = Nethereum.Util.Sha3Keccack.Current.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes("TokensMoved(address,address,uint256,uint256,bytes32)"));
        return new FilterLog
        {
            Address = TestData.Vault,
            BlockNumber = new HexBigInteger(1),
            LogIndex = new HexBigInteger(0),
            TransactionHash = "0xabc",
            Topics = new object[]
            {
                "0x" + Convert.ToHexStringLower(sig),
                "0x00000000000000000000000000000000000000000000000000000000000000a1", // from (alice)
                "0x00000000000000000000000000000000000000000000000000000000000000b2", // to (bob)
                "0x2cedb36ae8a539b67fe647ccbb2dda53e437077c94f5ccf10c8d0d7dc57c2340", // tradeId
            },
            Data = "0x" + rawId.PadLeft(64, '0') + new string('0', 64), // id, amt
        };
    }

    [Fact]
    public void TokensMoved_HighBitTokenId_DecodesVerbatim()
    {
        // A keccak-derived token id whose leading byte is 0xe4 (sign bit set): the exact
        // value that scrambled Nethereum's BigInteger and wedged the real indexer.
        const string rawId = "e41dbd8299f258af089710408d4e1fa782acb78adc7ab2f4e6a881a134a58991";
        var e = NewDecoder().Decode(TokensMovedLog(rawId));

        var moved = Assert.IsType<TokensMoved>(e);
        Assert.Equal("0x" + rawId, moved.TokenId);
    }

    [Fact]
    public void TokensMoved_LowBitTokenId_DecodesVerbatim()
    {
        const string rawId = "0000000000000000000000000000000000000000000000000000000000000005";
        var e = NewDecoder().Decode(TokensMovedLog(rawId));

        var moved = Assert.IsType<TokensMoved>(e);
        Assert.Equal("0x" + rawId, moved.TokenId);
    }

    [Fact]
    public void Deposited_Decodes()
    {
        var sig = Nethereum.Util.Sha3Keccack.Current.CalculateHash(
            System.Text.Encoding.UTF8.GetBytes("Deposited(address,uint256)"));
        var log = new FilterLog
        {
            Address = TestData.Vault,
            BlockNumber = new HexBigInteger(1),
            LogIndex = new HexBigInteger(0),
            TransactionHash = "0xabc",
            Topics = new object[]
            {
                "0x" + Convert.ToHexStringLower(sig),
                "0x00000000000000000000000000000000000000000000000000000000000000a1",
            },
            Data = "0x" + new BigInteger(1_000_000).ToString("x64"),
        };
        var e = Assert.IsType<Deposited>(NewDecoder().Decode(log));
        Assert.Equal(TestData.Alice, e.User);
        Assert.Equal(new BigInteger(1_000_000), e.Amt);
    }
}
