using AVMTradeReporter.Model.Data;
using AVMTradeReporter.Models.Data;
using AVMTradeReporter.Models.Data.Enums;
using AVMTradeReporter.Processors.Pool;
using Microsoft.AspNetCore.Mvc;

namespace AVMTradeReporter.Repository
{
    public interface IPoolRepository
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<Pool?> GetPoolAsync(string poolAddress, CancellationToken cancellationToken);

        /// <summary>
        /// The cached pool exactly as it is, or null when the cache does not hold it. Unlike <see cref="GetPoolAsync"/> this
        /// neither enriches the pool nor hides one whose asset decimals are not resolved yet - callers that resolve
        /// decimals themselves (the CoinGecko pair snapshot) need the raw entry to tell "unresolved" from "unknown".
        /// </summary>
        Pool? GetCachedPool(string poolAddress);
        Task<bool> StorePoolAsync(Pool pool, bool updateAggregated = true, CancellationToken? cancellationToken = null);
        Task UpdatePoolFromTrade(Trade trade, CancellationToken cancellationToken);
        Task UpdatePoolFromLiquidity(Liquidity liquidity, CancellationToken cancellationToken);
        Task<List<Pool>> GetPoolsAsync(ulong? assetIdA, ulong? assetIdB, string? address, DEXProtocol? protocol = null, int size = 100, PoolOrderBy? orderBy = null, SortDirection direction = SortDirection.Desc, CancellationToken cancellationToken = default);
        Task<int> GetPoolCountAsync(CancellationToken cancellationToken = default);
        IPoolProcessor? GetPoolProcessor(DEXProtocol protocol);
        Task UpdateAggregatedPool(ulong aId, ulong bId, CancellationToken cancellationToken);
        /// <summary>
        /// Snapshot of every pool address currently cached, regardless of trading activity - used
        /// by <see cref="Services.VolumeUpdateBackgroundService"/>'s periodic full sweep so a pool
        /// that has gone quiet (no new trades to trigger the fast incremental path) still gets its
        /// Volume1H/24H/7D re-derived from real trailing-window trade data instead of staying frozen.
        /// </summary>
        IEnumerable<string> GetAllPoolAddresses();

        /// <summary>
        /// True once <see cref="InitializeAsync"/> loaded the cache successfully. An initialised repository that holds no pool
        /// is a deployment without pools (a network the DEX is not on yet); an uninitialised one is a pod that failed to load
        /// or is still warming - the two must not be confused (the latter is retryable, the former is a plain "nothing here").
        /// </summary>
        bool IsInitialized { get; }
    }
}