using AVMTradeReporter.Model.DTO;

namespace AVMTradeReporter.Services
{
    public interface IAssetTimeseriesService
    {
        /// <summary>
        /// Returns the 7d hourly price + TVL OHLC series for the requested assets, served from the
        /// Redis cache when possible and computed (and cached) on demand otherwise.
        /// </summary>
        Task<List<AssetTimeseries7D>> GetTimeseriesAsync(IReadOnlyCollection<ulong> assetIds, CancellationToken cancellationToken = default);

        /// <summary>
        /// Precomputes and caches the series for the top-TVL asset universe. Returns how many assets were cached.
        /// </summary>
        Task<int> ComputeAndCacheUniverseAsync(CancellationToken cancellationToken = default);
    }
}
