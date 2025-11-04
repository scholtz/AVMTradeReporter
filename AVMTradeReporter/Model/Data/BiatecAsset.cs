using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporter.Model.Data
{
    public class BiatecAsset : Algorand.Algod.Model.Asset
    {
        public AssetType Type { get; set; } = AssetType.ASA;
        public decimal PriceUSD { get; set; }
        /// <summary>
        /// Real TVL in USD: Sum of trusted token values from pools where this asset is paired with a trusted reference token.
        /// Calculated as: Σ(trusted_token_amount × trusted_token_price) for each pool with this asset.
        /// Only counts the trusted token side (e.g., ALGO, USDC, major stablecoins), not the asset side.
        /// This represents the actual liquid value backed by trusted assets.
        /// </summary>
        public decimal TVL_USD { get; set; }
        public DateTimeOffset? Timestamp { get; set; }
        /// <summary>
        /// Total TVL in USD: Sum of both sides (asset + trusted token) from all aggregated pools where this asset is paired with a trusted reference token.
        /// Calculated as: Σ((asset_amount × asset_price) + (trusted_token_amount × trusted_token_price)) for each pool.
        /// This represents the total pool value including both the asset itself and the trusted tokens it's paired with.
        /// </summary>
        public decimal? TotalTVLAssetInUSD { get; set; }
    }
}
