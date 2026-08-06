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

        /// <summary>Trading volume in USD over the past 24 hours.</summary>
        public decimal Volume24HUSD { get; set; }

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
    /// The candidate universe for every list is: top 150 assets ordered by real TVL (BiatecAsset.TVL_USD),
    /// with stable assets (StabilityIndex &gt; 0) excluded. Each list contains up to 3 entries.
    /// The response is recomputed and cached in Redis every 5 minutes (see TopAssetsBackgroundService),
    /// so it is served mostly from cache.
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

        /// <summary>Top assets by absolute real TVL growth in USD over 24h ("Top value gainers"). Only positive changes qualify.</summary>
        public List<TopAssetItem> TopValueGainers { get; set; } = new();

        /// <summary>Top assets by absolute real TVL loss in USD over 24h ("Top value losers"). Only negative changes qualify.</summary>
        public List<TopAssetItem> TopValueLosers { get; set; } = new();

        /// <summary>Timestamp this response was computed.</summary>
        public DateTimeOffset GeneratedAt { get; set; }
    }
}
