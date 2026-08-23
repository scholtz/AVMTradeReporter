using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.DTO.TVL
{
    /// <summary>
    /// Real-time per-recompute TVL (real total value locked, USD) tick broadcast over SignalR (event
    /// <c>BiatecScanHub.Subscriptions.TVL</c>) to the group <c>BiatecScanHub.TvlGroupName(assetId)</c>.
    /// One tick is sent whenever an asset's real TVL_USD actually changes.
    /// </summary>
    public class TvlTickDto
    {
        [JsonPropertyName("assetId")] public ulong AssetId { get; set; }
        /// <summary>Current real TVL of the asset, in USD.</summary>
        [JsonPropertyName("tvl")] public decimal Tvl { get; set; }
        /// <summary>Tick timestamp, unix seconds (UTC).</summary>
        [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    }

    /// <summary>
    /// TVL OHLC history response for the <c>/api/TVL/history</c> endpoint. Deliberately not
    /// TradingView-UDF-shaped (no resolution/symbol resolution machinery like OHLCController) — this
    /// is a plain bars endpoint for a future liquidity chart consumer; array indices line up (t[i],
    /// o[i], h[i], l[i], c[i]).
    /// </summary>
    public class TvlHistoryResponseDto
    {
        [JsonPropertyName("t")] public List<long> T { get; set; } = new();
        [JsonPropertyName("o")] public List<decimal> O { get; set; } = new();
        [JsonPropertyName("h")] public List<decimal> H { get; set; } = new();
        [JsonPropertyName("l")] public List<decimal> L { get; set; } = new();
        [JsonPropertyName("c")] public List<decimal> C { get; set; } = new();
    }
}
