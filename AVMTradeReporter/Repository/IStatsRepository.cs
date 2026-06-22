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
    }
}
