using Venue.Infrastructure;

namespace Venue.Settlement;

/// <summary>Parsed revert for one failed whole-batch settlement.</summary>
public sealed record BatchRevertInfo(int? FailIndex, string? TradeId, string ErrorName, string Raw);

/// <summary>
/// Parses the raw revert data of CTFExchangeLite.settleBatch. The contract reverts the
/// ENTIRE batch with a custom error naming the failing trade (PLAN_CONTRACTS §2):
/// SettleBatchFailed(uint256 index, bytes32 tradeId). Also recognizes the other
/// batch-level errors for reporting. On any other/unknown revert the caller treats
/// attribution as unclear and falls back to cancel-all + re-cross.
/// </summary>
public static class RevertParser
{
    private static readonly string SettleBatchFailedSelector = SelectorOf("SettleBatchFailed(uint256,bytes32)");
    private static readonly string BatchReusedSelector = SelectorOf("BatchReused(bytes32)");
    private static readonly string BatchTooLargeSelector = SelectorOf("BatchTooLarge(uint256)");
    private static readonly string EmptyBatchSelector = SelectorOf("EmptyBatch()");

    /// <summary>Parse revert data (hex, possibly RPC error data or an eth_call raw result).</summary>
    public static BatchRevertInfo Parse(string revertHex)
    {
        var raw = revertHex ?? string.Empty;
        var h = raw.Trim();
        if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h[2..];
        if (h.Length < 8) return new BatchRevertInfo(null, null, "Unknown", raw);

        var selector = h[..8].ToLowerInvariant();
        var body = h.Length > 8 ? h[8..] : "";

        if (selector == SettleBatchFailedSelector)
        {
            var index = ReadUint256Word(body, 0);
            var tradeId = ReadBytes32Word(body, 1);
            return new BatchRevertInfo(index, tradeId, "SettleBatchFailed", raw);
        }
        if (selector == BatchReusedSelector)
        {
            var batchId = ReadBytes32Word(body, 0);
            return new BatchRevertInfo(null, batchId, "BatchReused", raw);
        }
        if (selector == BatchTooLargeSelector)
        {
            var len = ReadUint256Word(body, 0);
            return new BatchRevertInfo(null, null, $"BatchTooLarge(len={len})", raw);
        }
        if (selector == EmptyBatchSelector)
        {
            return new BatchRevertInfo(null, null, "EmptyBatch", raw);
        }
        return new BatchRevertInfo(null, null, "Unknown", raw);
    }

    /// <summary>Rejections on the happy path: an already-used trade id means the batch
    /// already settled (idempotent replay) — treat as success, never a repair.</summary>
    public static bool IsReplay(BatchRevertInfo revert)
        => revert.ErrorName == "SettleBatchFailed" && revert.FailIndex is null && revert.TradeId is not null;

    private static string SelectorOf(string signature)
    {
        var hash = Hash.KeccakHex(signature);
        return hash[2..10]; // 4 bytes, no "0x" — compared against a stripped revert payload
    }

    private static int? ReadUint256Word(string hexBody, int wordIndex)
    {
        var word = Word(hexBody, wordIndex);
        if (word == null || word.Length != 64) return null;
        // value fits int if the leading 8 hex chars are zero
        var head = word[..8];
        if (head != "00000000") return null;
        return Convert.ToInt32(word, 16);
    }

    private static string? ReadBytes32Word(string hexBody, int wordIndex)
    {
        var word = Word(hexBody, wordIndex);
        return word == null ? null : "0x" + word.ToLowerInvariant();
    }

    private static string? Word(string hexBody, int index)
    {
        if (hexBody.Length < (index + 1) * 64) return null;
        return hexBody.Substring(index * 64, 64);
    }
}
