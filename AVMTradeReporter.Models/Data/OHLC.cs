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
        /// Document id: AssetIdA-AssetIdB-Interval-(asset|usd)-YYYYMMddHHmmss (bucket start utc).
        /// </summary>
        public string Id => $"{AssetIdA}-{AssetIdB}-{Interval}-{(InUSDValuation ? "usd" : "asset")}-{StartTime:yyyyMMddHHmmss}";

        /// <summary>
        /// Asset id of the base asset (always the lower asset id).
        /// </summary>
        public ulong AssetIdA { get; set; }

        /// <summary>
        /// Asset id of the quote asset (always the higher asset id).
        /// </summary>
        public ulong AssetIdB { get; set; }

        /// <summary>
        /// Interval code (1m,5m,15m,1h,4h,1d,1w,1M)
        /// </summary>
        public string Interval { get; set; } = string.Empty;

        /// <summary>
        /// Indicates whether this candle is stored in USD valuation.
        /// If <c>false</c>, the candle is stored in asset valuation (price in quote per base).
        /// When loading documents that do not have this field, they are treated as asset valuation.
        /// </summary>
        public bool InUSDValuation { get; set; }

        /// <summary>
        /// Start time (inclusive) of the bucket (UTC)
        /// </summary>
        public DateTimeOffset StartTime { get; set; }
        /// <summary>
        /// Open price of the bucket (AssetB per AssetA)
        /// </summary>
        public decimal? Open { get; set; }

        /// <summary>
        /// High price of the bucket.
        /// </summary>
        public decimal? High { get; set; }

        /// <summary>
        /// Low price of the bucket.
        /// </summary>
        public decimal? Low { get; set; }

        /// <summary>
        /// Close price of the bucket.
        /// </summary>
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
