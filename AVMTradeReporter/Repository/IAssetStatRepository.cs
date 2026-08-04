using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;

namespace AVMTradeReporter.Repository
{
    public interface IAssetStatRepository
    {
        Task UpsertAsync(AssetStat stat, CancellationToken cancellationToken = default);
        Task UpsertManyAsync(IEnumerable<AssetStat> stats, CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns all cached asset stat rows, optionally filtered by protocol scope and sorted.
        /// </summary>
        /// <param name="protocol">Optional protocol filter. Pass null to keep all rows (both the "all protocols"
        /// rows and the per-protocol rows); pass a specific protocol to only return rows scoped to it.</param>
        /// <param name="sortBy">Optional sort field: TVLUSD, Volume24hUSD, Volume7dUSD, Apr24h, Apr7d.</param>
        /// <param name="desc">Sort direction; true for descending (default).</param>
        IEnumerable<AssetStat> GetAllAsync(DEXProtocol? protocol = null, string? sortBy = null, bool desc = true);

        /// <summary>
        /// Retrieves the stat row for a given asset. When <paramref name="protocol"/> is null, returns the
        /// combined ("all protocols") row for the asset.
        /// </summary>
        AssetStat? GetByAssetIdAsync(ulong assetId, DEXProtocol? protocol = null);
    }
}
