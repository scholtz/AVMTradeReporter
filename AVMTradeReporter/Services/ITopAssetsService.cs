using AVMTradeReporter.Model.DTO;

namespace AVMTradeReporter.Services
{
    public interface ITopAssetsService
    {
        /// <summary>
        /// Returns the current "top assets" highlight lists. Served from the Redis cache (refreshed every
        /// 5 minutes by <see cref="TopAssetsBackgroundService"/>) whenever possible; falls back to an
        /// on-demand computation only when no cached value exists yet.
        /// </summary>
        Task<TopAssetsResponse> GetTopAssetsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Recomputes the highlight lists from the asset cache, persists an hourly real-TVL snapshot
        /// (used for the 24h TVL change lists) and stores the result in Redis.
        /// </summary>
        Task<TopAssetsResponse> ComputeAndCacheAsync(CancellationToken cancellationToken = default);
    }
}
