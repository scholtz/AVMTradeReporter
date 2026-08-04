using AVMTradeReporter.Models.Data;

namespace AVMTradeReporter.Services
{
    public interface IAssetStatsService
    {
        /// <summary>
        /// Recomputes TVL/volume/fees/APR for every asset that appears in at least one pool, both combined
        /// across all protocols and per-protocol, and upserts the results into <see cref="Repository.IAssetStatRepository"/>.
        /// </summary>
        /// <returns>All computed <see cref="AssetStat"/> rows (combined + per-protocol) for this run.</returns>
        Task<List<AssetStat>> ComputeAssetStatsAsync(CancellationToken cancellationToken = default);
    }
}
