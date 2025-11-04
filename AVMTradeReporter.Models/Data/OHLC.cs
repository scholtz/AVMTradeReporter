using System;
using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Represents OHLC (Open/High/Low/Close) candlestick data for a specific asset pair and interval bucket.
    /// All pairs are stored in canonical order (AssetIdA &lt; AssetIdB). Volumes are expressed in the real on-chain units (raw integer amounts).
    /// Price = VolumeQuote / VolumeBase (i.e. AssetIdB per AssetIdA).
    /// </summary>
    public class OHLC
    {
        /// <summary>
        /// Document id: AssetIdA-AssetIdB-Interval-YYYYMMddHHmmss (bucket start utc)
        /// </summary>
        public string Id => $"{AssetIdA}-{AssetIdB}-{Interval}-{StartTime:yyyyMMddHHmmss}";
        public ulong AssetIdA { get; set; }
        public ulong AssetIdB { get; set; }
        /// <summary>
        /// Interval code (1m,5m,15m,1h,4h,1d,1w,1M)
        /// </summary>
        public string Interval { get; set; } = string.Empty;
        /// <summary>
        /// Start time (inclusive) of the bucket (UTC)
        /// </summary>
        public DateTimeOffset StartTime { get; set; }
        /// <summary>
        /// Open price of the bucket (AssetB per AssetA)
        /// </summary>
        public decimal? Open { get; set; }
        public decimal? High { get; set; }
        public decimal? Low { get; set; }
        public decimal? Close { get; set; }
        /// <summary>
        /// Total traded amount for base asset (AssetIdA)
        /// </summary>
        public decimal? VolumeBase { get; set; }
        /// <summary>
        /// Total traded amount for quote asset (AssetIdB)
        /// </summary>
        public decimal? VolumeQuote { get; set; }
        /// <summary>
        /// Trade count aggregated into bucket
        /// </summary>
        public long? Trades { get; set; }
        /// <summary>
        /// When this bucket was last updated
        /// </summary>
        public DateTimeOffset? LastUpdated { get; set; }
    }
}
