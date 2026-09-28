using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Model.DTO;
using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Services
{
    public interface ITradeQueryService
    {
        Task<IEnumerable<Trade>> GetTradesAsync(
            ulong? assetIdIn = null,
            ulong? assetIdOut = null,
            string? txId = null,
            int offset = 0,
            int size = 100,
            CancellationToken cancellationToken = default);

        Task<PagedResult<Trade>> GetTradesAsync(TradeFilter filter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Computes windowed 1H/24H/7D pool volumes directly from confirmed trades. Returns null
        /// when Elasticsearch is unavailable or any window's query fails, so callers can skip the
        /// update instead of mistaking a failed query for pools that genuinely have zero volume.
        /// </summary>
        Task<Dictionary<string, (decimal Volume1H, decimal Volume24H, decimal Volume7D)>?> GetPoolVolumesAsync(IEnumerable<string> poolAddresses, CancellationToken cancellationToken = default);

        Task<IReadOnlyDictionary<ulong, AssetVolumeWindows>?> GetAssetVolumeWindowsAsync(DateTimeOffset now, CancellationToken cancellationToken = default);
    }
}