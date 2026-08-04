namespace AVMTradeReporter.Repository
{
    /// <summary>
    /// Provides raw Elasticsearch aggregations for DEX trading statistics.
    /// </summary>
    public interface IStatsRepository
    {
        /// <summary>
        /// Queries the <c>trades</c> Elasticsearch index for aggregated statistics over the given time window.
        /// Only trades with <c>tradeState = Confirmed</c> and matching <paramref name="protocol"/> are included.
        /// </summary>
        /// <param name="protocol">Protocol name as stored in Elasticsearch (e.g. "Biatec", "Pact", "Tiny").</param>
        /// <param name="from">Window start, inclusive (ISO-8601).</param>
        /// <param name="to">Window end, exclusive (ISO-8601).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// A tuple with four sums sourced directly from Elasticsearch double aggregations:
        /// <c>VolumeUSD</c>, <c>FeesUSD</c>, <c>FeesUSDProvider</c>, <c>FeesUSDProtocol</c>.
        /// All values are zero when Elasticsearch is unavailable or the query fails.
        /// </returns>
        Task<(double VolumeUSD, double FeesUSD, double FeesUSDProvider, double FeesUSDProtocol)> GetDexAggregationsAsync(
            string protocol,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Queries the <c>trades</c> Elasticsearch index for the sum of liquidity-provider fees
        /// (<c>feesUSDProvider</c>), bucketed per pool address (terms aggregation on <c>poolAddress.keyword</c>),
        /// for confirmed trades within [<paramref name="from"/>, <paramref name="to"/>). Used to compute the
        /// trailing 24h/7d fee windows for <see cref="AVMTradeReporter.Models.Data.AssetStat"/> by calling this
        /// method twice with different cutoffs.
        /// </summary>
        /// <param name="from">Window start, inclusive.</param>
        /// <param name="to">Window end, exclusive.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Map of pool address to summed <c>feesUSDProvider</c>. Empty when Elasticsearch is unavailable or the query fails.</returns>
        Task<Dictionary<string, decimal>> GetFeesUSDProviderByPoolAsync(
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken = default);
    }
}
