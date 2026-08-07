using AVMTradeReporter.Models.Data.Enums;
using System.Text.Json.Serialization;

namespace AVMTradeReporter.Models.Data
{
    /// <summary>
    /// Backend-computed per-asset rollup (TVL, volume, fees, APR) across all pools that trade the asset.
    /// Keyed by (AssetId, Protocol) where <see cref="Protocol"/> == null represents the combined figure
    /// across all protocols (mirrors the existing "protocol" query-param convention on
    /// PoolController/AggregatedPoolController).
    /// </summary>
    public class AssetStat
    {
        /// <summary>
        /// Algorand asset id this stat row is about.
        /// </summary>
        public ulong AssetId { get; set; }

        /// <summary>
        /// Protocol this row is scoped to, or null when the row aggregates across all protocols.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public DEXProtocol? Protocol { get; set; }

        /// <summary>
        /// Identifier for this row, formed by combining AssetId and Protocol ("All" when Protocol is null).
        /// </summary>
        public string Id => $"{AssetId}-{(Protocol.HasValue ? Protocol.Value.ToString() : "All")}";

        /// <summary>
        /// Asset display name, sourced from <see cref="BiatecAsset"/> asset params.
        /// </summary>
        public string? AssetName { get; set; }

        /// <summary>
        /// Asset unit name (ticker), sourced from <see cref="BiatecAsset"/> asset params.
        /// </summary>
        public string? UnitName { get; set; }

        /// <summary>
        /// Number of decimals of the asset, sourced from <see cref="BiatecAsset"/> asset params.
        /// </summary>
        public ulong? Decimals { get; set; }

        /// <summary>
        /// Asset image/metadata URL, sourced from <see cref="BiatecAsset"/> asset params (Url field).
        /// </summary>
        public string? ImageUrl { get; set; }

        /// <summary>
        /// Current USD price of the asset, sourced from <see cref="BiatecAsset"/>.
        /// </summary>
        public decimal? PriceUSD { get; set; }

        /// <summary>
        /// Total value locked in USD, summed across all pools trading this asset (this asset's side only)
        /// for the given protocol scope.
        /// </summary>
        public decimal TVLUSD { get; set; }

        /// <summary>
        /// Total value locked in USD of the paired (other) side of each pool trading this asset,
        /// summed across all pools for the given protocol scope. Together with <see cref="TVLUSD"/>
        /// this gives the full both-sides TVL of the pools this asset participates in.
        /// </summary>
        public decimal TVLOtherUSD { get; set; }

        /// <summary>
        /// 24 hours trading volume in USD, summed across all pools trading this asset.
        /// </summary>
        public decimal Volume24hUSD { get; set; }

        /// <summary>
        /// 7 days trading volume in USD, summed across all pools trading this asset.
        /// </summary>
        public decimal Volume7dUSD { get; set; }

        /// <summary>
        /// 24 hours liquidity-provider fees in USD, summed across all pools trading this asset.
        /// </summary>
        public decimal Fees24hUSD { get; set; }

        /// <summary>
        /// 7 days liquidity-provider fees in USD, summed across all pools trading this asset.
        /// </summary>
        public decimal Fees7dUSD { get; set; }

        /// <summary>
        /// Annualized percentage yield derived from the trailing 24h fees. See <see cref="ComputeApr"/>.
        /// </summary>
        public decimal Apr24h { get; set; }

        /// <summary>
        /// Annualized percentage yield derived from the trailing 7d fees. See <see cref="ComputeApr"/>.
        /// </summary>
        public decimal Apr7d { get; set; }

        /// <summary>
        /// Number of pools that contributed to this rollup.
        /// </summary>
        public int PoolCount { get; set; }

        /// <summary>
        /// Timestamp this row was last (re)computed.
        /// </summary>
        public DateTimeOffset LastUpdated { get; set; }

        /// <summary>
        /// Computes the annualized percentage yield (APR) that liquidity providers of this asset's pools
        /// would earn based on trailing fees, projected against the current TVL.
        ///
        /// Load-bearing business rule - formula:
        ///   Apr24h = TVLUSD > 0 ? Fees24hUSD * 365   / TVLUSD * 100 : 0
        ///   Apr7d  = TVLUSD > 0 ? Fees7dUSD  * 365/7 / TVLUSD * 100 : 0
        ///
        /// i.e. the trailing fee window is annualized (24h fees * 365, 7d fees * 365/7) and expressed as a
        /// percentage of the current TVL. When TVL is zero or negative, APR is defined as zero rather than
        /// undefined/infinite.
        /// </summary>
        /// <param name="tvlUsd">Current total value locked in USD.</param>
        /// <param name="fees24hUsd">Trailing 24h liquidity-provider fees in USD.</param>
        /// <param name="fees7dUsd">Trailing 7d liquidity-provider fees in USD.</param>
        /// <returns>Tuple of (Apr24h, Apr7d) expressed as percentages (e.g. 12.5 == 12.5%).</returns>
        public static (decimal Apr24h, decimal Apr7d) ComputeApr(decimal tvlUsd, decimal fees24hUsd, decimal fees7dUsd)
        {
            if (tvlUsd <= 0) return (0m, 0m);
            var apr24h = fees24hUsd * 365m / tvlUsd * 100m;
            var apr7d = fees7dUsd * (365m / 7m) / tvlUsd * 100m;
            return (apr24h, apr7d);
        }
    }
}
