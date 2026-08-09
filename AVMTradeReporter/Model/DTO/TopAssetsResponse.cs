namespace AVMTradeReporter.Model.DTO
{
    /// <summary>
    /// One asset entry inside <see cref="TopAssetsResponse"/> lists.
    /// </summary>
    public class TopAssetItem
    {
        /// <summary>Algorand asset id.</summary>
        public ulong AssetId { get; set; }

        /// <summary>Asset display name.</summary>
        public string? Name { get; set; }

        /// <summary>Asset unit name (ticker).</summary>
        public string? UnitName { get; set; }

        /// <summary>Number of decimals of the asset.</summary>
        public ulong? Decimals { get; set; }

        /// <summary>Current USD price of the asset.</summary>
        public decimal PriceUSD { get; set; }

        /// <summary>USD price 24 hours ago (null when no history is available yet).</summary>
        public decimal? PriceUSD24H { get; set; }

        /// <summary>Price change over the past 24 hours in percent (null when no price history).</summary>
        public decimal? PriceChange24HPercent { get; set; }

        /// <summary>Trading volume in USD over the past hour.</summary>
        public decimal Volume1HUSD { get; set; }

        /// <summary>Trading volume in USD over the previous hour window (2h ago .. 1h ago). Null when trade history was unavailable during computation.</summary>
        public decimal? Volume1HUSDPrev { get; set; }

        /// <summary>Volume change of the past-hour window vs the previous-hour window in percent (null when the previous window had no volume).</summary>
        public decimal? Volume1HChangePercent { get; set; }

        /// <summary>Trading volume in USD over the past 24 hours.</summary>
        public decimal Volume24HUSD { get; set; }

        /// <summary>Trading volume in USD over the previous 24h window (48h ago .. 24h ago). Null when trade history was unavailable during computation.</summary>
        public decimal? Volume24HUSDPrev { get; set; }

        /// <summary>Volume change of the past-24h window vs the previous-24h window in percent (null when the previous window had no volume).</summary>
        public decimal? Volume24HChangePercent { get; set; }

        /// <summary>Current real TVL in USD (trusted-token side only, see BiatecAsset.TVL_USD).</summary>
        public decimal RealTVLUSD { get; set; }

        /// <summary>Real TVL in USD ~24 hours ago (null when no snapshot history is available yet).</summary>
        public decimal? RealTVLUSD24H { get; set; }

        /// <summary>Absolute real TVL change over the past 24 hours in USD (null when no snapshot history).</summary>
        public decimal? TVLChange24HUSD { get; set; }

        /// <summary>Real TVL change over the past 24 hours in percent (null when no snapshot history).</summary>
        public decimal? TVLChange24HPercent { get; set; }
    }

    /// <summary>
    /// Backend-computed "top assets" highlight lists shown at the top of the Biatec Scan UI.
    /// The candidate universe for every list is the top 150 assets ordered by real TVL (BiatecAsset.TVL_USD).
    /// Every asset competes in every list, including stable/base reference assets (ALGO, USDC, ...).
    /// Each list contains up to 3 entries. The response is recomputed and cached in Redis every 5 minutes
    /// (see TopAssetsBackgroundService), so it is served mostly from cache.
    /// </summary>
    public class TopAssetsResponse
    {
        /// <summary>Top assets by 24h trading volume ("Popular assets").</summary>
        public List<TopAssetItem> Popular { get; set; } = new();

        /// <summary>Top assets by 1h trading volume ("Trending assets").</summary>
        public List<TopAssetItem> Trending { get; set; } = new();

        /// <summary>Top assets by 24h price growth in percent ("Top gainers"). Only positive changes qualify.</summary>
        public List<TopAssetItem> TopGainers { get; set; } = new();

        /// <summary>Top assets by 24h price loss in percent ("Top losers"). Only negative changes qualify.</summary>
        public List<TopAssetItem> TopLosers { get; set; } = new();

        /// <summary>Top assets by relative real TVL growth in percent over 24h ("Top liquidity gainers"). Only positive changes qualify.
        /// Newly funded assets (no TVL 24h ago) count as infinite relative growth: they rank first, ordered by absolute USD gain, with TVLChange24HPercent null.</summary>
        public List<TopAssetItem> TopValueGainers { get; set; } = new();

        /// <summary>Top assets by relative real TVL loss in percent over 24h ("Top liquidity losers"). Only negative changes qualify.</summary>
        public List<TopAssetItem> TopValueLosers { get; set; } = new();

        /// <summary>Timestamp this response was computed.</summary>
        public DateTimeOffset GeneratedAt { get; set; }
    }
}
