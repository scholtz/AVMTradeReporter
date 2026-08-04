namespace AVMTradeReporter.Models.Data.Enums
{
    /// <summary>
    /// Server-side ordering options for the pool list endpoints.
    /// </summary>
    public enum PoolOrderBy
    {
        /// <summary>
        /// Order by total value locked in USD (sum of both asset sides).
        /// </summary>
        TVL,
        /// <summary>
        /// Order by 1 hour trading volume in USD.
        /// </summary>
        Volume1H,
        /// <summary>
        /// Order by 24 hours trading volume in USD.
        /// </summary>
        Volume24H,
        /// <summary>
        /// Order by 7 days trading volume in USD.
        /// </summary>
        Volume7D,
        /// <summary>
        /// Order by the last update timestamp.
        /// </summary>
        LastUpdated,
        /// <summary>
        /// Order by the number of pools aggregated into the result (aggregated pools only).
        /// </summary>
        PoolCount
    }
}
