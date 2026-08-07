using System.Text.Json.Serialization;

namespace AVMTradeReporter.Model.DTO
{
    /// <summary>
    /// Compact OHLC candle series in TradingView-style parallel arrays (all arrays share the same
    /// length and index i describes one candle). Chosen over an array of candle objects to keep the
    /// JSON payload small — the 7d asset timeseries endpoint returns up to ~168 hourly candles per
    /// series for up to 100 assets per request.
    /// </summary>
    public class TimeseriesCandles
    {
        /// <summary>Bucket start times (unix seconds, UTC, ascending).</summary>
        [JsonPropertyName("t")]
        public List<long> T { get; set; } = new();
        /// <summary>Open values.</summary>
        [JsonPropertyName("o")]
        public List<decimal> O { get; set; } = new();
        /// <summary>High values.</summary>
        [JsonPropertyName("h")]
        public List<decimal> H { get; set; } = new();
        /// <summary>Low values.</summary>
        [JsonPropertyName("l")]
        public List<decimal> L { get; set; } = new();
        /// <summary>Close values.</summary>
        [JsonPropertyName("c")]
        public List<decimal> C { get; set; } = new();
    }

    /// <summary>
    /// 7 day hourly history for one asset: USD price OHLC candles (from trade OHLC aggregates) and
    /// real TVL OHLC candles (from the hourly Redis TVL snapshots). Cached in Redis and refreshed on
    /// a 1 hour cadence, so consumers may treat it as immutable within the hour.
    /// </summary>
    public class AssetTimeseries7D
    {
        /// <summary>ASA id (0 = ALGO).</summary>
        public ulong AssetId { get; set; }

        /// <summary>Hourly USD price OHLC candles for the last 7 days. Empty when the asset had no priced trades.</summary>
        public TimeseriesCandles Price { get; set; } = new();

        /// <summary>Hourly real TVL (USD) OHLC candles for the last 7 days. Grows as snapshots accumulate.</summary>
        public TimeseriesCandles Tvl { get; set; } = new();

        /// <summary>When this series was computed.</summary>
        public DateTimeOffset GeneratedAt { get; set; }
    }
}
