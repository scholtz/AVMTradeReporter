using System;

namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Represents TVL (real total value locked, in USD) OHLC candlestick data for a single asset and
    /// interval bucket. Mirrors <see cref="OHLC"/> but for a single asset's USD value over time
    /// instead of a traded asset pair - there is no base/quote pair, no volume, no trade count.
    /// </summary>
    public class TvlOhlc
    {
        /// <summary>
        /// Document id: AssetId-Interval-YYYYMMddHHmmss (bucket start utc).
        /// </summary>
        public string Id => $"{AssetId}-{Interval}-{StartTime:yyyyMMddHHmmss}";

        /// <summary>
        /// Asset id whose real TVL (USD) this candle tracks.
        /// </summary>
        public ulong AssetId { get; set; }

        /// <summary>
        /// Interval code (1m,5m,15m,1h,4h,1d,1w,1M)
        /// </summary>
        public string Interval { get; set; } = string.Empty;

        /// <summary>
        /// Start time (inclusive) of the bucket (UTC)
        /// </summary>
        public DateTimeOffset StartTime { get; set; }

        /// <summary>
        /// Open TVL (USD) of the bucket.
        /// </summary>
        public decimal? Open { get; set; }

        /// <summary>
        /// High TVL (USD) of the bucket.
        /// </summary>
        public decimal? High { get; set; }

        /// <summary>
        /// Low TVL (USD) of the bucket.
        /// </summary>
        public decimal? Low { get; set; }

        /// <summary>
        /// Close TVL (USD) of the bucket.
        /// </summary>
        public decimal? Close { get; set; }

        /// <summary>
        /// When this bucket was last updated.
        /// </summary>
        public DateTimeOffset? LastUpdated { get; set; }
    }
}
